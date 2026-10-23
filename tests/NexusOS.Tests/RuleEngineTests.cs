using Xunit;

public sealed class RuleEngineTests : IDisposable
{
    private readonly TempState _state = new();
    // 19 Oct 2026 is a Monday; the clock starts at 08:59:30 UTC.
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 19, 8, 59, 30, TimeSpan.Zero));
    private readonly RuleStore _store;
    private readonly RuleEngine _engine;

    public RuleEngineTests()
    {
        _store = new RuleStore(new SqliteStateDatabase(_state.Paths()), _state.Config(), _time);
        _engine = new RuleEngine(_store, _time);
    }

    public void Dispose() => _state.Dispose();

    private Task<Rule> Add(RuleDefinition definition, bool enabled = true) =>
        _store.AddAsync(definition, "rule", "", enabled, default);

    [Fact]
    public async Task MatchingEventFiresAndRecordsARun()
    {
        var rule = await Add(RuleSamples.Headphones());
        var fired = Assert.Single(await _engine.HandleEventAsync(new RuleEvent(RuleTriggers.DeviceConnected, "audio", "Sony WH-1000XM4"), default));
        Assert.Equal(rule.Id, fired.RuleId);
        Assert.Equal([RuleSamples.OpenSpotify], fired.Actions);
        var run = Assert.Single((await _store.HistoryAsync(rule.Id, 10, default))!);
        Assert.Equal(fired.RunId, run.Id);
        Assert.Equal("audio device “Sony WH-1000XM4” connected", run.Trigger);
        Assert.Equal("events-response", run.Delivery);
        // Events-response actions are not also queued.
        Assert.Empty(_engine.Pending(0).Items);
    }

    [Theory]
    [InlineData(RuleTriggers.DeviceDisconnected, "audio", null)]
    [InlineData(RuleTriggers.DeviceConnected, "usb", null)]
    [InlineData(RuleTriggers.PowerPlugged, null, null)]
    public async Task NonMatchingEventsDoNotFire(string type, string? kind, string? name)
    {
        await Add(RuleSamples.Headphones());
        Assert.Empty(await _engine.HandleEventAsync(new RuleEvent(type, kind, name), default));
    }

    [Fact]
    public async Task TriggerFieldsMatchAsDocumented()
    {
        Assert.True(RuleEngine.Matches(new RuleTrigger(RuleTriggers.DeviceConnected, "usb", "keyboard"),
            new RuleEvent(RuleTriggers.DeviceConnected, "usb", "Logitech USB Keyboard")));
        Assert.False(RuleEngine.Matches(new RuleTrigger(RuleTriggers.DeviceConnected, "usb", "keyboard"),
            new RuleEvent(RuleTriggers.DeviceConnected, "usb", null)));
        Assert.True(RuleEngine.Matches(new RuleTrigger(RuleTriggers.AppOpened, AppId: "Steam"), new RuleEvent(RuleTriggers.AppOpened, AppId: "steam")));
        Assert.True(RuleEngine.Matches(new RuleTrigger(RuleTriggers.NetworkJoined, Ssid: "Home"), new RuleEvent(RuleTriggers.NetworkJoined, Ssid: "home")));
        Assert.False(RuleEngine.Matches(new RuleTrigger(RuleTriggers.NetworkJoined, Ssid: "Home"), new RuleEvent(RuleTriggers.NetworkJoined, Ssid: "Home-5G")));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task PausedRulesDoNotFire()
    {
        var rule = await Add(RuleSamples.Headphones(), enabled: false);
        Assert.Empty(await _engine.HandleEventAsync(new RuleEvent(RuleTriggers.DeviceConnected, "audio"), default));
        await _store.SetEnabledAsync(rule.Id, true, default);
        Assert.Single(await _engine.HandleEventAsync(new RuleEvent(RuleTriggers.DeviceConnected, "audio"), default));
    }

    [Fact]
    public async Task ARuleFiresAtMostOncePerCooldown()
    {
        await Add(RuleSamples.Headphones());
        var connected = new RuleEvent(RuleTriggers.DeviceConnected, "audio");
        Assert.Single(await _engine.HandleEventAsync(connected, default));
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(await _engine.HandleEventAsync(connected, default));
        _time.Advance(RuleEngine.Cooldown);
        Assert.Single(await _engine.HandleEventAsync(connected, default));
    }

    [Fact]
    public async Task ConditionsGateFiring()
    {
        await Add(new RuleDefinition(new RuleTrigger(RuleTriggers.DeviceConnected, "audio"),
            [new RuleCondition(RuleConditions.TimeBetween, From: "22:00", To: "07:00"), new RuleCondition(RuleConditions.Days, Days: ["mon"])],
            [RuleSamples.OpenSpotify]));
        var e = new RuleEvent(RuleTriggers.DeviceConnected, "audio");
        Assert.Empty(await _engine.HandleEventAsync(e, default)); // 08:59 Monday: outside the range.
        _time.Now = new DateTimeOffset(2026, 10, 19, 23, 0, 0, TimeSpan.Zero);
        Assert.Single(await _engine.HandleEventAsync(e, default)); // 23:00 Monday: wraps past midnight.
        _time.Now = new DateTimeOffset(2026, 10, 20, 6, 0, 0, TimeSpan.Zero);
        Assert.Empty(await _engine.HandleEventAsync(e, default)); // Tuesday.
    }

    [Fact]
    public async Task PowerConditionUsesReportedStateAndUnknownNeverHolds()
    {
        await Add(new RuleDefinition(new RuleTrigger(RuleTriggers.DeviceConnected, "display"),
            [new RuleCondition(RuleConditions.PowerIs, State: "plugged")], [RuleSamples.OpenSpotify]));
        var e = new RuleEvent(RuleTriggers.DeviceConnected, "display");
        Assert.Null(_engine.PowerState);
        Assert.Empty(await _engine.HandleEventAsync(e, default));
        await _engine.HandleEventAsync(new RuleEvent(RuleTriggers.PowerPlugged), default);
        Assert.Equal("plugged", _engine.PowerState);
        Assert.Single(await _engine.HandleEventAsync(e, default));
    }

    [Fact]
    public async Task TimeRulesFireOncePerDayIntoThePendingQueue()
    {
        var rule = await Add(RuleSamples.Daily("09:00", "mon", "tue"));
        Assert.Equal(0, await _engine.TickAsync(default)); // 08:59:30.
        _time.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal(1, await _engine.TickAsync(default)); // 09:00:10.
        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(0, await _engine.TickAsync(default)); // Already fired today.
        var (items, next) = _engine.Pending(0);
        var pending = Assert.Single(items);
        Assert.Equal(rule.Id, pending.RuleId);
        Assert.Equal(pending.Seq, next);
        Assert.Empty(_engine.Pending(next).Items); // The cursor skips what the shell has seen.
        Assert.Single(_engine.Pending(0).Items);   // Reading does not remove.
        Assert.Equal("queued", Assert.Single((await _store.HistoryAsync(rule.Id, 10, default))!).Delivery);
        // Queued actions expire rather than run late.
        _time.Advance(RuleEngine.PendingLifetime);
        Assert.Empty(_engine.Pending(0).Items);
        // Next day (Tuesday) it fires again; Wednesday is not in its days.
        _time.Now = new DateTimeOffset(2026, 10, 20, 9, 1, 0, TimeSpan.Zero);
        Assert.Equal(1, await _engine.TickAsync(default));
        _time.Now = new DateTimeOffset(2026, 10, 21, 9, 1, 0, TimeSpan.Zero);
        Assert.Equal(0, await _engine.TickAsync(default));
    }

    [Fact]
    public async Task LateTicksOutsideTheWindowDoNotFire()
    {
        await Add(RuleSamples.Daily("09:00"));
        _time.Now = new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero) + RuleEngine.DailyWindow;
        Assert.Equal(0, await _engine.TickAsync(default));
    }

    [Fact]
    public async Task TimeTriggersUseTheLocalTimeZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus-two", TimeSpan.FromHours(2), "plus-two", "plus-two");
        var time = new FakeTime(new DateTimeOffset(2026, 10, 19, 7, 0, 30, TimeSpan.Zero), zone); // 09:00:30 local.
        var engine = new RuleEngine(new RuleStore(new SqliteStateDatabase(_state.Paths()), _state.Config(), time), time);
        await Add(RuleSamples.Daily("09:00"));
        Assert.Equal(1, await engine.TickAsync(default));
    }

    [Fact]
    public async Task PausingARuleDropsItsQueuedActions()
    {
        var rule = await Add(RuleSamples.Daily("09:00"));
        _time.Advance(TimeSpan.FromSeconds(40));
        await _engine.TickAsync(default);
        _engine.Forget(rule.Id);
        Assert.Empty(_engine.Pending(0).Items);
    }

    [Fact]
    public async Task CursorFromBeforeARestartStartsOver()
    {
        await Add(RuleSamples.Daily("09:00"));
        _time.Advance(TimeSpan.FromSeconds(40));
        await _engine.TickAsync(default);
        Assert.Single(_engine.Pending(500).Items);
    }

    [Fact]
    public async Task DeletedRulesStopFiring()
    {
        var rule = await Add(RuleSamples.Headphones());
        await _store.DeleteAsync(rule.Id, default);
        Assert.Empty(await _engine.HandleEventAsync(new RuleEvent(RuleTriggers.DeviceConnected, "audio"), default));
    }
}
