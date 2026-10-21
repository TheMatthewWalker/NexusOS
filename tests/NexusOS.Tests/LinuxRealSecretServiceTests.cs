using System.Diagnostics;
using Xunit;

[CollectionDefinition("IsolatedRealSecretService", DisableParallelization = true)]
public sealed class IsolatedRealSecretServiceCollection { }

[Collection("IsolatedRealSecretService")]
public sealed class LinuxRealSecretServiceTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("NEXUS_REAL_SECRET_SERVICE_TEST") == "1";

    [Fact]
    public async Task UnlockedKeyringWorksAndMissingSecretNeverUsesEnvironment()
    {
        if (!Enabled) return;
        Assert.True(OperatingSystem.IsLinux());
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")));
        var secret = "nexus-integration-" + Guid.NewGuid().ToString("N");
        var store = new LinuxSecretToolStore("secret-tool", TimeSpan.FromSeconds(5));
        await SecretToolAsync(new[] { "store", "--label=NexusOS isolated integration", "app", "nexusos", "provider", "openai-compatible" }, secret + "\n");
        try
        {
            Assert.Equal(secret, await store.GetAsync("openai-compatible", CancellationToken.None));
        }
        finally
        {
            await SecretToolAsync(new[] { "clear", "app", "nexusos", "provider", "openai-compatible" });
        }
        var old = Environment.GetEnvironmentVariable("NEXUS_OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("NEXUS_OPENAI_API_KEY", "not-an-authorized-credential");
            Assert.Null(await store.GetAsync("openai-compatible", CancellationToken.None));
        }
        finally { Environment.SetEnvironmentVariable("NEXUS_OPENAI_API_KEY", old); }
    }

    [Fact]
    public async Task UnavailableSessionBusFailsClosedDespitePlaintextEnvironment()
    {
        if (!Enabled) return;
        var oldBus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        var oldKey = Environment.GetEnvironmentVariable("NEXUS_OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "unix:path=/nonexistent/nexusos-keyring-bus");
            Environment.SetEnvironmentVariable("NEXUS_OPENAI_API_KEY", "not-an-authorized-credential");
            var store = new LinuxSecretToolStore("secret-tool", TimeSpan.FromSeconds(3));
            string? result = null;
            try { result = await store.GetAsync("openai-compatible", CancellationToken.None); }
            catch (InvalidOperationException) { }
            Assert.Null(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", oldBus);
            Environment.SetEnvironmentVariable("NEXUS_OPENAI_API_KEY", oldKey);
        }
    }

    [Fact]
    public async Task LockedCollectionCannotSendCloudRequest()
    {
        if (!Enabled || Environment.GetEnvironmentVariable("NEXUS_EXPECT_LOCKED_KEYRING") != "1") return;
        var store = new LinuxSecretToolStore("secret-tool", TimeSpan.FromSeconds(3));
        string? credential = null;
        try { credential = await store.GetAsync("openai-compatible", CancellationToken.None); }
        catch (InvalidOperationException) { }
        Assert.Null(credential);
    }

    private static async Task SecretToolAsync(string[] args, string? input = null)
    {
        var start = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("secret-tool failed to start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (input != null) await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Secret Service operation exceeded its timeout.");
        }
        Assert.True(process.ExitCode == 0, "Isolated Secret Service operation failed; diagnostic output withheld.");
    }
}
