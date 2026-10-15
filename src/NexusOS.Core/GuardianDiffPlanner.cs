public sealed record GuardianDiffEntry(string Option, bool Before, bool After);
public sealed record GuardianDiff(string PlanId, IReadOnlyList<GuardianDiffEntry> Differences, string Risk, bool UsesObservedBaseline);

public static class GuardianDiffPlanner
{
    // Caller-provided baseline is advisory. A privileged verifier must independently
    // query the active NixOS generation before any deployment.
    public static GuardianDiff Compare(string description, IReadOnlyList<GuardianChange> changes,
        IReadOnlyDictionary<string, bool> baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var plan = GuardianPlanner.Preview(description, changes);
        var expected = plan.Changes.Select(x => x.Option).ToHashSet(StringComparer.Ordinal);
        if (baseline.Count != expected.Count || baseline.Keys.Any(key => !expected.Contains(key)))
            throw new ArgumentException("Baseline must specify exactly the proposed options.", nameof(baseline));
        var differences = plan.Changes.Where(x => baseline[x.Option] != x.Enabled)
            .Select(x => new GuardianDiffEntry(x.Option, baseline[x.Option], x.Enabled))
            .ToArray();
        return new GuardianDiff(plan.Id, differences, plan.Risk, false);
    }
}
