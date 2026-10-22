using System.Diagnostics;
using Xunit;

public sealed class LinuxSecretToolProcessTests
{
    [Fact]
    public async Task HungHelperTimesOut()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HelperFixture("sleep 10");
        var store = new LinuxSecretToolStore(fixture.Executable, TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetAsync("openai-compatible", CancellationToken.None));
        Assert.Contains("timed out", exception.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancellationStopsHungHelper()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HelperFixture("sleep 10");
        var store = new LinuxSecretToolStore(fixture.Executable, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.GetAsync("openai-compatible", cancellation.Token));
    }

    private sealed class HelperFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "nexus-keyring-" + Guid.NewGuid().ToString("N"));
        public string Executable => Path.Combine(_directory, "helper");
        public HelperFixture(string command)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Executable, "#!/bin/sh\n" + command + "\n");
            File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
