// Actions a rule asks the shell to perform. Core cannot open apps or change
// shell settings itself; the shell performs these through its own allow-list.
public sealed record RuleFiring(string RuleId, string RuleName, long? RunId, IReadOnlyList<RuleAction> Actions);

public sealed record PendingRuleActions(long Seq, DateTimeOffset QueuedAt, DateTimeOffset ExpiresAt, string RuleId,
    string RuleName, long? RunId, IReadOnlyList<RuleAction> Actions);

// Evaluates rules when events arrive and on a timer. It only matches data and
// records runs; it never executes anything.
public sealed class RuleEngine(RuleStore store, TimeProvider time)
{
    // A rule fires at most once a minute, so a flapping device or two rules
    // that open each other's apps cannot loop.
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    // Late ticks (suspend, a busy machine) still fire inside this window.
    public static readonly TimeSpan DailyWindow = TimeSpan.FromMinutes(2);
    // Queued actions go stale: opening an app hours after its trigger would surprise.
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(5);
    public const int MaxPending = 100;

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastFired = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateOnly> _dailyFired = new(StringComparer.Ordinal);
    private readonly LinkedList<PendingRuleActions> _pending = new();
    private long _seq;
    private string? _power;

    // The last power state the shell reported; null until it reports one.
    // Conditions on an unknown state are never treated as met.
    public string? PowerState { get { lock (_gate) return _power; } }

    public async Task<IReadOnlyList<RuleFiring>> HandleEventAsync(RuleEvent e, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (e.Type == RuleTriggers.PowerPlugged) _power = "plugged";
            else if (e.Type == RuleTriggers.PowerUnplugged) _power = "unplugged";
        }
        var firings = new List<RuleFiring>();
        foreach (var rule in await store.ListAsync(ct))
        {
            if (!rule.Enabled || !Matches(rule.Trigger, e) || !ConditionsHold(rule.Conditions, now)) continue;
            if (!TryClaim(rule.Id, now)) continue;
            var run = await store.RecordRunAsync(rule.Id, now, RuleSchema.DescribeEvent(e), rule.Actions, "events-response", ct);
            firings.Add(new RuleFiring(rule.Id, rule.Name, run, rule.Actions));
        }
        return firings;
    }

    // Fires time.daily rules whose time has come. Returns how many fired.
    public async Task<int> TickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, time.LocalTimeZone);
        var today = DateOnly.FromDateTime(local.DateTime);
        var fired = 0;
        foreach (var rule in await store.ListAsync(ct))
        {
            if (!rule.Enabled || rule.Trigger.Type != RuleTriggers.TimeDaily) continue;
            if (rule.Trigger.Days is { } days && !days.Contains(DayName(local.DayOfWeek))) continue;
            var at = TimeOnly.ParseExact(rule.Trigger.Time!, "HH:mm", System.Globalization.CultureInfo.InvariantCulture).ToTimeSpan();
            var since = local.TimeOfDay - at;
            if (since < TimeSpan.Zero || since >= DailyWindow) continue;
            if (!ConditionsHold(rule.Conditions, now)) continue;
            lock (_gate)
            {
                if (_dailyFired.TryGetValue(rule.Id, out var last) && last == today) continue;
                _dailyFired[rule.Id] = today;
                _lastFired[rule.Id] = now;
            }
            var run = await store.RecordRunAsync(rule.Id, now, $"time {rule.Trigger.Time}", rule.Actions, "queued", ct);
            lock (_gate)
            {
                _pending.AddLast(new PendingRuleActions(++_seq, now, now + PendingLifetime, rule.Id, rule.Name, run, rule.Actions));
                while (_pending.Count > MaxPending) _pending.RemoveFirst();
            }
            fired++;
        }
        return fired;
    }

    // Queued actions after the shell's cursor, oldest first. Reading does not
    // remove them, so a retried request cannot lose actions; they expire instead.
    public (IReadOnlyList<PendingRuleActions> Items, long Next) Pending(long after)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            // A cursor from before Core restarted is ahead of the new sequence: start over.
            if (after > _seq) after = 0;
            while (_pending.First is { } first && first.Value.ExpiresAt <= now) _pending.RemoveFirst();
            var items = _pending.Where(p => p.Seq > after).ToList();
            return (items, Math.Max(after, _seq));
        }
    }

    // Drops queued actions and cooldowns of a rule that was paused or removed.
    public void Forget(string ruleId)
    {
        lock (_gate)
        {
            for (var node = _pending.First; node is not null;)
            {
                var next = node.Next;
                if (node.Value.RuleId == ruleId) _pending.Remove(node);
                node = next;
            }
        }
    }

    private bool TryClaim(string ruleId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_lastFired.TryGetValue(ruleId, out var last) && now - last < Cooldown) return false;
            _lastFired[ruleId] = now;
            return true;
        }
    }

    public static bool Matches(RuleTrigger trigger, RuleEvent e)
    {
        if (trigger.Type != e.Type) return false;
        return e.Type switch
        {
            RuleTriggers.DeviceConnected or RuleTriggers.DeviceDisconnected => trigger.Kind == e.Kind &&
                (trigger.Name is null || (e.Name?.Contains(trigger.Name, StringComparison.OrdinalIgnoreCase) ?? false)),
            RuleTriggers.AppOpened => string.Equals(trigger.AppId, e.AppId, StringComparison.OrdinalIgnoreCase),
            RuleTriggers.NetworkJoined => string.Equals(trigger.Ssid, e.Ssid, StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    private bool ConditionsHold(IReadOnlyList<RuleCondition> conditions, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, time.LocalTimeZone);
        var clock = TimeOnly.FromTimeSpan(local.TimeOfDay);
        foreach (var condition in conditions)
        {
            var holds = condition.Type switch
            {
                RuleConditions.TimeBetween => Between(clock, TimeOnly.ParseExact(condition.From!, "HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                    TimeOnly.ParseExact(condition.To!, "HH:mm", System.Globalization.CultureInfo.InvariantCulture)),
                RuleConditions.Days => condition.Days!.Contains(DayName(local.DayOfWeek)),
                _ => PowerState is { } state && state == condition.State,
            };
            if (!holds) return false;
        }
        return true;
    }

    // [from, to), wrapping past midnight when to is earlier than from.
    private static bool Between(TimeOnly now, TimeOnly from, TimeOnly to) =>
        from < to ? now >= from && now < to : now >= from || now < to;

    private static string DayName(DayOfWeek day) => RuleSchema.DayNames[((int)day + 6) % 7];
}

public sealed class RuleTimerService(RuleEngine engine, ILogger<RuleTimerService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await engine.TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            // Never log rule contents; they can name the person's devices and networks.
            catch (Exception ex) { logger.LogWarning("Rule timer tick failed: {Type}", ex.GetType().Name); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
