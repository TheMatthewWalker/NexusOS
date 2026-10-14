using System.ComponentModel;
using System.Diagnostics;

public interface IProviderCredentialStore
{
    Task<string?> GetAsync(string provider, CancellationToken ct);
}

public sealed class LinuxSecretToolStore : IProviderCredentialStore
{
    public async Task<string?> GetAsync(string provider, CancellationToken ct)
    {
        if (provider != "openai-compatible") throw new ArgumentException("Unsupported provider.", nameof(provider));
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException("OS credential lookup requires Linux Secret Service.");
        var start = new ProcessStartInfo
        {
            FileName = "secret-tool",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var part in new[] { "lookup", "app", "nexusos", "provider", provider })
            start.ArgumentList.Add(part);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) return null; }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("Install libsecret secret-tool and unlock your login keyring.");
        }
        try
        {
            var read = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var output = await read;
            _ = await stderr; // Do not log secrets or tool diagnostics.
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output.TrimEnd('\r', '\n') : null;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
