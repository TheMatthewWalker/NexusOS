using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// Read-only diagnostics Core may run (docs/design/features.md, "Probes").
// The list is fixed in code: an id, an area, an exact argv (never a shell),
// a timeout and an output cap. Nothing a caller or a model supplies can add a
// probe or change its arguments. Probe output is untrusted text.
public static class ProbeAreas
{
    public const string Sound = "sound", Network = "network", Display = "display", Power = "power", Disk = "disk", App = "app";
    public static readonly string[] All = [Sound, Network, Display, Power, Disk, App];
}

public enum ProbeFormat { Text, Colon, Columns, KeyValue, WpctlSinks }

public sealed record ProbeDefinition(string Id, string Area, string[] Argv, TimeSpan Timeout, int OutputCap,
    ProbeFormat Format = ProbeFormat.Text, string[]? Fields = null, bool Health = false);

public static class Probes
{
    public const int DefaultOutputCap = 64 * 1024;
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(5);

    public static readonly IReadOnlyList<ProbeDefinition> All =
    [
        new("sound.status", ProbeAreas.Sound, ["wpctl", "status"], Short, DefaultOutputCap, ProbeFormat.WpctlSinks),
        new("sound.default-sink", ProbeAreas.Sound, ["wpctl", "inspect", "@DEFAULT_AUDIO_SINK@"], Short, DefaultOutputCap),
        new("sound.volume", ProbeAreas.Sound, ["wpctl", "get-volume", "@DEFAULT_AUDIO_SINK@"], Short, 4096),
        new("network.general", ProbeAreas.Network, ["nmcli", "-t", "-f", "STATE,CONNECTIVITY,WIFI-HW,WIFI", "general"], Short, 4096,
            ProbeFormat.Colon, ["STATE", "CONNECTIVITY", "WIFI-HW", "WIFI"], Health: true),
        new("network.devices", ProbeAreas.Network, ["nmcli", "-t", "-f", "DEVICE,TYPE,STATE", "device"], Short, 16 * 1024,
            ProbeFormat.Colon, ["DEVICE", "TYPE", "STATE"]),
        new("display.monitors", ProbeAreas.Display, ["hyprctl", "monitors"], Short, DefaultOutputCap),
        new("power.devices", ProbeAreas.Power, ["upower", "-d"], Short, DefaultOutputCap, ProbeFormat.KeyValue, Health: true),
        new("power.profile", ProbeAreas.Power, ["powerprofilesctl", "get"], Short, 1024),
        new("disk.usage", ProbeAreas.Disk, ["df", "-P", "-x", "tmpfs", "-x", "devtmpfs", "-x", "efivarfs"], TimeSpan.FromSeconds(10),
            DefaultOutputCap, ProbeFormat.Columns, ["filesystem", "blocks", "used", "available", "capacity", "mount"], Health: true),
        new("app.failed-units", ProbeAreas.App, ["systemctl", "--user", "--failed", "--no-legend", "--plain"], Short, DefaultOutputCap,
            ProbeFormat.Columns, ["unit", "load", "active", "sub", "description"], Health: true),
        new("app.journal-warnings", ProbeAreas.App, ["journalctl", "--user", "-p", "warning", "-n", "50", "--no-pager"],
            TimeSpan.FromSeconds(10), DefaultOutputCap, Health: true),
    ];

    private static readonly Dictionary<string, ProbeDefinition> ById = All.ToDictionary(x => x.Id, StringComparer.Ordinal);

    public static ProbeDefinition? Find(string id) => ById.GetValueOrDefault(id);
    public static IEnumerable<ProbeDefinition> ForArea(string area) => All.Where(x => x.Area == area);
}

public static class ProbeStatus
{
    public const string Ok = "ok", Failed = "failed", Unavailable = "unavailable", Timeout = "timeout";
}

public sealed record CommandResult(string Status, int? ExitCode, string Output, bool Truncated)
{
    public static readonly CommandResult Unavailable = new(ProbeStatus.Unavailable, null, "", false);
}

// The only place Core starts a process for probes and personal fixes. Tests
// substitute a fake to supply fixture output.
public interface ICommandRunner
{
    // Absolute path of a trusted binary, or null when it is not installed.
    string? Resolve(string binary);
    Task<CommandResult> RunAsync(string path, IReadOnlyList<string> args, TimeSpan timeout, int outputCap, CancellationToken ct);
}

public sealed class ProcessCommandRunner(IConfiguration config) : ICommandRunner
{
    private const int StderrCap = 4096;
    // Fixed system locations only; the caller's PATH is not consulted.
    private readonly string[] _searchPath = (config["NEXUS_PROBE_PATH"] ?? "/run/current-system/sw/bin:/usr/bin:/bin")
        .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(Path.IsPathRooted).ToArray();

    public string? Resolve(string binary)
    {
        if (binary.Length == 0 || binary.Contains('/') || binary.Contains('\0')) return null;
        foreach (var dir in _searchPath)
        {
            var candidate = Path.Combine(dir, binary);
            try
            {
                if (File.Exists(candidate) && (OperatingSystem.IsWindows() ||
                        (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0))
                    return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public async Task<CommandResult> RunAsync(string path, IReadOnlyList<string> args, TimeSpan timeout, int outputCap, CancellationToken ct)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        // Stable, parseable, uncoloured output with no pager.
        start.Environment["LC_ALL"] = "C";
        start.Environment["LANG"] = "C";
        start.Environment["SYSTEMD_COLORS"] = "0";
        start.Environment["SYSTEMD_PAGER"] = "";
        start.Environment["NO_COLOR"] = "1";
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException) { return CommandResult.Unavailable; }
        process.StandardInput.Close();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var stdout = new BoundedText(outputCap);
        var stderr = new BoundedText(Math.Min(outputCap, StderrCap));
        void Kill() { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        var pumps = Task.WhenAll(Pump(process.StandardOutput, stdout, Kill), Pump(process.StandardError, stderr, null));
        var timedOut = false;
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            Kill();
            if (ct.IsCancellationRequested) throw;
            timedOut = true;
        }
        try { await pumps.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); }
        catch (TimeoutException) { }
        var output = stdout.Text + (stderr.Text.Length > 0 ? "\n" + stderr.Text : "");
        if (timedOut) return new CommandResult(ProbeStatus.Timeout, null, output, stdout.Truncated);
        if (stdout.Truncated) return new CommandResult(ProbeStatus.Ok, null, output, true);
        return new CommandResult(process.ExitCode == 0 ? ProbeStatus.Ok : ProbeStatus.Failed, process.ExitCode, output, false);
    }

    private static async Task Pump(StreamReader reader, BoundedText sink, Action? onFull)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            if (!sink.Append(buffer.AsSpan(0, read)))
            {
                onFull?.Invoke();
                return;
            }
        }
    }

    private sealed class BoundedText(int cap)
    {
        private readonly StringBuilder _text = new();
        public bool Truncated { get; private set; }
        public string Text => _text.ToString();
        public bool Append(ReadOnlySpan<char> chunk)
        {
            var room = cap - _text.Length;
            if (chunk.Length > room)
            {
                _text.Append(chunk[..Math.Max(room, 0)]);
                Truncated = true;
                return false;
            }
            _text.Append(chunk);
            return true;
        }
    }
}

// One parsed line or block of probe output.
public sealed record ProbeRecord(IReadOnlyDictionary<string, string> Fields, string Line);

public sealed record ProbeResult(string Id, string Status, string Output, bool Truncated, IReadOnlyList<ProbeRecord> Records)
{
    public bool Ran => Status is ProbeStatus.Ok or ProbeStatus.Failed;
}

public sealed class ProbeRunner(ICommandRunner runner)
{
    public async Task<ProbeResult> RunAsync(string id, CancellationToken ct)
    {
        var probe = Probes.Find(id) ?? throw new ArgumentException("Unknown probe.", nameof(id));
        var path = runner.Resolve(probe.Argv[0]);
        // A missing tool is reported as unavailable, never guessed at.
        if (path is null) return new ProbeResult(probe.Id, ProbeStatus.Unavailable, "", false, []);
        var result = await runner.RunAsync(path, probe.Argv[1..], probe.Timeout, probe.OutputCap, ct);
        var output = result.Output.Length > probe.OutputCap + 4096 ? result.Output[..(probe.OutputCap + 4096)] : result.Output;
        var records = result.Status is ProbeStatus.Ok or ProbeStatus.Failed ? ProbeParsers.Parse(probe, output) : [];
        return new ProbeResult(probe.Id, result.Status, output, result.Truncated || output.Length < result.Output.Length, records);
    }

    public async Task<IReadOnlyList<ProbeResult>> RunAllAsync(IEnumerable<ProbeDefinition> probes, CancellationToken ct) =>
        await Task.WhenAll(probes.Select(x => RunAsync(x.Id, ct)));
}

public static class ProbeParsers
{
    public const int MaxRecords = 512;
    private static readonly Regex SinkLine = new(@"^[\s│├└─|`]*(?<default>\*)?\s*(?<id>\d{1,6})\.\s+(?<name>.*?)(?:\s+\[[^\]]*\])?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static IReadOnlyList<ProbeRecord> Parse(ProbeDefinition probe, string output)
    {
        var lines = output.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var records = probe.Format switch
        {
            ProbeFormat.Colon => lines.Where(x => x.Length > 0).Select(x => Colon(x, probe.Fields!)),
            ProbeFormat.Columns => lines.Skip(probe.Id == "disk.usage" ? 1 : 0).Where(x => x.Trim().Length > 0)
                .Select(x => Columns(x, probe.Fields!)),
            ProbeFormat.KeyValue => KeyValue(lines),
            ProbeFormat.WpctlSinks => WpctlSinks(lines),
            _ => lines.Where(x => x.Trim().Length > 0).Select(x => new ProbeRecord(new Dictionary<string, string>(), x)),
        };
        return records.Where(x => x is not null).Take(MaxRecords).ToArray()!;
    }

    // nmcli -t: colon separated, with "\:" and "\\" escapes.
    private static ProbeRecord Colon(string line, string[] fields)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length) { current.Append(line[++i]); continue; }
            if (line[i] == ':') { values.Add(current.ToString()); current.Clear(); continue; }
            current.Append(line[i]);
        }
        values.Add(current.ToString());
        return new ProbeRecord(Map(fields, values), line);
    }

    // Whitespace columns; the last field takes the rest of the line.
    private static ProbeRecord Columns(string line, string[] fields)
    {
        var parts = line.Trim().Split((char[]?)null, fields.Length, StringSplitOptions.RemoveEmptyEntries);
        return new ProbeRecord(Map(fields, parts.Select(x => x.Trim())), line);
    }

    private static Dictionary<string, string> Map(string[] fields, IEnumerable<string> values)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (field, value) in fields.Zip(values)) map[field] = value;
        return map;
    }

    // upower -d: "Device:" / "Daemon:" blocks of "key: value" lines. A bare
    // indented word (for example "battery") names the device kind.
    private static IEnumerable<ProbeRecord> KeyValue(string[] lines)
    {
        Dictionary<string, string>? current = null;
        var block = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("Device:", StringComparison.Ordinal) || line.StartsWith("Daemon:", StringComparison.Ordinal))
            {
                if (current is not null) yield return new ProbeRecord(current, block.ToString().Trim());
                current = new Dictionary<string, string>(StringComparer.Ordinal)
                    { ["device"] = line.StartsWith("Daemon:", StringComparison.Ordinal) ? "daemon" : line[7..].Trim() };
                block.Clear();
                continue;
            }
            if (current is null || line.Trim().Length == 0) continue;
            if (block.Length < 1024) block.Append(line.Trim()).Append('\n');
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                var key = line[..colon].Trim().ToLowerInvariant();
                if (key.Length is > 0 and <= 64) current.TryAdd(key, line[(colon + 1)..].Trim());
            }
            else if (line.Trim() is { Length: > 0 and <= 32 } kind && kind.All(char.IsLetterOrDigit))
                current.TryAdd("kind", kind.ToLowerInvariant());
        }
        if (current is not null) yield return new ProbeRecord(current, block.ToString().Trim());
    }

    // wpctl status: the sinks listed under Audio, with "*" marking the default.
    private static IEnumerable<ProbeRecord> WpctlSinks(string[] lines)
    {
        var inAudio = false;
        var inSinks = false;
        foreach (var line in lines)
        {
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] is not ('│' or '├' or '└'))
            {
                inAudio = line.Trim() == "Audio";
                inSinks = false;
                continue;
            }
            if (!inAudio) continue;
            if (line.Contains("─ ", StringComparison.Ordinal) && line.TrimEnd().EndsWith(':'))
            {
                inSinks = line.TrimEnd().EndsWith("Sinks:", StringComparison.Ordinal);
                continue;
            }
            if (!inSinks) continue;
            var match = SinkLine.Match(line);
            if (!match.Success) continue;
            yield return new ProbeRecord(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id"] = match.Groups["id"].Value,
                ["name"] = match.Groups["name"].Value.Trim(),
                ["default"] = match.Groups["default"].Success ? "yes" : "no",
            }, line.Trim());
        }
    }
}
