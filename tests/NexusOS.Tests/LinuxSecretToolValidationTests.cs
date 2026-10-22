using Xunit;

public sealed class LinuxSecretToolValidationTests
{
    [Theory]
    [InlineData("printf 'bad token\\n'")]
    [InlineData("printf 'bad\\tvalue\\n'")]
    [InlineData("printf 'bad\\rvalue\\n'")]
    public async Task MalformedCredentialsFailClosed(string command)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var helper = new TemporaryHelper(command);
        var store = new LinuxSecretToolStore(helper.Path, TimeSpan.FromSeconds(2));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetAsync("openai-compatible", CancellationToken.None));
        Assert.DoesNotContain("bad token", error.Message);
        Assert.DoesNotContain("bad\\tvalue", error.Message);
    }

    [Fact]
    public async Task ExcessiveOutputFailsClosedWithoutLeakingSecret()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var helper = new TemporaryHelper("head -c 12000 /dev/zero | tr '\\000' X");
        var store = new LinuxSecretToolStore(helper.Path, TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetAsync("openai-compatible", CancellationToken.None));
        Assert.Contains("permitted size", error.Message);
        Assert.DoesNotContain(new string('X', 20), error.Message);
    }

    private sealed class TemporaryHelper : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-keyring-validation-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(directory, "helper");
        public TemporaryHelper(string command)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path, "#!/bin/sh\n" + command + "\n");
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public void Dispose() => Directory.Delete(directory, true);
    }
}
