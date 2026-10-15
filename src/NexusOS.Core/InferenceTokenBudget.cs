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

public sealed class InferenceTokenSettings(IConfiguration config)
{
    public InferenceTokenBudget Budget
    {
        get
        {
            var ctx = int.TryParse(config["NEXUS_CONTEXT_TOKENS"], out var c) ? c : 4096;
            var output = int.TryParse(config["NEXUS_MAX_OUTPUT_TOKENS"], out var o) ? o : 512;
            return InferenceTokenBudgetPlanner.Calculate(ctx, output);
        }
    }
}
