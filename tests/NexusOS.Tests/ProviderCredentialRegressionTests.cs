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
}
