using Xunit;

public sealed class GuardianReviewTokensTests
{
    [Fact]
    public void TokenCanOnlyBeUsedOnce()
    {
        var reviews = new GuardianReviewTokens();
        var token = reviews.Issue("plan-one");
        Assert.True(reviews.Consume(token, "plan-one"));
        Assert.False(reviews.Consume(token, "plan-one"));
    }

    [Fact]
    public void WrongPlanConsumesAttemptWithoutApproval()
    {
        var reviews = new GuardianReviewTokens();
        var token = reviews.Issue("plan-one");
        Assert.False(reviews.Consume(token, "plan-two"));
        Assert.False(reviews.Consume(token, "plan-one"));
    }

    [Fact]
    public void ExpiredReviewCannotBeAcknowledged()
    {
        var now = DateTimeOffset.UtcNow;
        var reviews = new GuardianReviewTokens(() => now);
        var token = reviews.Issue("plan-one");
        now = now.AddMinutes(3);
        Assert.False(reviews.Consume(token, "plan-one"));
    }
}
