using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

public sealed record MoveInFolderRequest(string? User, string? Folder, long? Bytes, bool? On);
public sealed record MoveInLookRequest(string? Root, string? Os, MoveInFolderRequest?[]? Folders);
public sealed record MoveInBookmarksRequest(string? User);

public sealed record MoveInFolder(string User, string Folder, long Bytes, bool On);
public sealed record MoveInBookmarkSource(string User, string Browser, int Count);
// AppId is a Flathub id to offer, or null when NexusOS already has the app (Already names it).
public sealed record WindowsProgramRequest(string? FileName);
public sealed record MoveInApp(string Was, string? AppId, string Name, bool Same, string? Already = null);
public sealed record MoveInReport(string Root, string Os, IReadOnlyList<string> Users, IReadOnlyList<MoveInFolder> Folders,
    IReadOnlyList<MoveInBookmarkSource> Bookmarks, IReadOnlyList<string> OldApps, IReadOnlyList<MoveInApp> Apps,
    IReadOnlyList<string> Notes, DateTimeOffset LookedAt)
{
    public IEnumerable<MoveInApp> ToGet => Apps.Where(a => a.AppId is not null);
}
public sealed record MoveInBookmark(string Title, string Url, IReadOnlyList<string> Folders);
public sealed record MoveInSaved(string Path, int Count, IReadOnlyList<string> Browsers);

public sealed class MoveInException(string message) : Exception(message);

// Move in, the parts Core does (the shell opens the drive and copies folders
// with lib/movein.sh): on the old drive the shell opened, it finds each
// person's browser bookmarks and the programs that were installed, matches
// those to Flathub apps, and saves the bookmarks as a file Firefox imports.
// The drive is not trusted, so:
//  - nothing on it is written: Firefox's places.sqlite is copied (size
//    capped) into Core's own temporary folder and only the copy is opened;
//  - paths are fixed (Users/<name>/AppData/..., Library/Application Support/
//    ...); a symlink anywhere on the way is not followed, and only plain,
//    non-empty files under a size cap are read (a FIFO or device is empty);
//  - only bookmarks are read: never passwords, cookies, history or forms;
//  - a bookmark keeps only an http or https address (javascript:, data:,
//    file: and the like are dropped) and a cleaned title, and the file is
//    written with every text HTML-escaped and a policy that runs no script;
//  - names (users, programs) are checked and cut short; app suggestions only
//    ever name ids from the fixed list below.
// The bookmarks file goes to ~/Documents with a new name each time; nothing
// in the person's Firefox is replaced: Firefox's "Import Bookmarks from HTML"
// adds them in a folder of their own.
public sealed partial class MoveInService(IConfiguration config, ActivityJournal journal, NexusStatePaths paths, TimeProvider time)
{
    public const long MaxPlacesBytes = 256L << 20;
    public const long MaxChromeBytes = 32L << 20;
    public const int MaxBookmarks = 20_000;
    public const int MaxUsers = 24;
    public const int MaxTitle = 300;
    public const int MaxUrl = 4096;
    public const int MaxFolderDepth = 12;
    public const int MaxNames = 600;
    private static readonly string[] SkippedUsers = ["Public", "Default", "Default User", "All Users", "defaultuser0", "Shared", "Guest", "lost+found"];

    private readonly object _gate = new();
    private MoveInReport? _current;

    public MoveInReport? Current { get { lock (_gate) return _current; } }
    public void Forget() { lock (_gate) _current = null; }

    private string Home => Path.TrimEndingDirectorySeparator(Path.GetFullPath(config["NEXUS_MOVEIN_HOME"] is { Length: > 0 } h
        ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    // ---- looking ----

    public MoveInReport Look(MoveInLookRequest request)
    {
        var root = request.Root?.Trim() ?? "";
        if (root.Length is 0 or > 4096 || !Path.IsPathFullyQualified(root) || root.Any(char.IsControl) || root.Split('/').Contains(".."))
            throw new MoveInException("Give the folder the old files are in, as a full path.");
        if (!Directory.Exists(root)) throw new MoveInException("That folder is not there. Is the old drive still open?");
        var volume = VolumeOf(root);
        var users = Users(volume);
        var folders = (request.Folders ?? []).Where(f => f is not null && ValidUser(f.User ?? "") && MoveInFolders.Contains(f.Folder))
            .Take(MaxUsers * MoveInFolders.Length)
            .Select(f => new MoveInFolder(f!.User ?? "", f.Folder!, Math.Max(0, f.Bytes ?? 0), f.On ?? true)).ToArray();
        var os = request.Os is "Windows" or "Mac" or "Linux" ? request.Os : "";
        var bookmarks = new List<MoveInBookmarkSource>();
        var notes = new List<string>();
        foreach (var user in users)
            foreach (var (browser, marks) in ReadBookmarks(volume, user, notes))
                if (marks.Count > 0) bookmarks.Add(new MoveInBookmarkSource(user, browser, marks.Count));
        var oldApps = InstalledPrograms(volume, users);
        var report = new MoveInReport(root, os, users, folders, bookmarks, oldApps, MoveInApps.Match(oldApps),
            notes.Distinct().ToArray(), time.GetUtcNow());
        lock (_gate) _current = report;
        return report;
    }

    internal static readonly string[] MoveInFolders = ["Documents", "Desktop", "Downloads", "Pictures", "Music", "Videos", "Movies"];

    // As lib/movein.sh: one plain path component.
    public static bool ValidUser(string name) =>
        name.Length <= 255 && !name.StartsWith('.') && !name.Contains('/') && !name.Any(char.IsControl);

    // As lib/movein.sh: ROOT, or ROOT/root for an apfs-fuse mount.
    internal static string VolumeOf(string root) =>
        IsRealDir(Path.Combine(root, "root")) && Directory.Exists(Path.Combine(root, "private-dir")) ? Path.Combine(root, "root") : root;

    private static string? PeopleOf(string volume) =>
        IsRealDir(Path.Combine(volume, "Users")) ? "Users" : IsRealDir(Path.Combine(volume, "home")) ? "home" : null;

    private static bool IsRealDir(string path)
    {
        try { var d = new DirectoryInfo(path); return d.Exists && d.LinkTarget is null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static readonly EnumerationOptions Plain = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true, RecurseSubdirectories = false,
        ReturnSpecialDirectories = false, MatchCasing = MatchCasing.CaseInsensitive
    };

    private static IEnumerable<string> Names(string dir, Func<FileSystemInfo, bool> keep)
    {
        if (!IsRealDir(dir)) yield break;
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Plain).Take(MaxNames * 4).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var e in entries)
            if (e.LinkTarget is null && keep(e)) yield return e.Name;
    }

    private static IReadOnlyList<string> Users(string volume)
    {
        if (PeopleOf(volume) is not { } people) return [];
        return Names(Path.Combine(volume, people), e => e is DirectoryInfo)
            .Where(n => ValidUser(n) && !SkippedUsers.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).Take(MaxUsers).ToArray();
    }

    // A path of fixed parts under the volume, with no symlink on the way; null if any part is missing.
    private static string? Safe(string volume, params string[] parts)
    {
        var path = volume;
        foreach (var part in parts)
        {
            path = Path.Combine(path, part);
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null) return null;
        }
        return path;
    }

    private static string? PlainFile(string? path, long max) =>
        path is not null && new FileInfo(path) is { Exists: true, LinkTarget: null, Length: > 0 } f && f.Length <= max ? path : null;

    // ---- bookmarks ----

    // Where each browser keeps a person's profiles, on Windows, macOS and Linux.
    private static IEnumerable<(string Browser, bool Firefox, string[] Parts)> BrowserHomes(string people, string user)
    {
        string[] W(params string[] p) => [people, user, "AppData", .. p];
        string[] M(params string[] p) => [people, user, "Library", "Application Support", .. p];
        string[] L(params string[] p) => [people, user, .. p];
        yield return ("Firefox", true, W("Roaming", "Mozilla", "Firefox", "Profiles"));
        yield return ("Firefox", true, M("Firefox", "Profiles"));
        yield return ("Firefox", true, L(".mozilla", "firefox"));
        yield return ("Chrome", false, W("Local", "Google", "Chrome", "User Data"));
        yield return ("Chrome", false, M("Google", "Chrome"));
        yield return ("Chrome", false, L(".config", "google-chrome"));
        yield return ("Edge", false, W("Local", "Microsoft", "Edge", "User Data"));
        yield return ("Edge", false, M("Microsoft Edge"));
        yield return ("Edge", false, L(".config", "microsoft-edge"));
        yield return ("Brave", false, W("Local", "BraveSoftware", "Brave-Browser", "User Data"));
        yield return ("Brave", false, M("BraveSoftware", "Brave-Browser"));
        yield return ("Brave", false, L(".config", "BraveSoftware", "Brave-Browser"));
        yield return ("Chromium", false, L(".config", "chromium"));
    }

    // Every browser's bookmarks for one person on the old drive, by browser.
    public IReadOnlyList<(string Browser, IReadOnlyList<MoveInBookmark> Bookmarks)> ReadBookmarks(string volume, string user, List<string>? notes = null)
    {
        var result = new List<(string, IReadOnlyList<MoveInBookmark>)>();
        if (!ValidUser(user) || PeopleOf(volume) is not { } people) return result;
        foreach (var group in BrowserHomes(people, user).GroupBy(b => b.Browser))
        {
            var marks = new List<MoveInBookmark>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (_, firefox, parts) in group)
            {
                if (Safe(volume, parts) is not { } home) continue;
                foreach (var profile in Names(home, e => e is DirectoryInfo).Order(StringComparer.Ordinal).Take(20))
                {
                    var file = firefox ? PlainFile(Safe(home, profile, "places.sqlite"), MaxPlacesBytes)
                                       : PlainFile(Safe(home, profile, "Bookmarks"), MaxChromeBytes);
                    if (file is null)
                    {
                        if (firefox && Safe(home, profile, "places.sqlite") is { } big && new FileInfo(big).Length > MaxPlacesBytes)
                            notes?.Add($"{user}'s Firefox bookmarks file is too large to read.");
                        else if (!firefox && Safe(home, profile, "Bookmarks") is { } bigger && new FileInfo(bigger).Length > MaxChromeBytes)
                            notes?.Add($"{user}'s {group.Key} bookmarks file is too large to read.");
                        continue;
                    }
                    try
                    {
                        foreach (var b in firefox ? ReadPlaces(file) : ReadChrome(file))
                            if (marks.Count < MaxBookmarks && seen.Add(string.Join('\u001f', b.Folders) + '\u001e' + b.Url)) marks.Add(b);
                    }
                    catch (Exception ex) when (ex is SqliteException or JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
                    {
                        notes?.Add($"Some of {user}'s {group.Key} bookmarks could not be read.");
                    }
                }
            }
            result.Add((group.Key, marks));
        }
        // Safari keeps its bookmarks in a binary list Nexus does not read.
        if (Safe(volume, people, user, "Library", "Safari", "Bookmarks.plist") is not null)
            notes?.Add($"{user} used Safari: on the Mac, choose File, Export Bookmarks in Safari, then import that file in Firefox.");
        return result;
    }

    // Firefox: a copy of places.sqlite (and its write-ahead log) in Core's own folder; the drive is never opened for writing.
    internal IEnumerable<MoveInBookmark> ReadPlaces(string file)
    {
        var dir = Directory.CreateDirectory(Path.Combine(paths.Root, "movein-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var copy = Path.Combine(dir, "places.sqlite");
            CopyCapped(file, copy, MaxPlacesBytes);
            if (PlainFile(file + "-wal", MaxPlacesBytes) is { } wal) CopyCapped(wal, copy + "-wal", MaxPlacesBytes);
            var marks = new List<MoveInBookmark>();
            var cs = new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
            using (var db = new SqliteConnection(cs))
            {
                db.Open();
                Exec(db, "PRAGMA trusted_schema=OFF; PRAGMA cell_size_check=ON;");
                // Real tables only: a crafted file could make them views.
                using (var check = db.CreateCommand())
                {
                    check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('moz_bookmarks','moz_places')";
                    if (Convert.ToInt64(check.ExecuteScalar()) != 2) throw new FormatException("not a Firefox places file");
                }
                Exec(db, "PRAGMA query_only=ON;");
                var folders = new Dictionary<long, (long Parent, string Title, string Guid)>();
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = "SELECT id, parent, title, guid FROM moz_bookmarks WHERE type=2 LIMIT 20000";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        folders[r.GetInt64(0)] = (r.IsDBNull(1) ? 0 : r.GetInt64(1), r.IsDBNull(2) ? "" : Text(r.GetValue(2)), r.IsDBNull(3) ? "" : Text(r.GetValue(3)));
                }
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = $"SELECT b.title, p.url, b.parent FROM moz_bookmarks b JOIN moz_places p ON p.id=b.fk WHERE b.type=1 ORDER BY b.parent, b.position LIMIT {MaxBookmarks * 2}";
                    using var r = cmd.ExecuteReader();
                    while (r.Read() && marks.Count < MaxBookmarks)
                    {
                        if (Bookmark(r.IsDBNull(0) ? "" : Text(r.GetValue(0)), r.IsDBNull(1) ? "" : Text(r.GetValue(1)),
                                FirefoxPath(folders, r.IsDBNull(2) ? 0 : r.GetInt64(2))) is { } b)
                            marks.Add(b);
                    }
                }
            }
            return marks;
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private static string Text(object value) => value as string ?? "";

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // The folder names from the top down; null for tags (not bookmarks) or a loop.
    private static IReadOnlyList<string>? FirefoxPath(Dictionary<long, (long Parent, string Title, string Guid)> folders, long parent)
    {
        var names = new List<string>();
        var visited = new HashSet<long>();
        for (var id = parent; folders.TryGetValue(id, out var f); id = f.Parent)
        {
            if (!visited.Add(id) || visited.Count > 64) return null;
            switch (f.Guid)
            {
                case "root________": names.Reverse(); return names.Take(MaxFolderDepth).ToArray();
                case "tags________": return null;
                case "menu________": names.Add("Bookmarks menu"); break;
                case "toolbar_____": names.Add("Bookmarks toolbar"); break;
                case "unfiled_____": names.Add("Other bookmarks"); break;
                case "mobile______": names.Add("Mobile bookmarks"); break;
                default: names.Add(f.Title); break;
            }
        }
        names.Reverse();
        return names.Take(MaxFolderDepth).ToArray();
    }

    // Chrome, Edge and Brave: the Bookmarks JSON file, read whole (it is capped).
    internal static IEnumerable<MoveInBookmark> ReadChrome(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        using var doc = JsonDocument.Parse(ReadCapped(stream, MaxChromeBytes), new JsonDocumentOptions { MaxDepth = 64 });
        var marks = new List<MoveInBookmark>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("roots", out var roots) ||
            roots.ValueKind != JsonValueKind.Object) throw new FormatException("not a Bookmarks file");
        (string Key, string Name)[] tops = [("bookmark_bar", "Bookmarks bar"), ("other", "Other bookmarks"), ("synced", "Mobile bookmarks")];
        foreach (var (key, name) in tops)
            if (roots.TryGetProperty(key, out var node)) Walk(node, [name], marks);
        return marks;

        static void Walk(JsonElement node, List<string> path, List<MoveInBookmark> marks)
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("children", out var children) ||
                children.ValueKind != JsonValueKind.Array) return;
            foreach (var child in children.EnumerateArray())
            {
                if (marks.Count >= MaxBookmarks) return;
                if (child.ValueKind != JsonValueKind.Object) continue;
                var type = Str(child, "type");
                if (type == "url" && Bookmark(Str(child, "name"), Str(child, "url"), path) is { } b) marks.Add(b);
                else if (type == "folder" && path.Count < MaxFolderDepth) Walk(child, [.. path, Str(child, "name")], marks);
            }
        }
        static string Str(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    private static string ReadCapped(Stream stream, long max)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buffer = new char[Math.Min(max, stream.Length) + 1];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }

    private static void CopyCapped(string from, string to, long max)
    {
        using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var target = new FileStream(to, FileMode.CreateNew, FileAccess.Write);
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += n;
            if (total > max) throw new IOException("too large");
            target.Write(buffer, 0, n);
        }
    }

    // A bookmark worth keeping: an http(s) address and a clean title; anything else is dropped.
    internal static MoveInBookmark? Bookmark(string title, string url, IReadOnlyList<string>? folders)
    {
        if (folders is null) return null;
        url = url.Trim();
        if (url.Length is 0 or > MaxUrl || url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return null;
        var clean = CleanTitle(title);
        return new MoveInBookmark(clean.Length > 0 ? clean : uri.Host, uri.AbsoluteUri, folders.Select(CleanTitle).Select(f => f.Length > 0 ? f : "Folder").ToArray());
    }

    // No control or direction-changing characters, one line, bounded.
    internal static string CleanTitle(string text)
    {
        var kept = new string(text.Where(c => !char.IsControl(c) && c is not ('‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩'))).ToArray()).Trim();
        return kept.Length <= MaxTitle ? kept : kept[..(MaxTitle - 1)] + "…";
    }

    // ---- saving bookmarks for Firefox ----

    public async Task<MoveInSaved> SaveBookmarksAsync(string? user, CancellationToken ct)
    {
        var report = Current ?? throw new MoveInException("Open the old drive in Move in first.");
        var users = report.Bookmarks.Select(b => b.User).Distinct().ToArray();
        if (users.Length == 0) throw new MoveInException("No browser bookmarks were found on the old drive.");
        user = string.IsNullOrWhiteSpace(user) ? (users.Length == 1 ? users[0] : null) : user.Trim();
        if (user is null) throw new MoveInException("Say whose bookmarks to bring: " + string.Join(", ", users) + ".");
        if (!users.Contains(user, StringComparer.Ordinal)) throw new MoveInException("No bookmarks were found for that person.");
        var groups = ReadBookmarks(VolumeOf(report.Root), user).Where(g => g.Bookmarks.Count > 0).ToArray();
        var count = groups.Sum(g => g.Bookmarks.Count);
        if (count == 0) throw new MoveInException("No bookmarks could be read. Is the old drive still open?");
        var html = BookmarksHtml($"From the old computer ({user})", groups);
        var path = WriteNew(html);
        await journal.AppendAsync(new JournalRecord(JournalKinds.Setting, "user",
            NexusActions.Clean($"Move in saved {count} bookmarks from the old computer to {Display(path)}", ActivityJournal.MaxSummaryLength),
            Ref: "movein:bookmarks"), ct);
        return new MoveInSaved(path, count, groups.Select(g => g.Browser).ToArray());
    }

    public string Display(string path) => path.StartsWith(Home + "/", StringComparison.Ordinal) ? "~" + path[Home.Length..] : path;

    // The Netscape bookmarks format Firefox imports and exports, with every text escaped.
    internal static string BookmarksHtml(string title, IEnumerable<(string Browser, IReadOnlyList<MoveInBookmark> Bookmarks)> groups)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE NETSCAPE-Bookmark-file-1>\n<!-- Made by NexusOS Move in. Import it in Firefox: Bookmarks, Manage bookmarks, Import and Backup, Import Bookmarks from HTML. -->\n");
        sb.Append("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\n");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'self'; script-src 'none'; img-src data: *; object-src 'none'\"></meta>\n");
        sb.Append("<TITLE>Bookmarks</TITLE>\n<H1>Bookmarks</H1>\n<DL><p>\n");
        sb.Append("    <DT><H3>").Append(E(title)).Append("</H3>\n    <DL><p>\n");
        foreach (var (browser, marks) in groups)
        {
            sb.Append("        <DT><H3>").Append(E(browser)).Append("</H3>\n        <DL><p>\n");
            var open = new List<string>();
            foreach (var b in marks.OrderBy(b => string.Join('\u001f', b.Folders), StringComparer.Ordinal))
            {
                var common = 0;
                while (common < open.Count && common < b.Folders.Count && open[common] == b.Folders[common]) common++;
                for (var i = open.Count; i > common; i--) sb.Append(Pad(i + 2)).Append("</DL><p>\n");
                open.RemoveRange(common, open.Count - common);
                foreach (var f in b.Folders.Skip(common))
                {
                    open.Add(f);
                    sb.Append(Pad(open.Count + 2)).Append("<DT><H3>").Append(E(f)).Append("</H3>\n").Append(Pad(open.Count + 2)).Append("<DL><p>\n");
                }
                sb.Append(Pad(open.Count + 3)).Append("<DT><A HREF=\"").Append(E(b.Url)).Append("\">").Append(E(b.Title)).Append("</A>\n");
            }
            for (var i = open.Count; i > 0; i--) sb.Append(Pad(i + 2)).Append("</DL><p>\n");
            sb.Append("        </DL><p>\n");
        }
        sb.Append("    </DL><p>\n</DL>\n");
        return sb.ToString();

        static string Pad(int level) => new(' ', level * 4);
    }

    // ~/Documents/Bookmarks from the old computer.html, or " 2", " 3"...: never an existing file, never through a symlink.
    private string WriteNew(string html)
    {
        var docs = Path.Combine(Home, "Documents");
        if (new DirectoryInfo(docs) is { LinkTarget: not null }) throw new MoveInException("Your Documents folder is a link; Nexus will not write through it.");
        Directory.CreateDirectory(docs);
        for (var n = 1; n < 100; n++)
        {
            var path = Path.Combine(docs, n == 1 ? "Bookmarks from the old computer.html" : $"Bookmarks from the old computer {n}.html");
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                var bytes = Encoding.UTF8.GetBytes(html);
                stream.Write(bytes, 0, bytes.Length);
                return path;
            }
            catch (IOException) when (File.Exists(path) || new FileInfo(path).LinkTarget is not null) { }
        }
        throw new MoveInException("There are already many bookmark files in Documents. Remove some first.");
    }

    // ---- programs on the old computer ----

    // Cheap and read-only: folder names in Program Files, Start menu shortcuts, and .app names in Applications.
    internal static IReadOnlyList<string> InstalledPrograms(string volume, IReadOnlyList<string> users)
    {
        var names = new List<string>();
        void Add(IEnumerable<string> found) { foreach (var n in found) if (names.Count < MaxNames) names.Add(n); }
        foreach (var pf in new[] { "Program Files", "Program Files (x86)" })
            if (Safe(volume, pf) is { } dir) Add(Names(dir, e => e is DirectoryInfo));
        var menus = new List<string?> { Safe(volume, "ProgramData", "Microsoft", "Windows", "Start Menu", "Programs") };
        menus.AddRange(users.Select(u => Safe(volume, "Users", u, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs")));
        foreach (var menu in menus.OfType<string>())
        {
            Add(Names(menu, e => e is FileInfo && e.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)));
            foreach (var sub in Names(menu, e => e is DirectoryInfo).Take(200))
                Add(Names(Path.Combine(menu, sub), e => e is FileInfo && e.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)));
        }
        var apps = new List<string?> { Safe(volume, "Applications"), Safe(volume, "Applications", "Utilities") };
        apps.AddRange(users.Select(u => Safe(volume, "Users", u, "Applications")));
        foreach (var dir in apps.OfType<string>())
            Add(Names(dir, e => e is DirectoryInfo && e.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)));
        return names.Select(ProgramName).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string ProgramName(string raw)
    {
        var name = Regex.Replace(raw, @"\.(app|lnk)$", "", RegexOptions.IgnoreCase);
        name = NexusActions.Clean(new string(name.Where(c => !char.IsControl(c)).ToArray()), 80);
        return Regex.IsMatch(name, @"^(uninstall|readme|help|license|website|documentation)\b", RegexOptions.IgnoreCase) ? "" : name;
    }
}

// What a program on the old computer becomes here: the same app from Flathub,
// one that does the same job, or one NexusOS already has. Ids come only from
// this list, never from the drive.
public static partial class MoveInApps
{
    private sealed record Entry(Regex Pattern, string? AppId, string Name, bool Same, string? Already = null);

    private static Entry Same(string words, string id, string name) => new(Words(words), id, name, true);
    private static Entry Instead(string words, string id, string name) => new(Words(words), id, name, false);
    private static Entry Here(string words, string name) => new(Words(words), null, name, false, name);
    private static Regex Words(string words) =>
        new($@"(^|[^a-z0-9])({words})($|[^a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    private static readonly Entry[] Catalogue =
    [
        Same("spotify", "com.spotify.Client", "Spotify"),
        Same("discord", "com.discordapp.Discord", "Discord"),
        Same("steam", "com.valvesoftware.Steam", "Steam"),
        Same("zoom|zoom\\.us", "us.zoom.Zoom", "Zoom"),
        Same("slack", "com.slack.Slack", "Slack"),
        Same("google chrome|chrome", "com.google.Chrome", "Google Chrome"),
        Same("microsoft edge", "com.microsoft.Edge", "Microsoft Edge"),
        Same("brave|brave browser", "com.brave.Browser", "Brave"),
        Same("opera|opera gx", "com.opera.Opera", "Opera"),
        Same("thunderbird|mozilla thunderbird", "org.mozilla.Thunderbird", "Thunderbird"),
        Instead("outlook|microsoft outlook", "org.mozilla.Thunderbird", "Thunderbird"),
        Same("visual studio code|vs code|microsoft vs code", "com.visualstudio.code", "Visual Studio Code"),
        Same("obs studio|obs", "com.obsproject.Studio", "OBS Studio"),
        Same("gimp|gimp 2", "org.gimp.GIMP", "GIMP"),
        Instead("adobe photoshop[a-z0-9 ]*|photoshop|pixelmator|pixelmator pro|affinity photo", "org.gimp.GIMP", "GIMP"),
        Same("inkscape", "org.inkscape.Inkscape", "Inkscape"),
        Instead("adobe illustrator[a-z0-9 ]*|illustrator|affinity designer|coreldraw", "org.inkscape.Inkscape", "Inkscape"),
        Same("blender", "org.blender.Blender", "Blender"),
        Same("krita", "org.kde.krita", "Krita"),
        Same("audacity", "org.audacityteam.Audacity", "Audacity"),
        Instead("adobe premiere[a-z0-9 ]*|premiere pro|final cut pro|imovie|davinci resolve|filmora|vegas pro|clipchamp", "org.kde.kdenlive", "Kdenlive"),
        Instead("adobe lightroom[a-z0-9 ]*|lightroom|lightroom classic", "org.darktable.Darktable", "darktable"),
        Same("telegram|telegram desktop", "org.telegram.desktop", "Telegram"),
        Same("signal", "org.signal.Signal", "Signal"),
        Same("dropbox", "com.dropbox.Client", "Dropbox"),
        Same("bitwarden", "com.bitwarden.desktop", "Bitwarden"),
        Instead("keepass|keepassxc|keepass password safe 2", "org.keepassxc.KeePassXC", "KeePassXC"),
        Same("calibre", "com.calibre_ebook.calibre", "calibre"),
        Same("zotero", "org.zotero.Zotero", "Zotero"),
        Same("obsidian", "md.obsidian.Obsidian", "Obsidian"),
        Instead("evernote|onenote|microsoft onenote", "net.cozic.joplin_desktop", "Joplin"),
        Same("postman", "com.getpostman.Postman", "Postman"),
        Same("android studio", "com.google.AndroidStudio", "Android Studio"),
        Same("intellij idea[a-z0-9 .]*", "com.jetbrains.IntelliJ-IDEA-Community", "IntelliJ IDEA Community"),
        Same("pycharm[a-z0-9 .]*", "com.jetbrains.PyCharm-Community", "PyCharm Community"),
        Same("handbrake", "fr.handbrake.ghb", "HandBrake"),
        Same("qbittorrent", "org.qbittorrent.qBittorrent", "qBittorrent"),
        Instead("utorrent|bittorrent|transmission", "org.qbittorrent.qBittorrent", "qBittorrent"),
        Same("filezilla", "org.filezillaproject.Filezilla", "FileZilla"),
        Instead("winscp|cyberduck", "org.filezillaproject.Filezilla", "FileZilla"),
        Instead("epic games launcher|epic games|gog galaxy", "com.heroicgameslauncher.hgl", "Heroic Games Launcher"),
        Instead("battle\\.net|ea app|origin|ubisoft connect", "net.lutris.Lutris", "Lutris"),
        Instead("minecraft|minecraft launcher", "org.prismlauncher.PrismLauncher", "Prism Launcher (Minecraft)"),
        Same("musescore[a-z0-9 ]*", "org.musescore.MuseScore", "MuseScore"),
        Same("anki", "net.ankiweb.Anki", "Anki"),
        Instead("garageband|fl studio[a-z0-9 ]*|ableton live[a-z0-9 ]*", "io.lmms.LMMS", "LMMS"),
        Same("unity hub", "com.unity.UnityHub", "Unity Hub"),
        Same("godot|godot engine", "org.godotengine.Godot", "Godot"),
        Same("anydesk", "com.anydesk.Anydesk", "AnyDesk"),
        Same("element", "im.riot.Riot", "Element"),
        Same("kodi", "tv.kodi.Kodi", "Kodi"),
        Same("plex|plex media player", "tv.plex.PlexDesktop", "Plex"),
        Same("sublime text[a-z0-9 ]*", "com.sublimetext.three", "Sublime Text"),
        Instead("paint\\.net", "com.github.PintaProject.Pinta", "Pinta"),
        Here("microsoft office|word|excel|powerpoint|microsoft word|microsoft excel|microsoft powerpoint|pages|numbers|keynote|libreoffice", "LibreOffice"),
        Here("vlc|vlc media player|windows media player|quicktime player", "VLC"),
        Here("mozilla firefox|firefox", "Firefox"),
        Here("adobe acrobat[a-z0-9 ]*|acrobat reader|adobe reader|foxit reader|foxit pdf reader", "Papers"),
        Here("7-zip|7z|winrar|winzip|the unarchiver", "Archive Manager"),
        Here("notepad\\+\\+", "Text Editor"),
    ];

    // A Windows installer's file name as the program it installs:
    // "SpotifySetup-1.2.3.x64.exe" -> "Spotify", "npp.8.6.Installer.x64.exe" -> "npp".
    public static string ProgramFromFileName(string fileName)
    {
        var name = Path.GetFileName(fileName ?? "");
        name = InstallerExtension().Replace(name, "");
        name = CamelCase().Replace(name, "$1 $2");
        var words = name.Split([' ', '_', '-', '.', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !InstallerWord().IsMatch(w)).ToArray();
        return string.Join(' ', words).Trim();
    }

    [GeneratedRegex(@"\.(exe|msi|msix|msixbundle|appx|appxbundle)$", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerExtension();
    [GeneratedRegex(@"([a-z])([A-Z])")]
    private static partial Regex CamelCase();
    [GeneratedRegex(@"^(setup|installer|install|x64|x86|win64|win32|windows|win|web|full|offline|online|amd64|arm64|stub|latest|release|portable|bit|64|32|v?\d[\d]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerWord();

    public static IReadOnlyList<string> Ids => Catalogue.Where(e => e.AppId is not null).Select(e => e.AppId!).Distinct().ToArray();

    // One suggestion per app here, naming the first old program that led to it.
    public static IReadOnlyList<MoveInApp> Match(IEnumerable<string> programs)
    {
        var result = new List<MoveInApp>();
        foreach (var program in programs)
        {
            Entry? hit = null;
            try { hit = Catalogue.FirstOrDefault(e => e.Pattern.IsMatch(program)); }
            catch (RegexMatchTimeoutException) { }
            if (hit is null || result.Any(r => r.Name == hit.Name)) continue;
            result.Add(new MoveInApp(program, hit.AppId, hit.Name, hit.Same, hit.Already));
        }
        return result.OrderBy(r => r.AppId is null).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

public static class MoveInEndpoints
{
    public static IServiceCollection AddMoveIn(this IServiceCollection services) => services.AddSingleton<MoveInService>();

    public static object View(MoveInReport r) => new
    {
        opened = true, root = r.Root, os = r.Os, users = r.Users,
        folders = r.Folders.Select(f => new { user = f.User, folder = f.Folder, bytes = f.Bytes, on = f.On }),
        bookmarks = r.Bookmarks.Select(b => new { user = b.User, browser = b.Browser, count = b.Count }),
        oldApps = r.OldApps.Take(80),
        apps = r.Apps.Select(a => new { was = a.Was, id = a.AppId, name = a.Name, same = a.Same, already = a.Already }),
        notes = r.Notes
    };

    // POST /movein/look: the shell opened the old drive (root) and found these folders; Core adds bookmarks and apps.
    // GET /movein: what was found last. DELETE /movein: the drive was closed. POST /movein/bookmarks: save them for Firefox.
    public static void MapMoveIn(this WebApplication app)
    {
        app.MapGet("/movein", (MoveInService movein) => Results.Ok(movein.Current is { } r ? View(r) : new { opened = false }));
        app.MapPost("/movein/look", (MoveInLookRequest request, MoveInService movein) =>
        {
            try { return Results.Ok(View(movein.Look(request))); }
            catch (MoveInException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        // A Windows program someone opened (a .exe or .msi): what it is, and the app here instead.
        app.MapPost("/windows-program", (WindowsProgramRequest request) =>
        {
            var program = MoveInApps.ProgramFromFileName(request.FileName ?? "");
            if (program.Length is 0 or > 120) return Results.Ok(new { program = "", match = (MoveInApp?)null });
            var match = MoveInApps.Match([program]).FirstOrDefault();
            return Results.Ok(new { program, match });
        });
        app.MapDelete("/movein", (MoveInService movein) => { movein.Forget(); return Results.Ok(new { opened = false }); });
        app.MapPost("/movein/bookmarks", async (MoveInBookmarksRequest request, MoveInService movein, CancellationToken ct) =>
        {
            try
            {
                var saved = await movein.SaveBookmarksAsync(request.User, ct);
                return Results.Ok(new { path = saved.Path, shown = movein.Display(saved.Path), count = saved.Count, browsers = saved.Browsers });
            }
            catch (MoveInException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireRateLimiting("journal");
    }
}
