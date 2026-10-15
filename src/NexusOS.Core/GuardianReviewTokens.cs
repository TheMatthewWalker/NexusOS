using System.Collections.Concurrent;
using System.Security.Cryptography;

public sealed class GuardianReviewTokens
{
    private sealed record Pending(string PlanId, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, Pending> _tokens = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now;
    public GuardianReviewTokens() : this(() => DateTimeOffset.UtcNow) { }
    public GuardianReviewTokens(Func<DateTimeOffset> now) => _now = now;

    public string Issue(string planId)
    {
        if (string.IsNullOrWhiteSpace(planId)) throw new ArgumentException("A plan ID is required.", nameof(planId));
        foreach (var pair in _tokens)
            if (pair.Value.Expires <= _now()) _tokens.TryRemove(pair.Key, out _);
        if (_tokens.Count >= 200) throw new InvalidOperationException("Too many outstanding Guardian reviews.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        if (!_tokens.TryAdd(token, new Pending(planId, _now().AddMinutes(2))))
            throw new InvalidOperationException("Could not create a unique review token.");
        return token;
    }

    public bool Consume(string? token, string planId)
    {
        if (string.IsNullOrEmpty(token)) return false;
        return _tokens.TryRemove(token, out var pending)
            && pending.PlanId == planId && pending.Expires > _now();
    }
}
