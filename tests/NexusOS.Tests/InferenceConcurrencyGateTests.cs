using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class InferenceConcurrencyGateTests
{
    private static InferenceConcurrencyGate Create(int? count = null, int? waitSeconds = null)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NEXUS_MAX_CONCURRENT_INFERENCE"] = count?.ToString(),
                ["NEXUS_INFERENCE_WAIT_SECONDS"] = waitSeconds?.ToString()
            }).Build();
        return new InferenceConcurrencyGate(cfg);
    }

    [Fact]
    public async Task ASecondRequestWaitsForTheFirst()
    {
        var gate = Create();
        Assert.Equal(1, gate.Limit);
        var first = await gate.EnterAsync(CancellationToken.None);
        Assert.NotNull(first);
        var second = gate.EnterAsync(CancellationToken.None).AsTask();
        await Task.Delay(100);
        Assert.False(second.IsCompleted);
        first!.Dispose();
        using var next = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(next);
    }

    [Fact]
    public async Task WaitingGivesUpOnlyAfterMaxWait()
    {
        var gate = Create(waitSeconds: 1);
        using var first = await gate.EnterAsync(CancellationToken.None);
        Assert.Null(await gate.EnterAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BackgroundWorkTakesOnlyAFreeSlotAndStepsAsideForAQuestion()
    {
        var gate = Create();
        var background = gate.TryEnterBackground(CancellationToken.None);
        Assert.NotNull(background);
        Assert.Null(gate.TryEnterBackground(CancellationToken.None));
        var question = gate.EnterAsync(CancellationToken.None).AsTask();
        // The question asks the background work to stop; it does, and gives the slot back.
        Assert.True(background!.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
        background.Dispose();
        using var turn = await question.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(turn);
    }

    [Fact]
    public async Task ReleasesPermitOnlyOnce()
    {
        var gate = Create();
        var lease = await gate.EnterAsync(CancellationToken.None);
        Assert.NotNull(lease);
        lease!.Dispose();
        lease.Dispose();
        using var first = await gate.EnterAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.Null(gate.TryEnterBackground(CancellationToken.None));
    }

    [Fact]
    public async Task SupportsControlledParallelism()
    {
        var gate = Create(2);
        using var first = await gate.EnterAsync(CancellationToken.None);
        using var second = await gate.EnterAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(gate.TryEnterBackground(CancellationToken.None));
    }

    [Theory]
    [InlineData(-99, 1)]
    [InlineData(1000, 8)]
    public void BoundsConfiguredConcurrency(int configured, int expected) =>
        Assert.Equal(expected, Create(configured).Limit);
}
