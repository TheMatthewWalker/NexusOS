using Xunit;

public sealed class InferenceActivityTests
{
    [Fact]
    public void TouchResetsElapsedIdleTime()
    {
        var activity = new InferenceActivity();
        Assert.True(activity.MillisecondsIdle >= 0);
        activity.Touch();
        Assert.InRange(activity.MillisecondsIdle, 0, 1000);
    }
}
