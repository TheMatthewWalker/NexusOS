public sealed record InferenceTokenBudget(int ContextTokens, int ReservedOutputTokens, int MaxInputTokens);

public static class InferenceTokenBudgetPlanner
{
    public static InferenceTokenBudget Calculate(int contextTokens, int outputTokens)
    {
        if (contextTokens < 512 || contextTokens > 131072)
            throw new ArgumentOutOfRangeException(nameof(contextTokens));
        if (outputTokens < 1 || outputTokens >= contextTokens)
            throw new ArgumentOutOfRangeException(nameof(outputTokens));
        return new(contextTokens, outputTokens, contextTokens - outputTokens);
    }
}

// Parsed once so an invalid budget stops Core at startup instead of failing
// every chat request.
public sealed class InferenceTokenSettings
{
    public InferenceTokenBudget Budget { get; }

    public InferenceTokenSettings(IConfiguration config)
    {
        var ctx = Parse(config["NEXUS_CONTEXT_TOKENS"], 4096, "NEXUS_CONTEXT_TOKENS");
        var output = Parse(config["NEXUS_MAX_OUTPUT_TOKENS"], 512, "NEXUS_MAX_OUTPUT_TOKENS");
        try { Budget = InferenceTokenBudgetPlanner.Calculate(ctx, output); }
        catch (ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException(
                "Invalid token budget: NEXUS_CONTEXT_TOKENS must be 512-131072 and NEXUS_MAX_OUTPUT_TOKENS 1 to less than the context.");
        }
    }

    private static int Parse(string? raw, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw, out var value) ? value : throw new InvalidOperationException($"{name} must be an integer.");
    }
}
