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
}
