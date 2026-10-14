using Xunit;

public sealed class ProviderCredentialTests
{
    [Fact]
    public async Task RejectsUnknownCredentialNamespace()
    {
        IProviderCredentialStore store = new LinuxSecretToolStore();
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync("random-provider", CancellationToken.None));
    }
}
