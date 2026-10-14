using Xunit;

public sealed class InferenceBudgetTests
{
    [Fact]
    public void LeavesHeadroomForOtherProcesses()
    {
        var plan = InferenceBudgetPlanner.Calculate(8_000_000_000, 25);
        Assert.Equal(2_000_000_000, plan.ReserveBytes);
        Assert.Equal(6_000_000_000, plan.AiBudgetBytes);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(1, -1)]
    [InlineData(1, 101)]
    public void RejectsBadValues(long total, int reserve) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => InferenceBudgetPlanner.Calculate(total, reserve));
}
