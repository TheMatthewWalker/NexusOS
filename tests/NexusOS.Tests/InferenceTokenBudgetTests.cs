using Xunit;

public sealed class InferenceTokenBudgetTests
{
    [Fact]
    public void AllocatesOutputReserve()
    {
        var budget = InferenceTokenBudgetPlanner.Calculate(4096, 512);
        Assert.Equal(3584, budget.MaxInputTokens);
    }

    [Theory]
    [InlineData(511, 256)]
    [InlineData(4096, 4096)]
    [InlineData(4096, 0)]
    [InlineData(131073, 1024)]
    public void RejectsInvalidBudgets(int context, int output) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => InferenceTokenBudgetPlanner.Calculate(context, output));
}
