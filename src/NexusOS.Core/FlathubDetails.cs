using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

// What installing a Flathub app means, before it is installed: who publishes
// it, whether Flathub verified them, its licence, the download size and what
// it may reach outside its sandbox, in plain words with a warning for wide
// access. Read the cheap way first: the appstream catalogue flatpak keeps on
// disk (publisher, verified, licence), then `flatpak remote-info --cached`
// (sizes and permissions from the cached summary), and only then the network.
// Everything read here is the catalogue's text, not the person's or the
// model's: it is cleaned and shown as data.
public sealed record AppPermission(string Text, bool Broad);

public sealed record FlathubAppDetails(string Id, bool Known, string? Name, string? Summary, string? Developer, bool? Verified,
    string? License, bool? OpenSource, string? DownloadSize, string? InstalledSize, string? Version, bool PermissionsKnown,
    IReadOnlyList<AppPermission> Permissions, string? Warning, IReadOnlyList<string> Lines);

public static partial class FlatpakPermissions
{
    // What each sandbox hole means for the person. Missing entries (wayland,
    // dri, ipc, fallback-x11...) are what every desktop app needs and are not listed.
    private static readonly Dictionary<string, (string Text, bool Broad)> Sockets = new(StringComparer.Ordinal)
    {
        ["x11"] = ("Uses the older X11 display, where it can see other X11 apps' windows and what you type into them", true),
        ["pulseaudio"] = ("Can play sound and use the microphone", false),
        ["session-bus"] = ("Can talk to every app and service in your session", true),
        ["system-bus"] = ("Can talk to every system service", true),
        ["ssh-auth"] = ("Can use your SSH keys", true),
        ["gpg-agent"] = ("Can use your GPG keys", true),
        ["pcsc"] = ("Can use smart cards", false),
        ["cups"] = ("Can print", false),
    };
    private static readonly Dictionary<string, (string Text, bool Broad)> Devices = new(StringComparer.Ordinal)
    {
        ["all"] = ("Can use all devices, including the camera, microphones and USB devices", true),
        ["kvm"] = ("Can run virtual machines", false),
        ["input"] = ("Can read game controllers and other input devices", false),
        ["usb"] = ("Can use USB devices", false),
    };
    private static readonly Dictionary<string, (string Text, bool Broad)> Features = new(StringComparer.Ordinal)
    {
        ["devel"] = ("Can inspect other programs (a developer feature)", true),
        ["bluetooth"] = ("Can use Bluetooth", false),
        ["canbus"] = ("Can use CAN bus devices", false),
    };
    private static readonly Dictionary<string, (string Text, bool Broad)> Buses = new(StringComparer.Ordinal)
    {
        ["org.freedesktop.Flatpak"] = ("Can run commands outside its sandbox", true),
        ["org.freedesktop.secrets"] = ("Can read your saved passwords", true),
        ["org.freedesktop.impl.portal.PermissionStore"] = ("Can change other apps' permissions", true),
    };
    private static readonly Dictionary<string, string> Folders = new(StringComparer.Ordinal)
    {
        ["xdg-download"] = "your Downloads folder", ["xdg-documents"] = "your Documents folder", ["xdg-pictures"] = "your Pictures folder",
        ["xdg-music"] = "your Music folder", ["xdg-videos"] = "your Videos folder", ["xdg-desktop"] = "your Desktop folder",
        ["xdg-templates"] = "your Templates folder", ["xdg-public-share"] = "your Public folder",
    };

    // Flatpak's metadata file: an ini with [Context] and [Session Bus Policy].
    public static IReadOnlyList<AppPermission> Summarise(string metadata)
    {
        var found = new List<AppPermission>();
        void Add(string text, bool broad) { if (!found.Any(p => p.Text == text)) found.Add(new AppPermission(text, broad)); }
        string? section = null;
        foreach (var raw in metadata.Split('\n').Take(500))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var values = line[(eq + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(v => !v.StartsWith('!')).Take(64);
            switch (section, key)
            {
                case ("Context", "shared"):
                    if (values.Contains("network")) Add("Can use the internet", false);
                    break;
                case ("Context", "sockets"):
                    foreach (var v in values) if (Sockets.TryGetValue(v, out var s)) Add(s.Text, s.Broad);
                    break;
                case ("Context", "devices"):
                    foreach (var v in values) if (Devices.TryGetValue(v, out var d)) Add(d.Text, d.Broad);
                    break;
                case ("Context", "features"):
                    foreach (var v in values) if (Features.TryGetValue(v, out var f)) Add(f.Text, f.Broad);
                    break;
                case ("Context", "filesystems"):
                    foreach (var v in values) if (Filesystem(v) is { } fs) Add(fs.Text, fs.Broad);
                    break;
                case ("Session Bus Policy", _) or ("System Bus Policy", _):
                    if (Buses.TryGetValue(key, out var b) && line[(eq + 1)..].Trim() is "talk" or "own") Add(b.Text, b.Broad);
                    break;
            }
        }
        return found.OrderByDescending(p => p.Broad).ToArray();
    }

    private static (string Text, bool Broad)? Filesystem(string entry)
    {
        var readOnly = entry.EndsWith(":ro", StringComparison.Ordinal);
        var create = entry.EndsWith(":create", StringComparison.Ordinal);
        var path = readOnly ? entry[..^3] : create ? entry[..^7] : entry.EndsWith(":rw", StringComparison.Ordinal) ? entry[..^3] : entry;
        var verb = readOnly ? "Can read" : "Can read and change";
        var slash = path.IndexOf('/');
        var root = slash < 0 ? path : path[..slash];
        var sub = slash < 0 ? "" : path[(slash + 1)..];
        switch (root)
        {
            case "host": return (verb + " all files on this computer, yours included", true);
            case "host-os": return ("Can read the system's programs and libraries", true);
            case "host-etc": return ("Can read the system's settings", true);
            case "host-root": return (verb + " the whole system", true);
            case "xdg-config" or "xdg-data" or "xdg-cache" or "xdg-state":
                return sub.Length == 0 ? (verb + " every app's settings and data", true) : (verb + " some other apps' settings", false);
            case "xdg-run": return null;   // sockets of desktop services, e.g. pipewire
            case "home" or "~": return Home(verb, sub.TrimEnd('/'));
        }
        if (Folders.TryGetValue(root, out var folder))
            return (verb + (sub.Length == 0 ? " " + folder : " a folder in " + folder), false);
        if (path is "/media" or "/run/media" or "/mnt" || path.StartsWith("/media/", StringComparison.Ordinal) ||
            path.StartsWith("/run/media/", StringComparison.Ordinal))
            return (verb + " USB drives and other disks", false);
        if (path.StartsWith('/')) return (verb + " " + Short(path), path is "/" or "/home" or "/var" or "/etc" or "/usr");
        return null;
    }

    // A folder in the home folder: keys and passwords, or every app's settings, are wide access.
    private static (string Text, bool Broad) Home(string verb, string sub) =>
        sub.Length == 0 ? (verb + " all your files", true)
        : sub.Split('/')[0] is ".ssh" or ".gnupg" or ".password-store" or ".pki" ? (verb + " ~/" + Short(sub) + ", which holds keys or passwords", true)
        : sub is ".config" or ".local" or ".local/share" ? (verb + " every app's settings and data", true)
        : (verb + " ~/" + Short(sub), false);

    private static string Short(string path) => NexusActions.Clean(path, 60);
}

public sealed partial class FlathubDetails(ICommandRunner runner, IConfiguration config)
{
    public const int MaxCatalogueBytes = 256 << 20;
    private static readonly TimeSpan CachedTimeout = TimeSpan.FromSeconds(8), OnlineTimeout = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (string Path, DateTime Stamp, Dictionary<string, Catalogue> Apps)? _index;

    public sealed record Catalogue(string? Name, string? Summary, string? Developer, bool Verified, string? License, long? DownloadBytes);

    public async Task<FlathubAppDetails> GetAsync(string id, CancellationToken ct)
    {
        if (!AppChanges.FlathubId().IsMatch(id)) throw new ArgumentException("Not a Flathub app id.");
        var listed = await CatalogueAsync(id, ct);
        var infoTask = RemoteInfoAsync(id, metadata: false, ct);
        var meta = await RemoteInfoAsync(id, metadata: true, ct);
        var info = await infoTask;
        var fields = info is null ? new Dictionary<string, string>() : Fields(info);
        IReadOnlyList<AppPermission> permissions = meta is null ? [] : FlatpakPermissions.Summarise(meta);
        var license = Clean(listed?.License ?? fields.GetValueOrDefault("License"), 100);
        bool? open = license is null ? null : !license.Contains("proprietary", StringComparison.OrdinalIgnoreCase);
        var download = fields.GetValueOrDefault("Download") is { } d ? Clean(d, 20) : listed?.DownloadBytes is { } bytes ? Size(bytes) : null;
        var details = new FlathubAppDetails(id, listed is not null || info is not null, Clean(listed?.Name, AppChanges.MaxNameLength),
            Clean(listed?.Summary, 200), Clean(listed?.Developer, 80), listed is null ? null : listed.Verified, license, open, download,
            Clean(fields.GetValueOrDefault("Installed"), 20), Clean(fields.GetValueOrDefault("Version"), 40), meta is not null, permissions,
            Warning(permissions), []);
        return details with { Lines = Lines(details) };
    }

    public static string? Warning(IReadOnlyList<AppPermission> permissions) =>
        permissions.FirstOrDefault(p => p.Broad) is { } broad
            ? $"Wide access: {char.ToLowerInvariant(broad.Text[0])}{broad.Text[1..]}. Flathub apps run in a sandbox, and this opens it up. Install it only if you trust who makes it."
            : null;

    // Plain sentences for a card, most important first.
    public static IReadOnlyList<string> Lines(FlathubAppDetails d)
    {
        var lines = new List<string>();
        if (!d.Known) { lines.Add("Flathub's details for this app could not be read, so its publisher and permissions are not known."); return lines; }
        if (d.Developer is not null || d.Verified is not null)
            lines.Add((d.Developer is null ? "Publisher not given" : "By " + d.Developer) +
                      (d.Verified == true ? ", verified by Flathub" : d.Verified == false ? ", not verified: Flathub has not confirmed who publishes it" : ""));
        if (d.License is not null)
            lines.Add("Licence: " + (d.OpenSource == false ? "proprietary (not open source)" : d.License));
        if (d.DownloadSize is not null)
            lines.Add("Download: " + d.DownloadSize + ", plus shared parts it needs that are not here yet");
        if (!d.PermissionsKnown) lines.Add("Its permissions could not be read (Flathub may not be reachable).");
        else if (d.Permissions.Count == 0) lines.Add("Stays in its sandbox: no access to your files, devices or the internet.");
        else lines.AddRange(d.Permissions.Select(p => p.Text));
        return lines;
    }

    private static string Size(long bytes) =>
        bytes >= 1 << 30 ? $"{bytes / (double)(1 << 30):0.0} GB" : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0} MB" : $"{Math.Max(1, bytes >> 10)} kB";

    private static string? Clean(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : NexusActions.Clean(Regex.Replace(text, @"\s+", " "), max) is { Length: > 0 } c ? c : null;

    // `flatpak remote-info` prints "  Label: value" lines.
    public static Dictionary<string, string> Fields(string output)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n').Take(100))
        {
            var match = FieldLine().Match(line);
            if (match.Success) fields.TryAdd(match.Groups[1].Value, match.Groups[2].Value.Trim());
        }
        return fields;
    }

    [GeneratedRegex(@"^\s*([A-Z][A-Za-z]{1,20}):\s+(\S.*)$")]
    private static partial Regex FieldLine();

    private async Task<string?> RemoteInfoAsync(string id, bool metadata, CancellationToken ct)
    {
        if (runner.Resolve("flatpak") is not { } flatpak) return null;
        string[] args = metadata ? ["remote-info", "--show-metadata"] : ["remote-info"];
        var cached = await runner.RunAsync(flatpak, [.. args, "--cached", AppChanges.Flathub, id], CachedTimeout, 65536, ct);
        if (cached.Status == ProbeStatus.Ok && cached.Output.Length > 0) return cached.Output;
        var online = await runner.RunAsync(flatpak, [.. args, AppChanges.Flathub, id], OnlineTimeout, 65536, ct);
        return online.Status == ProbeStatus.Ok && online.Output.Length > 0 ? online.Output : null;
    }

    // ---- the appstream catalogue on disk ----

    private IEnumerable<string> CatalogueFiles()
    {
        if (config["NEXUS_FLATPAK_APPSTREAM"] is { Length: > 0 } configured) return configured.Split(':', StringSplitOptions.RemoveEmptyEntries);
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } h ? h : Path.Combine(home, ".local", "share");
        return new[] { Path.Combine(dataHome, "flatpak"), "/var/lib/flatpak" }
            .SelectMany(root => new[] { "appstream.xml.gz", "appstream.xml" }
                .Select(name => Path.Combine(root, "appstream", AppChanges.Flathub, arch, "active", name)));
    }

    private async Task<Catalogue?> CatalogueAsync(string id, CancellationToken ct)
    {
        var file = CatalogueFiles().Select(f => new FileInfo(f)).FirstOrDefault(f => f.Exists && f.Length <= MaxCatalogueBytes);
        if (file is null) return null;
        await _lock.WaitAsync(ct);
        try
        {
            if (_index is not { } index || index.Path != file.FullName || index.Stamp != file.LastWriteTimeUtc)
            {
                var apps = await Task.Run(() => Parse(file.FullName), ct);
                _index = index = (file.FullName, file.LastWriteTimeUtc, apps);
            }
            return index.Apps.GetValueOrDefault(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidDataException) { return null; }
        finally { _lock.Release(); }
    }

    public static Dictionary<string, Catalogue> Parse(string path)
    {
        using var stream = File.OpenRead(path);
        using var input = path.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(stream, CompressionMode.Decompress) : (Stream)stream;
        return Parse(input);
    }

    public static Dictionary<string, Catalogue> Parse(Stream input)
    {
        var apps = new Dictionary<string, Catalogue>(StringComparer.Ordinal);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(input, settings);
        reader.MoveToContent();
        if (!reader.Read()) return apps;
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "component") { reader.Read(); continue; }
            if (XNode.ReadFrom(reader) is not XElement component || apps.Count >= 100_000) continue;
            var id = Plain(component, "id")?.Trim();
            if (id is null) continue;
            if (id.EndsWith(".desktop", StringComparison.Ordinal)) id = id[..^8];
            var developer = component.Element("developer") is { } dev ? Plain(dev, "name") : null;
            var verified = component.Element("custom")?.Elements("value")
                .Any(v => (string?)v.Attribute("key") == "flathub::verification::verified" && v.Value.Trim() == "true") == true;
            var size = component.Descendants("size").FirstOrDefault(s => (string?)s.Attribute("type") == "download");
            apps.TryAdd(id, new Catalogue(Plain(component, "name"), Plain(component, "summary"), developer ?? Plain(component, "developer_name"),
                verified, (string?)component.Element("project_license"), long.TryParse(size?.Value, out var bytes) ? bytes : null));
        }
        return apps;
    }

    // The untranslated text of a child element (no xml:lang).
    private static string? Plain(XElement parent, string name) =>
        parent.Elements(name).FirstOrDefault(e => e.Attribute(XNamespace.Xml + "lang") is null)?.Value;
}
