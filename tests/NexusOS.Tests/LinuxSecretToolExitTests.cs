using Xunit;

public sealed class LinuxSecretToolExitTests
{
    [Fact]
    public async Task EmptyOutputCannotAuthenticate()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/true")) return;
        var store = new LinuxSecretToolStore("/usr/bin/true", TimeSpan.FromSeconds(2));
        Assert.Null(await store.GetAsync("openai-compatible", CancellationToken.None));
    }
    [Fact]
    public async Task FailedProcessCannotAuthenticate()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/false")) return;
        var store = new LinuxSecretToolStore("/usr/bin/false", TimeSpan.FromSeconds(2));
        Assert.Null(await store.GetAsync("openai-compatible", CancellationToken.None));
    }

    [Fact]
    public async Task WhitespaceCredentialCannotAuthenticate()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/echo")) return;
        var store = new LinuxSecretToolStore("/usr/bin/echo", TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetAsync("openai-compatible", CancellationToken.None));
    }

}
