using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

public sealed record MemoryTelemetrySnapshot(long? RamTotalBytes, long? RamAvailableBytes, long? VramTotalBytes, long? VramFreeBytes, string GpuSource);

public sealed class MemoryTelemetry
{
    public static (long? Total, long? Available) ParseMemInfo(string content)
    {
        long? total = null, available = null;
        foreach (var line in content.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb)) continue;
            if (fields[0] == "MemTotal:") total = kb * 1024;
            if (fields[0] == "MemAvailable:") available = kb * 1024;
        }
        return (total, available);
    }

    public static (long? Total, long? Free) ParseNvidiaMemory(string output)
    {
        var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var columns = first?.Split(',', StringSplitOptions.TrimEntries);
        if (columns?.Length < 2 ||
            !long.TryParse(columns[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total) ||
            !long.TryParse(columns[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var free))
            return (null, null);
        return (total * 1024 * 1024, free * 1024 * 1024);
    }

    public async Task<MemoryTelemetrySnapshot> ReadAsync(CancellationToken ct)
    {
        var ram = OperatingSystem.IsLinux() && File.Exists("/proc/meminfo")
            ? ParseMemInfo(await File.ReadAllTextAsync("/proc/meminfo", ct))
            : (null, null);
        var gpu = await ReadNvidiaAsync(ct);
        return new(ram.Item1, ram.Item2, gpu.Total, gpu.Free, gpu.Total is null ? "unavailable" : "nvidia-smi");
    }

    private static async Task<(long? Total, long? Free)> ReadNvidiaAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return (null, null);
        var info = new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("--query-gpu=memory.total,memory.free");
        info.ArgumentList.Add("--format=csv,noheader,nounits");
        using var process = new Process { StartInfo = info };
        try { if (!process.Start()) return (null, null); }
        catch (Win32Exception) { return (null, null); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            _ = await errors;
            return process.ExitCode == 0 ? ParseNvidiaMemory(await output) : (null, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return (null, null);
        }
    }
}
