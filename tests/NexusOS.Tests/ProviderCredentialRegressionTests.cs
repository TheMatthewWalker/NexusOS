using Xunit;

public sealed class ProviderCredentialRegressionTests
{
    [Fact]
    public async Task UnsupportedProviderIsRejectedBeforeAnyLookup()
    {
        var store = new LinuxSecretToolStore();
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.GetAsync("untrusted-provider", CancellationToken.None));
    }

    [Fact]
    public async Task AlreadyCancelledLookupDoesNotSucceed()
    {
        if (!OperatingSystem.IsLinux()) return;
        var store = new LinuxSecretToolStore();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.GetAsync("openai-compatible", cancelled.Token));
    }
    [Fact]
    public async Task MissingExecutableFailsClosed()
    {
        if (!OperatingSystem.IsLinux()) return;
        var store = new LinuxSecretToolStore("/nexusos-no-such-secret-tool", TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetAsync("openai-compatible", CancellationToken.None));
    }

}
