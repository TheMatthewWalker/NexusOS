using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

// What a remembered workspace holds besides its apps (Workspaces.cs): the
// files open in them, Firefox's tabs and the Nexus conversation on screen.
// Everything here only reads, and only the person's own files: Core runs as
// the person, and looks only under their home directory.
public sealed partial class WorkspaceContents(IConfiguration config)
{
    public const int MaxFilesPerApp = 12;
    public const int MaxTabs = 60;
    public const int MaxPathLength = 4096;
    private const long MaxSessionBytes = 32 * 1024 * 1024;

    // NEXUS_WORKSPACE_HOME is for tests; otherwise the person's home.
    public string Home => config["NEXUS_WORKSPACE_HOME"] is { Length: > 0 } home
        ? Path.GetFullPath(home)
        : Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    // ---- files, from window titles ----

    // "Chapter 3.odt - LibreOffice Writer", "*notes.txt (~/Documents) - gedit",
    // "report.pdf — Document Viewer", "~/src/app.py - Mousepad". Only a first
    // part that looks like a file name counts; anything else is no file.
    [GeneratedRegex(@"^[^/\\\p{Cc}]{1,200}\.[A-Za-z0-9]{1,8}$")]
    private static partial Regex FileNamePattern();
    [GeneratedRegex(@"\s+(?:-|—|–|\|)\s+")]
    private static partial Regex TitleSeparator();

    public static string? FileNameFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var first = TitleSeparator().Split(title.Trim())[0].Trim().TrimStart('*', '●', '•', ' ');
        var paren = first.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) first = first[..paren].TrimEnd();
        if (first.StartsWith('/') || first.StartsWith("~/", StringComparison.Ordinal))
            return first.Length <= MaxPathLength && !first.Any(char.IsControl) ? first : null;
        return FileNamePattern().IsMatch(first) ? first : null;
    }

    // A name from a title becomes a full path through the person's recent
    // files (recently-used.xbel, kept by GTK and Qt apps), the newest first.
    // A path in the title is used as it is when it is in the home directory.
    public string? ResolveFile(string nameOrPath)
    {
        var home = Home;
        if (nameOrPath.StartsWith("~/", StringComparison.Ordinal)) nameOrPath = Path.Combine(home, nameOrPath[2..]);
        if (nameOrPath.StartsWith('/'))
        {
            var full = Path.GetFullPath(nameOrPath);
            return InHome(full) && File.Exists(full) ? full : null;
        }
        return RecentFiles().Where(p => Path.GetFileName(p) == nameOrPath).FirstOrDefault(File.Exists);
    }

    private bool InHome(string full) => full.StartsWith(Home.TrimEnd('/') + "/", StringComparison.Ordinal);

    public IReadOnlyList<string> RecentFiles()
    {
        var xbel = Path.Combine(Home, ".local/share/recently-used.xbel");
        if (!File.Exists(xbel)) return [];
        var found = new List<(string Path, string When)>();
        try
        {
            using var reader = XmlReader.Create(xbel, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "bookmark") continue;
                var href = reader.GetAttribute("href");
                if (href is null || !Uri.TryCreate(href, UriKind.Absolute, out var uri) || !uri.IsFile) continue;
                var path = Path.GetFullPath(uri.LocalPath);
                if (path.Length > MaxPathLength || !InHome(path)) continue;
                found.Add((path, reader.GetAttribute("visited") ?? reader.GetAttribute("modified") ?? ""));
                if (found.Count > 2000) break;
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException) { }
        return found.OrderByDescending(f => f.When, StringComparer.Ordinal).Select(f => f.Path).Distinct().ToArray();
    }

    // ---- Firefox tabs, from its session store ----

    public sealed record FirefoxWindow(string SelectedTitle, IReadOnlyList<WorkspaceTab> Tabs);
    public sealed record FirefoxSession(IReadOnlyList<FirefoxWindow> Windows, bool Running);

    // The default profile in profiles.ini (~/.mozilla/firefox, or the newer
    // ~/.config/mozilla/firefox), only when it lies under that folder.
    public string? FirefoxProfile()
    {
        foreach (var root in new[] { ".mozilla/firefox", ".config/mozilla/firefox" }.Select(r => Path.Combine(Home, r)))
        {
            var ini = Path.Combine(root, "profiles.ini");
            if (!File.Exists(ini)) continue;
            var sections = new List<Dictionary<string, string>>();
            foreach (var raw in File.ReadLines(ini).Take(2000))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) sections.Add(new(StringComparer.OrdinalIgnoreCase) { ["["] = line });
                else if (sections.Count > 0 && line.IndexOf('=') is > 0 and var eq) sections[^1][line[..eq]] = line[(eq + 1)..];
            }
            // Firefox's own choice ([Install...] Default=), else the profile marked Default=1.
            var install = sections.FirstOrDefault(s => s["["].StartsWith("[Install", StringComparison.OrdinalIgnoreCase) && s.ContainsKey("Default"));
            var profile = install is not null ? Path.Combine(root, install["Default"])
                : sections.Where(s => s["["].StartsWith("[Profile", StringComparison.OrdinalIgnoreCase) && s.ContainsKey("Path"))
                          .OrderByDescending(s => s.GetValueOrDefault("Default") == "1")
                          .Select(s => s.GetValueOrDefault("IsRelative", "1") == "0" ? s["Path"] : Path.Combine(root, s["Path"]))
                          .FirstOrDefault();
            if (profile is null) continue;
            var full = Path.GetFullPath(profile);
            if (full.StartsWith(Path.GetFullPath(root) + "/", StringComparison.Ordinal) && Directory.Exists(full)) return full;
        }
        return null;
    }

    // While Firefox runs it keeps sessionstore-backups/recovery.jsonlz4 up to
    // date (every 15 seconds); once it is closed, sessionstore.jsonlz4 holds
    // the last session. Private windows are in neither. Read, never written.
    public FirefoxSession? ReadFirefoxSession()
    {
        if (FirefoxProfile() is not { } profile) return null;
        var lockFile = new FileInfo(Path.Combine(profile, "lock"));
        var running = lockFile.Exists || lockFile.LinkTarget is not null;
        string[] candidates = running
            ? ["sessionstore-backups/recovery.jsonlz4", "sessionstore.jsonlz4"]
            : ["sessionstore.jsonlz4", "sessionstore-backups/recovery.jsonlz4"];
        foreach (var name in candidates)
        {
            var path = Path.Combine(profile, name);
            if (!File.Exists(path)) continue;
            try
            {
                if (new FileInfo(path).Length > MaxSessionBytes) continue;
                return new FirefoxSession(ParseSession(MozLz4.Decode(File.ReadAllBytes(path))), running);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public static IReadOnlyList<FirefoxWindow> ParseSession(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var windows = new List<FirefoxWindow>();
        if (!doc.RootElement.TryGetProperty("windows", out var ws) || ws.ValueKind != JsonValueKind.Array) return windows;
        foreach (var w in ws.EnumerateArray())
        {
            if (!w.TryGetProperty("tabs", out var tabs) || tabs.ValueKind != JsonValueKind.Array) continue;
            var list = new List<WorkspaceTab>();
            foreach (var tab in tabs.EnumerateArray())
                if (CurrentEntry(tab) is { } entry) list.Add(entry);
            var selected = w.TryGetProperty("selected", out var sel) && sel.TryGetInt32(out var s) ? s - 1 : 0;
            var tabArray = tabs.EnumerateArray().ToArray();
            var selectedTitle = selected >= 0 && selected < tabArray.Length && CurrentEntry(tabArray[selected], keepAll: true) is { } t ? t.Title : "";
            windows.Add(new FirefoxWindow(selectedTitle, list));
        }
        return windows;
    }

    // A tab's page now: entries[index - 1]. Only web pages are kept for
    // reopening (not about:, file: or extension pages).
    private static WorkspaceTab? CurrentEntry(JsonElement tab, bool keepAll = false)
    {
        if (!tab.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0) return null;
        var index = tab.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : entries.GetArrayLength();
        var entry = entries[Math.Clamp(index, 1, entries.GetArrayLength()) - 1];
        var url = entry.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        var title = entry.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
        if (!keepAll && !IsWebUrl(url)) return null;
        return new WorkspaceTab(url.Length <= 2048 ? url : "", Clean(title));
    }

    public static bool IsWebUrl(string? url) =>
        url is { Length: > 0 and <= 2048 } && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && !url.Any(char.IsControl);

    // The Firefox window titled "<page> — Mozilla Firefox" is the session
    // window whose chosen tab is that page.
    public static string FirefoxPageTitle(string windowTitle) =>
        Regex.Replace(windowTitle.Trim(), @"\s+(?:—|-|–)\s+(?:Mozilla Firefox|Firefox)(?: Private Browsing)?$", "");

    public static bool IsFirefox(string appId) => appId.Contains("firefox", StringComparison.OrdinalIgnoreCase);

    internal static string Clean(string? text)
    {
        var t = new string((text ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return t.Length <= 200 ? t : t[..199] + "…";
    }
}

// Firefox's "mozLz4" files: the magic "mozLz40\0", the size as a 32-bit
// little-endian number, then one LZ4 block.
public static class MozLz4
{
    private static readonly byte[] Magic = "mozLz40\0"u8.ToArray();
    public const int MaxDecoded = 64 * 1024 * 1024;

    public static byte[] Decode(byte[] file)
    {
        if (file.Length < 12 || !file.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException("Not a mozLz4 file.");
        var size = BitConverter.ToInt32(file, 8);
        if (size is < 0 or > MaxDecoded) throw new InvalidDataException("mozLz4 size out of range.");
        return DecodeBlock(file.AsSpan(12), size);
    }

    public static byte[] DecodeBlock(ReadOnlySpan<byte> src, int size)
    {
        var dst = new byte[size];
        int ip = 0, op = 0;
        while (ip < src.Length)
        {
            var token = src[ip++];
            var literals = token >> 4;
            if (literals == 15) literals += ReadLength(src, ref ip);
            if (literals > src.Length - ip || literals > size - op) throw new InvalidDataException("LZ4 literals out of range.");
            src.Slice(ip, literals).CopyTo(dst.AsSpan(op));
            ip += literals; op += literals;
            if (ip >= src.Length) break;                     // the last sequence has no match
            if (ip + 2 > src.Length) throw new InvalidDataException("LZ4 offset cut short.");
            var offset = src[ip] | src[ip + 1] << 8;
            ip += 2;
            if (offset == 0 || offset > op) throw new InvalidDataException("LZ4 offset out of range.");
            var match = (token & 15);
            if (match == 15) match += ReadLength(src, ref ip);
            match += 4;
            if (match > size - op) throw new InvalidDataException("LZ4 match out of range.");
            for (var k = 0; k < match; k++, op++) dst[op] = dst[op - offset];   // may overlap
        }
        if (op != size) throw new InvalidDataException("LZ4 block shorter than its size.");
        return dst;
    }

    private static int ReadLength(ReadOnlySpan<byte> src, ref int ip)
    {
        int total = 0, b;
        do
        {
            if (ip >= src.Length) throw new InvalidDataException("LZ4 length cut short.");
            b = src[ip++];
            total += b;
            if (total > MaxDecoded) throw new InvalidDataException("LZ4 length out of range.");
        } while (b == 255);
        return total;
    }
}
