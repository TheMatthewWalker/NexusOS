using System.Diagnostics;
using Xunit;

// Records every process Core asks to start and answers from fixtures.
public sealed class FakeCommandRunner : ICommandRunner
{
    private readonly Dictionary<string, CommandResult> _outputs = new(StringComparer.Ordinal);
    public HashSet<string> Installed { get; } = new(StringComparer.Ordinal)
        { "wpctl", "nmcli", "hyprctl", "upower", "powerprofilesctl", "df", "systemctl", "journalctl" };
    public List<string[]> Executed { get; } = [];
    public CommandResult Default { get; set; } = new(ProbeStatus.Ok, 0, "", false);

    public FakeCommandRunner Returns(string probeId, string output, string status = ProbeStatus.Ok)
    {
        _outputs[string.Join(' ', Probes.Find(probeId)!.Argv)] = new CommandResult(status, status == ProbeStatus.Ok ? 0 : 1, output, false);
        return this;
    }

    public FakeCommandRunner ReturnsForArgv(string[] argv, CommandResult result)
    {
        _outputs[string.Join(' ', argv)] = result;
        return this;
    }

    public string? Resolve(string binary) => Installed.Contains(binary) ? "/fake/bin/" + binary : null;

    public Task<CommandResult> RunAsync(string path, IReadOnlyList<string> args, TimeSpan timeout, int outputCap, CancellationToken ct)
    {
        var argv = new[] { Path.GetFileName(path) }.Concat(args).ToArray();
        lock (Executed) Executed.Add(argv);
        return Task.FromResult(_outputs.GetValueOrDefault(string.Join(' ', argv), Default));
    }
}

public static class ProbeFixtures
{
    public const string WpctlDummyDefault = """
        PipeWire 'pipewire-0' [1.0.5, alice@nexus, cookie:1234]
         └─ Clients:
                31. WirePlumber                         [1.0.5, alice@nexus, pid:1201]

        Audio
         ├─ Devices:
         │      42. Built-in Audio                      [alsa]
         │
         ├─ Sinks:
         │  *   30. Dummy Output                        [vol: 1.00]
         │      46. Built-in Audio Analog Stereo        [vol: 0.40]
         │
         ├─ Sink endpoints:
         │
         ├─ Sources:
         │  *   47. Built-in Audio Analog Stereo        [vol: 1.00]
         │
         └─ Streams:

        Video
         ├─ Devices:
         │      50. Integrated Camera                   [v4l2]
        """;

    public static readonly string WpctlHealthy = WpctlDummyDefault
        .Replace("*   30. Dummy Output", "    30. Dummy Output", StringComparison.Ordinal)
        .Replace("    46. Built-in Audio Analog Stereo        [vol: 0.40]", "*   46. Built-in Audio Analog Stereo        [vol: 0.40]", StringComparison.Ordinal);

    public const string WpctlNoDaemon = "Could not connect to PipeWire";

    public const string UpowerCritical = """
        Device: /org/freedesktop/UPower/devices/battery_BAT0
          native-path:          BAT0
          vendor:               SMP
          power supply:         yes
          updated:              Thu 22 Oct 2026 10:00:00 BST (5 seconds ago)
          battery
            present:             yes
            state:               discharging
            energy:              4.1 Wh
            percentage:          7%
            capacity:            90%

        Device: /org/freedesktop/UPower/devices/DisplayDevice
          power supply:         yes
          battery
            state:               discharging
            percentage:          7%

        Daemon:
          daemon-version:  1.90.2
          on-battery:      yes
        """;

    public static readonly string UpowerHealthy = UpowerCritical.Replace("7%", "64%", StringComparison.Ordinal);

    public const string DfFull = """
        Filesystem     1024-blocks      Used Available Capacity Mounted on
        /dev/nvme0n1p2   488281250 468750000  19531250      97% /
        /dev/nvme0n1p1     1046512    120000    926512      12% /boot
        """;

    public static readonly string DfHealthy = DfFull.Replace("97% /", "41% /", StringComparison.Ordinal);

    public const string FailedUnit = "backup.service loaded failed failed Nightly backup\n";
    public const string WifiOff = "disconnected:none:enabled:disabled\n";
    public const string Disconnected = "disconnected:none:enabled:enabled\n";
    public const string Connected = "connected:full:enabled:enabled\n";
    public const string DevicesDisconnected = "wlp2s0:wifi:disconnected\nlo:loopback:unmanaged\n";
    public const string Muted = "Volume: 0.40 [MUTED]\n";
    public const string Unmuted = "Volume: 0.40\n";
}

public sealed class ProbeTests
{
    private static readonly string[] ReadOnlyBinaries =
        ["wpctl", "nmcli", "hyprctl", "upower", "powerprofilesctl", "df", "systemctl", "journalctl"];

    [Fact]
    public void EveryProbeIsAnExactArgvOfAKnownReadOnlyTool()
    {
        Assert.Equal(Probes.All.Count, Probes.All.Select(x => x.Id).Distinct().Count());
        foreach (var probe in Probes.All)
        {
            Assert.Contains(probe.Argv[0], ReadOnlyBinaries);
            Assert.Contains(probe.Area, ProbeAreas.All);
            Assert.InRange(probe.Timeout, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15));
            Assert.InRange(probe.OutputCap, 1, 64 * 1024);
            // No shells, and no verbs that change state.
            Assert.DoesNotContain(probe.Argv, a => a is "sh" or "bash" or "-c" || a.Contains(';') || a.Contains('|') || a.Contains('`'));
            Assert.DoesNotContain(probe.Argv, a => a.StartsWith("set", StringComparison.Ordinal) ||
                a is "restart" or "start" or "stop" or "connect" or "on" or "off" or "radio" or "networking" or "--vacuum-size");
        }
    }

    [Fact]
    public void EveryAreaHasProbesAndHealthProbesExist()
    {
        foreach (var area in ProbeAreas.All) Assert.NotEmpty(Probes.ForArea(area));
        Assert.Contains(Probes.All, x => x.Health);
    }

    [Fact]
    public async Task UnknownProbeIdsAreRefused()
    {
        var runner = new FakeCommandRunner();
        var probes = new ProbeRunner(runner);
        await Assert.ThrowsAsync<ArgumentException>(() => probes.RunAsync("rm -rf /", default));
        await Assert.ThrowsAsync<ArgumentException>(() => probes.RunAsync("sound.status ; reboot", default));
        Assert.Empty(runner.Executed);
    }

    [Fact]
    public async Task MissingBinaryIsReportedUnavailableAndNothingRuns()
    {
        var runner = new FakeCommandRunner();
        runner.Installed.Remove("wpctl");
        var result = await new ProbeRunner(runner).RunAsync("sound.status", default);
        Assert.Equal(ProbeStatus.Unavailable, result.Status);
        Assert.Empty(result.Records);
        Assert.Empty(runner.Executed);
    }

    [Fact]
    public async Task OversizedFakeOutputIsCappedByTheRunner()
    {
        var runner = new FakeCommandRunner().Returns("sound.volume", new string('x', 1_000_000));
        var result = await new ProbeRunner(runner).RunAsync("sound.volume", default);
        Assert.True(result.Truncated);
        Assert.True(result.Output.Length <= Probes.Find("sound.volume")!.OutputCap + 4096);
    }

    [Fact]
    public void ParsedRecordsAreBounded()
    {
        var lines = string.Concat(Enumerable.Range(0, 5000).Select(i => $"unit{i}.service loaded failed failed x\n"));
        Assert.Equal(ProbeParsers.MaxRecords, ProbeParsers.Parse(Probes.Find("app.failed-units")!, lines).Count);
    }

    [Fact]
    public void ResolveUsesOnlyTheFixedSearchPath()
    {
        using var state = new TempState();
        Directory.CreateDirectory(state.Root);
        var empty = new ProcessCommandRunner(state.Config(("NEXUS_PROBE_PATH", state.Root)));
        Assert.Null(empty.Resolve("sleep"));
        var system = new ProcessCommandRunner(TestConfig.Of(("NEXUS_PROBE_PATH", "/usr/bin:/bin")));
        Assert.NotNull(system.Resolve("sleep"));
        Assert.Null(system.Resolve("/usr/bin/sleep"));
        Assert.Null(system.Resolve("../bin/sleep"));
        Assert.Null(system.Resolve("nexus-no-such-tool"));
    }

    [Fact]
    public async Task RealProcessRunnerEnforcesTheTimeout()
    {
        var runner = new ProcessCommandRunner(TestConfig.Of(("NEXUS_PROBE_PATH", "/usr/bin:/bin")));
        var clock = Stopwatch.StartNew();
        var result = await runner.RunAsync(runner.Resolve("sleep")!, ["30"], TimeSpan.FromMilliseconds(300), 1024, default);
        Assert.Equal(ProbeStatus.Timeout, result.Status);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RealProcessRunnerEnforcesTheOutputCap()
    {
        var runner = new ProcessCommandRunner(TestConfig.Of(("NEXUS_PROBE_PATH", "/usr/bin:/bin")));
        var clock = Stopwatch.StartNew();
        var result = await runner.RunAsync(runner.Resolve("yes")!, [], TimeSpan.FromSeconds(10), 1000, default);
        Assert.True(result.Truncated);
        Assert.True(result.Output.Length <= 1000);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RealProcessRunnerPassesArgumentsWithoutAShell()
    {
        var runner = new ProcessCommandRunner(TestConfig.Of(("NEXUS_PROBE_PATH", "/usr/bin:/bin")));
        var result = await runner.RunAsync(runner.Resolve("echo")!, ["$(id)", ";", "`whoami`"], TimeSpan.FromSeconds(5), 1024, default);
        Assert.Equal(ProbeStatus.Ok, result.Status);
        Assert.Equal("$(id) ; `whoami`", result.Output.Trim());
    }

    [Fact]
    public void WpctlSinksAreParsedFromTheAudioSectionOnly()
    {
        var records = ProbeParsers.Parse(Probes.Find("sound.status")!, ProbeFixtures.WpctlDummyDefault);
        Assert.Equal(2, records.Count);
        Assert.Equal(("30", "Dummy Output", "yes"), (records[0].Fields["id"], records[0].Fields["name"], records[0].Fields["default"]));
        Assert.Equal(("46", "Built-in Audio Analog Stereo", "no"), (records[1].Fields["id"], records[1].Fields["name"], records[1].Fields["default"]));
    }

    [Fact]
    public void UpowerBlocksAndNmcliEscapesAreParsed()
    {
        var upower = ProbeParsers.Parse(Probes.Find("power.devices")!, ProbeFixtures.UpowerCritical);
        Assert.Equal("battery", upower[0].Fields["kind"]);
        Assert.Equal("7%", upower[0].Fields["percentage"]);
        Assert.Equal("daemon", upower[^1].Fields["device"]);
        var nmcli = ProbeParsers.Parse(Probes.Find("network.devices")!, @"my\:dev:wifi:connected");
        Assert.Equal("my:dev", nmcli[0].Fields["DEVICE"]);
        var df = ProbeParsers.Parse(Probes.Find("disk.usage")!, ProbeFixtures.DfFull);
        Assert.Equal(("/", "97%"), (df[0].Fields["mount"], df[0].Fields["capacity"]));
    }
}
