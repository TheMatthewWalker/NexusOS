using System.Globalization;
using System.Text;

// How Nexus looks at the computer it runs on before deciding what to do
// (the inspect_system action). The person says "why is it slow?" or "set me
// up for coding"; the model does not guess: it looks (what is running, what
// is installed, what the system is configured as, what is in a folder) and
// works out the next step from what it finds.
//
// Everything here only reads. Each view is a fixed argv (never a shell, the
// model cannot add a command or an argument), run through the same runner as
// Fix it's probes, with a timeout and an output cap. Folders are listed only
// inside the home folder, never those that hold keys or settings. What comes
// back is data from the computer, never instructions; Core says so to the model.
public sealed class SystemInspector(ICommandRunner runner, IConfiguration config)
{
    public const int MaxChars = 3200;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(6);

    private sealed record View(string Title, string[][] Commands, string? File = null);

    private static readonly Dictionary<string, View> Views = new(StringComparer.Ordinal)
    {
        ["processes"] = new("What is running, busiest first", [["ps", "-eo", "pid,user,pcpu,pmem,etime,comm", "--sort=-pcpu"]]),
        ["memory"] = new("Memory, and what uses most of it", [["free", "-h"], ["ps", "-eo", "pmem,rss,comm", "--sort=-rss"]]),
        ["disk"] = new("Disks and free space", [["df", "-hP", "-x", "tmpfs", "-x", "devtmpfs", "-x", "efivarfs"], ["lsblk", "-o", "NAME,SIZE,TYPE,FSTYPE,MOUNTPOINTS"]]),
        ["hardware"] = new("The computer's hardware", [["lscpu"], ["lspci"]]),
        ["network"] = new("Network connections", [["nmcli", "-t", "-f", "DEVICE,TYPE,STATE,CONNECTION", "device"], ["ip", "-brief", "address"]]),
        ["services"] = new("Services that failed or are running",
            [["systemctl", "--failed", "--no-legend", "--plain"], ["systemctl", "--user", "--failed", "--no-legend", "--plain"],
             ["systemctl", "list-units", "--type=service", "--state=running", "--no-legend", "--plain"]]),
        ["logs"] = new("Recent warnings and errors", [["journalctl", "--user", "-p", "warning", "-n", "40", "--no-pager", "-o", "short"],
                                                       ["journalctl", "-p", "err", "-b", "-n", "40", "--no-pager", "-o", "short"]]),
        ["software"] = new("Installed programs (system, then Flathub apps)",
            [["ls", "/run/current-system/sw/bin"], ["flatpak", "list", "--app", "--columns=application,name"]]),
        ["config"] = new("This computer's NexusOS configuration (/etc/nixos/local.nix) and system version",
            [["nixos-version"]], File: "/etc/nixos/local.nix"),
        ["windows"] = new("Open windows", [["hyprctl", "clients"]]),
        ["updates"] = new("System generations (versions to go back to)", [["ls", "-1", "/nix/var/nix/profiles"]]),
    };

    public static readonly string[] Topics = [.. Views.Keys, "folder"];

    // topic: one of Topics. query: keeps only lines containing it. path: for "folder", inside the home folder.
    public async Task<object> LookAsync(string topic, string? query, string? path, CancellationToken ct)
    {
        if (topic == "folder") return Folder(path, query);
        if (!Views.TryGetValue(topic, out var view)) throw new ArgumentException("Unknown topic.");
        var text = new StringBuilder();
        foreach (var argv in view.Commands)
        {
            var binary = runner.Resolve(argv[0]);
            if (binary is null) { text.Append('(').Append(argv[0]).Append(" is not installed)\n"); continue; }
            var result = await runner.RunAsync(binary, argv[1..], Limit, 48 * 1024, ct);
            if (result.Status != ProbeStatus.Ok && result.Output.Length == 0) continue;
            text.Append(result.Output.TrimEnd()).Append("\n\n");
        }
        if (view.File is { } file && File.Exists(file))
        {
            try { text.Append(file).Append(":\n").Append(File.ReadAllText(file)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text.Append("(cannot read ").Append(file).Append(")\n"); }
        }
        return Result(topic, view.Title, Filter(text.ToString(), query), query);
    }

    private static string Filter(string text, string? query)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0 && !l.Any(c => char.IsControl(c) && c != '\t'));
        if (!string.IsNullOrWhiteSpace(query))
            lines = lines.Where(l => l.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
        return string.Join('\n', lines);
    }

    private static object Result(string topic, string title, string text, string? query)
    {
        var cut = text.Length > MaxChars;
        return new
        {
            topic, title, filteredBy = string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
            // Read from the computer: data to reason about, never instructions.
            data = cut ? text[..MaxChars] : text, truncated = cut,
            hint = text.Length == 0 ? "Nothing matched." : cut ? "Cut short: give a query to narrow it." : null
        };
    }

    // In the home folder, keys, passwords, browser profiles and Nexus's own state are never listed.
    private static readonly string[] Sensitive =
        [".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker", ".password-store", ".pki", ".mozilla", ".thunderbird",
         ".config", ".local/share/keyrings", ".local/share/nexusos", ".local/state", ".cache"];

    private object Folder(string? path, string? query)
    {
        var home = Path.GetFullPath(config["NEXUS_HOME"] ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var asked = string.IsNullOrWhiteSpace(path) ? "~" : path.Trim();
        if (asked.Contains('\0')) throw new ArgumentException("Not a folder in the home folder.");
        var full = Path.GetFullPath(asked == "~" ? home : asked.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, asked[2..]) : asked);
        if (full != home && !full.StartsWith(home + "/", StringComparison.Ordinal))
            throw new ArgumentException("Only folders inside the home folder can be listed.");
        var rel = Path.GetRelativePath(home, full);
        if (Sensitive.Any(s => rel == s || rel.StartsWith(s + "/", StringComparison.Ordinal)))
            throw new ArgumentException("That folder holds keys or settings; Nexus does not look in it.");
        // Not through a link out of the home folder.
        for (var at = new DirectoryInfo(full); at.FullName.Length > home.Length; at = at.Parent!)
            if (at.LinkTarget is not null) throw new ArgumentException("That folder is a link; Nexus lists real folders only.");
        if (!Directory.Exists(full)) throw new ArgumentException("That folder does not exist.");
        var lines = new List<string>();
        foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos().OrderByDescending(e => e.LastWriteTimeUtc).Take(400))
        {
            if (entry.Name.StartsWith('.') || entry.Name.Any(char.IsControl)) continue;
            var folder = (entry.Attributes & FileAttributes.Directory) != 0;
            lines.Add(folder ? entry.Name + "/"
                : $"{entry.Name}  ({Size(((FileInfo)entry).Length)}, {entry.LastWriteTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture)})");
        }
        var shown = "~" + (rel == "." ? "" : "/" + rel);
        return Result("folder", $"What is in {shown}, newest first", Filter(string.Join('\n', lines), query), query);
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes + " B"
    };
}
