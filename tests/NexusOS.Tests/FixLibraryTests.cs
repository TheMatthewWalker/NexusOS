using Xunit;

// Each shipped library entry matches its fixture output, stays quiet on a
// healthy system, and yields only allow-listed actions.
public sealed class FixLibraryTests
{
    private static readonly FixLibrary Library = new();

    private static async Task<IReadOnlyList<FixMatch>> MatchAsync(string area, FakeCommandRunner runner)
    {
        var ids = Probes.ForArea(area).Select(x => x.Id).Concat(Library.ProbesFor(area)).Distinct();
        var results = await new ProbeRunner(runner).RunAllAsync(ids.Select(x => Probes.Find(x)!), default);
        return Library.Match(results.ToDictionary(x => x.Id), area);
    }

    public static IEnumerable<object[]> Cases() => new (string Entry, string Area, Func<FakeCommandRunner> Runner, string[] Argv)[]
    {
        ("sound.pipewire-down", "sound", () => new FakeCommandRunner().Returns("sound.status", ProbeFixtures.WpctlNoDaemon, ProbeStatus.Failed),
            ["systemctl", "--user", "restart", "pipewire.service", "wireplumber.service"]),
        ("sound.default-sink-dummy", "sound", () => new FakeCommandRunner().Returns("sound.status", ProbeFixtures.WpctlDummyDefault)
            .Returns("sound.volume", ProbeFixtures.Unmuted), ["wpctl", "set-default", "46"]),
        ("sound.muted", "sound", () => new FakeCommandRunner().Returns("sound.status", ProbeFixtures.WpctlHealthy)
            .Returns("sound.volume", ProbeFixtures.Muted), ["wpctl", "set-mute", "@DEFAULT_AUDIO_SINK@", "0"]),
        ("network.wifi-off", "network", () => new FakeCommandRunner().Returns("network.general", ProbeFixtures.WifiOff),
            ["nmcli", "radio", "wifi", "on"]),
        ("network.disconnected", "network", () => new FakeCommandRunner().Returns("network.general", ProbeFixtures.Disconnected)
            .Returns("network.devices", ProbeFixtures.DevicesDisconnected), ["nmcli", "device", "connect", "wlp2s0"]),
        ("disk.low-space", "disk", () => new FakeCommandRunner().Returns("disk.usage", ProbeFixtures.DfFull), []),
        ("app.failed-unit", "app", () => new FakeCommandRunner().Returns("app.failed-units", ProbeFixtures.FailedUnit),
            ["systemctl", "--user", "restart", "backup.service"]),
        ("power.battery-critical", "power", () => new FakeCommandRunner().Returns("power.devices", ProbeFixtures.UpowerCritical),
            ["powerprofilesctl", "set", "power-saver"]),
    }.Select(x => new object[] { x.Entry, x.Area, x.Runner, x.Argv });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EachEntryMatchesItsFixture(string entry, string area, Func<FakeCommandRunner> runner, string[] argv)
    {
        var matches = await MatchAsync(area, runner());
        var match = Assert.Single(matches);
        Assert.Equal(entry, match.Entry.Id);
        Assert.NotEmpty(match.Evidence);
        Assert.All(match.Evidence, e => Assert.InRange(e.Excerpt.Length, 1, FixLibrary.MaxExcerpt));
        Assert.DoesNotContain("{", match.Cause);
        var fix = Assert.Single(match.Fixes);
        if (argv.Length == 0)
        {
            Assert.Equal(FixKinds.System, fix.Kind);
            Assert.Null(fix.Argv);
            Assert.False(string.IsNullOrWhiteSpace(fix.Proposal));
        }
        else Assert.Equal(argv, fix.Argv);
    }

    [Fact]
    public void LibraryShipsTheAgreedEntries()
    {
        var ids = Library.Entries.Select(x => x.Id).ToArray();
        Assert.InRange(ids.Length, 6, 8);
        Assert.Equal(Cases().Select(x => (string)x[0]).Order(), ids.Order());
    }

    [Theory]
    [InlineData("sound")]
    [InlineData("network")]
    [InlineData("disk")]
    [InlineData("app")]
    [InlineData("power")]
    public async Task HealthyFixturesMatchNothing(string area)
    {
        var runner = new FakeCommandRunner()
            .Returns("sound.status", ProbeFixtures.WpctlHealthy).Returns("sound.volume", ProbeFixtures.Unmuted)
            .Returns("network.general", ProbeFixtures.Connected).Returns("network.devices", "wlp2s0:wifi:connected\n")
            .Returns("disk.usage", ProbeFixtures.DfHealthy).Returns("app.failed-units", "")
            .Returns("power.devices", ProbeFixtures.UpowerHealthy);
        Assert.Empty(await MatchAsync(area, runner));
    }

    [Fact]
    public async Task UnavailableProbesNeverMatch()
    {
        var runner = new FakeCommandRunner().Returns("power.devices", ProbeFixtures.UpowerCritical);
        runner.Installed.Clear();
        foreach (var area in ProbeAreas.All) Assert.Empty(await MatchAsync(area, runner));
    }

    [Fact]
    public void EveryPersonalActionIsAFixedReadableArgv()
    {
        foreach (var action in PersonalActions.All.Values)
        {
            Assert.Contains(action.Argv[0], new[] { "wpctl", "nmcli", "systemctl", "powerprofilesctl" });
            Assert.DoesNotContain(action.Argv, a => a is "sh" or "bash" or "-c" or "sudo" or "pkexec" or "--system");
            foreach (var token in action.Argv.Where(t => t.StartsWith('{')))
                Assert.True(action.Parameters.ContainsKey(token[1..^1]));
        }
    }

    [Theory]
    [InlineData("app.restart-user-unit", "unit", "evil.service;rm")]
    [InlineData("app.restart-user-unit", "unit", "--root=/.service")]
    [InlineData("app.restart-user-unit", "unit", "x.service\nrm -rf ~")]
    [InlineData("audio.set-default-sink", "sinkId", "46 && reboot")]
    [InlineData("audio.set-default-sink", "sinkId", "-1")]
    [InlineData("network.connect-device", "device", "-h")]
    [InlineData("network.connect-device", "device", "wlan0 $(id)")]
    public void ActionParametersRejectHostileValues(string action, string name, string value) =>
        Assert.Null(PersonalActions.Resolve(PersonalActions.All[action], new Dictionary<string, string> { [name] = value }));

    private const string Valid = """
        {"schemaVersion":1,"entries":[{"id":"x.test","area":"sound","priority":1,
          "match":[{"probe":"sound.volume","regex":"MUTED"}],"cause":"Muted.",
          "fixes":[{"id":"unmute","kind":"personal","label":"Unmute","action":"audio.unmute-default-sink"}]}]}
        """;

    [Fact]
    public void ValidLibraryFileLoads() => Assert.Single(new FixLibrary([FixLibrary.ParseFile(Valid)]).Entries);

    [Theory]
    [InlineData("\"action\":\"audio.unmute-default-sink\"", "\"action\":\"shell.run\"")]
    [InlineData("\"action\":\"audio.unmute-default-sink\"", "\"action\":\"app.restart-user-unit\"")]
    [InlineData("\"kind\":\"personal\"", "\"kind\":\"root\"")]
    [InlineData("\"kind\":\"personal\",\"label\":\"Unmute\",\"action\":\"audio.unmute-default-sink\"", "\"kind\":\"system\",\"label\":\"Unmute\",\"action\":\"audio.unmute-default-sink\"")]
    [InlineData("\"probe\":\"sound.volume\"", "\"probe\":\"shell\"")]
    [InlineData("\"area\":\"sound\"", "\"area\":\"kernel\"")]
    [InlineData("\"regex\":\"MUTED\"", "\"regex\":\"(MUTED\"")]
    [InlineData("\"regex\":\"MUTED\"", "\"regex\":\"(?<=x)MUTED\"")]
    [InlineData("\"regex\":\"MUTED\"", "\"regex\":\"MUTED\",\"status\":\"ok\"")]
    [InlineData("\"cause\":\"Muted.\"", "\"cause\":\"Muted {who}.\"")]
    [InlineData("\"cause\":\"Muted.\"", "\"cause\":\"Muted.\",\"command\":\"rm -rf /\"")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":2")]
    public void InvalidLibraryFilesAreRejected(string from, string to)
    {
        var json = Valid.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(Valid, json);
        Assert.ThrowsAny<Exception>(() => new FixLibrary([FixLibrary.ParseFile(json)]));
    }
}
