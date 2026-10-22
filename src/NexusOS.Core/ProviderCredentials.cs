using System.ComponentModel;
using System.Diagnostics;
using System.Text;

public interface IProviderCredentialStore
{
    Task<string?> GetAsync(string provider, CancellationToken ct);
}

public sealed class LinuxSecretToolStore : IProviderCredentialStore
{
    private const int MaxCredentialCharacters = 8192;
    private readonly string _executable;
    private readonly TimeSpan _timeout;

    public LinuxSecretToolStore() : this("secret-tool", TimeSpan.FromSeconds(10)) { }

    // Explicit executable and timeout support isolated process-level regression tests.
    public LinuxSecretToolStore(string executable, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("Executable required.", nameof(executable));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _executable = executable;
        _timeout = timeout;
    }

    public async Task<string?> GetAsync(string provider, CancellationToken ct)
    {
        if (provider != "openai-compatible")
            throw new ArgumentException("Unsupported provider.", nameof(provider));
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException("OS credential lookup requires Linux Secret Service.");
        ct.ThrowIfCancellationRequested();

        var start = new ProcessStartInfo
        {
            FileName = _executable,
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

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        try
        {
            // Drain both pipes concurrently, with an upper bound. Overflow kills the
            // child immediately, so WaitForExitAsync cannot deadlock on a full pipe.
            var outputTask = ReadBoundedAsync(process.StandardOutput, process, MaxCredentialCharacters + 2, deadline.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, process, MaxCredentialCharacters + 2, deadline.Token);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), outputTask, errorTask);
            if (process.ExitCode != 0) return null;
            var credential = outputTask.Result.TrimEnd('\r', '\n');
            if (credential.Length == 0) return null;
            if (credential.Length > MaxCredentialCharacters ||
                credential.Any(ch => char.IsControl(ch) || char.IsWhiteSpace(ch)))
                throw new InvalidOperationException("OS keyring returned an invalid credential.");
            return credential;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Terminate(process);
            throw new InvalidOperationException("OS credential lookup timed out.");
        }
        catch
        {
            Terminate(process);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, Process process, int max, CancellationToken ct)
    {
        var buffer = new char[1024];
        var result = new StringBuilder();
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) return result.ToString();
            if (result.Length + count > max)
            {
                Terminate(process);
                throw new InvalidOperationException("OS keyring output exceeded its permitted size.");
            }
            result.Append(buffer, 0, count);
        }
    }

    private static void Terminate(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
