using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class InferenceConcurrencyGateTests
{
    private static InferenceConcurrencyGate Create(int? count = null)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NEXUS_MAX_CONCURRENT_INFERENCE"] = count?.ToString()
            }).Build();
        return new InferenceConcurrencyGate(cfg);
    }

    [Fact]
    public async Task BusyModelRejectsSecondRequestAndRecoversAfterRelease()
    {
        var gate = Create();
        Assert.Equal(1, gate.Limit);
        using (var first = await gate.TryEnterAsync(CancellationToken.None))
        {
            Assert.NotNull(first);
            Assert.Null(await gate.TryEnterAsync(CancellationToken.None));
        }
        using var next = await gate.TryEnterAsync(CancellationToken.None);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task ReleasesPermitOnlyOnce()
    {
        var gate = Create();
        var lease = await gate.TryEnterAsync(CancellationToken.None);
        Assert.NotNull(lease);
        lease!.Dispose();
        lease.Dispose();
        using var first = await gate.TryEnterAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.Null(await gate.TryEnterAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SupportsControlledParallelism()
    {
        var gate = Create(2);
        using var first = await gate.TryEnterAsync(CancellationToken.None);
        using var second = await gate.TryEnterAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(await gate.TryEnterAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(-99, 1)]
    [InlineData(1000, 8)]
    public void BoundsConfiguredConcurrency(int configured, int expected) =>
        Assert.Equal(expected, Create(configured).Limit);
}
