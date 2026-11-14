using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

// The large local model: a mixture-of-experts (MoE) model, far bigger than
// the standard one, on a computer that could not hold a dense model of its
// size. Each word such a model writes uses only a few of its "experts", so
// the experts can sit in ordinary memory while the small part every word
// needs goes on the graphics card (llama-server --n-cpu-moe). Core's managed
// llama.cpp server runs it (LlamaServerProcess); the model file is fetched by
// nexus-large-model.service (nix/modules/large-model.nix).
//
// Built with 16 GB computers in mind:
//  - the split is worked out from the graphics memory actually there (MoePlan);
//  - before the server starts, Core checks the part kept in memory fits in
//    what is free now, and says how much is short rather than push the
//    computer into swapping;
//  - while the large model cannot be used (not downloaded, not enough free
//    memory, failed to start) questions go to the standard model, and the
//    reason is shown in Settings (ModelRouter.Current).
public sealed record MoeSplit(int GpuLayers, int CpuMoeLayers, long MemoryBytes, string Where);

public static class MoePlan
{
    public const long GiB = 1024L * 1024 * 1024;
    // Kept free on the graphics card for the desktop and the model's working memory.
    public const long GpuReserve = 3 * GiB / 2;
    // The part of a MoE model every word uses (attention, embeddings); the rest is experts.
    public const double SharedShare = 0.12;
    public const int AllLayers = 999;

    // vramBytes: the graphics card's own memory (0: none, or a built-in GPU sharing the computer's).
    public static MoeSplit For(long vramBytes, long modelBytes, int layers)
    {
        layers = Math.Max(1, layers);
        var budget = vramBytes - GpuReserve;
        var shared = (long)(modelBytes * SharedShare);
        if (vramBytes < 3 * GiB || budget < shared + (modelBytes - shared) / layers)
            return new MoeSplit(0, 0, modelBytes + GiB, "on the processor, in memory");
        if (budget >= modelBytes + modelBytes / 20)
            return new MoeSplit(AllLayers, 0, GiB, "on the graphics card");
        var perLayer = (modelBytes - shared) / (double)layers;
        var onGpu = Math.Clamp((int)((budget - shared) / perLayer), 0, layers);
        var inMemory = layers - onGpu;
        return new MoeSplit(AllLayers, inMemory, (long)(inMemory * perLayer) + GiB,
            inMemory == layers ? "shared parts on the graphics card, experts in memory"
                               : $"on the graphics card, with {inMemory} of {layers} layers' experts in memory");
    }

    // For a llama-server without --n-cpu-moe: whole layers on the card instead.
    public static int WholeLayers(long vramBytes, long modelBytes, int layers) =>
        vramBytes < 3 * GiB ? 0 : (int)Math.Clamp((vramBytes - GpuReserve) * layers / Math.Max(1, modelBytes), 0, layers);
}

public sealed class LargeModel
{
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);
    private readonly IConfiguration _config;
    private readonly ILogger<LargeModel>? _log;
    private readonly object _gate = new();
    private long? _vram;
    private (DateTimeOffset At, string Reason)? _failure;

    public bool Configured { get; }
    public string Name { get; }
    public string? Path { get; }
    public long Bytes { get; }
    public int Layers { get; }
    // Chosen for this computer unless the person picked something themselves.
    public bool Preferred { get; }
    public string? StatusFile { get; }

    public LargeModel(IConfiguration config, ILogger<LargeModel>? log = null)
    {
        _config = config;
        _log = log;
        Path = config["NEXUS_LLAMA_MODEL_PATH"];
        Name = config["NEXUS_LARGE_MODEL_NAME"] ?? "";
        Configured = Name.Length > 0 && !string.IsNullOrWhiteSpace(Path) && !string.IsNullOrWhiteSpace(config["NEXUS_LLAMA_SERVER_BIN"]);
        Bytes = long.TryParse(config["NEXUS_LARGE_MODEL_BYTES"], NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : 0;
        Layers = int.TryParse(config["NEXUS_LARGE_MODEL_LAYERS"], NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? Math.Clamp(l, 1, 512) : 24;
        Preferred = string.Equals(config["NEXUS_LARGE_MODEL_PREFERRED"], "true", StringComparison.OrdinalIgnoreCase);
        StatusFile = config["NEXUS_LARGE_MODEL_STATUS"];
    }

    public bool Downloaded => Configured && File.Exists(Path);
    public long ModelBytes => Bytes > 0 ? Bytes : Downloaded ? new FileInfo(Path!).Length : 0;

    // The graphics card's own memory: NVIDIA's from nvidia-smi, AMD's from the kernel. Looked up once.
    public long VramBytes()
    {
        lock (_gate) if (_vram is { } known) return known;
        long found = 0;
        if (long.TryParse(_config["NEXUS_LARGE_MODEL_VRAM_MIB"], NumberStyles.None, CultureInfo.InvariantCulture, out var mib)) found = mib * 1024 * 1024;
        else
        {
            found = Math.Max(NvidiaVram(), AmdVram());
        }
        lock (_gate) _vram = found;
        return found;
    }

    private static long NvidiaVram()
    {
        try
        {
            var start = new ProcessStartInfo("nvidia-smi") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("--query-gpu=memory.total");
            start.ArgumentList.Add("--format=csv,noheader,nounits");
            using var process = Process.Start(start);
            if (process is null) return 0;
            if (!process.WaitForExit(4000)) { try { process.Kill(true); } catch (InvalidOperationException) { } return 0; }
            return process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => long.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m * 1024 * 1024 : 0)
                .DefaultIfEmpty(0).Max();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return 0; }
    }

    private static long AmdVram()
    {
        try
        {
            if (!Directory.Exists("/sys/class/drm")) return 0;
            return Directory.EnumerateDirectories("/sys/class/drm", "card*")
                .Select(card => System.IO.Path.Combine(card, "device", "mem_info_vram_total"))
                .Where(File.Exists)
                .Select(file => long.TryParse(File.ReadAllText(file).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0)
                .DefaultIfEmpty(0).Max();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    public MoeSplit Split() => MoePlan.For(VramBytes(), ModelBytes, Layers);

    // Memory free for the model now: what the kernel says is available (it counts
    // file cache it can drop, so a model read earlier does not count against itself).
    public long AvailableMemory()
    {
        if (long.TryParse(_config["NEXUS_LARGE_MODEL_AVAILABLE_MIB"], NumberStyles.None, CultureInfo.InvariantCulture, out var mib)) return mib * 1024 * 1024;
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
                if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;
        }
        catch (Exception ex) when (ex is IOException or FormatException or IndexOutOfRangeException or UnauthorizedAccessException) { }
        return long.MaxValue;
    }

    public void Failed(string reason)
    {
        lock (_gate) _failure = (DateTimeOffset.UtcNow, reason);
        _log?.LogWarning("The large model could not be used: {Reason}", reason);
    }
    public void Started() { lock (_gate) _failure = null; }

    // Why the large model cannot answer right now; null when it can. `running`: its server is already up.
    // The router asks without the memory check: the standard model is unloaded
    // first (LlamaCppProvider), and only then is free memory measured, at start.
    public string? Blocked(bool running, bool checkMemory = true)
    {
        if (!Configured) return "This computer has no large model set up.";
        if (!Downloaded) return $"{Name} is not downloaded yet.";
        if (running) return null;
        lock (_gate)
            if (_failure is { } f && DateTimeOffset.UtcNow - f.At < FailureCooldown) return f.Reason;
        if (!checkMemory) return null;
        var need = Split().MemoryBytes;
        var free = AvailableMemory();
        return free >= need ? null
            : $"{Name} needs about {Gb(need)} GB of free memory and {Gb(free)} GB is free. Close some apps to use it.";
    }

    private static string Gb(long bytes) => (bytes / (double)MoePlan.GiB).ToString("0.#", CultureInfo.InvariantCulture);

    // What nexus-large-model.service last wrote about the download (state, message, sizes).
    public object? Download()
    {
        if (StatusFile is null || !File.Exists(StatusFile)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(StatusFile));
            var root = doc.RootElement;
            string? Text(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long? Number(string key) => root.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) ? n : null;
            var total = Number("size") ?? Bytes;
            var part = Path + ".part";
            var have = Downloaded ? total : File.Exists(part) ? new FileInfo(part).Length : 0;
            return new { state = Text("state"), message = Text("message"), bytes = have, total,
                         percent = total > 0 ? (int)Math.Clamp(have * 100 / total, 0, 100) : 0, nextTry = Number("nextTry") is long n and > 0 && DateTimeOffset.FromUnixTimeSeconds(n) > DateTimeOffset.UtcNow
                             ? DateTimeOffset.FromUnixTimeSeconds(n) : (DateTimeOffset?)null };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}

public sealed record LargeModelUse(bool On);

public static class LargeModelEndpoints
{
    public static object View(LargeModel large, ProviderStore store, LlamaServerProcess process)
    {
        var selected = store.Selected == ProviderNames.LlamaCpp;
        var split = large.Configured && large.ModelBytes > 0 ? large.Split() : null;
        return new
        {
            configured = large.Configured, name = large.Name, downloaded = large.Downloaded, selected,
            running = process.IsRunning,
            // Chosen, but something stops it for now: questions go to the standard model meanwhile.
            blocked = large.Configured ? large.Blocked(process.IsRunning) : null,
            where = split?.Where, memoryGb = split is null ? 0 : Math.Round(split.MemoryBytes / (double)MoePlan.GiB, 1),
            sizeGb = Math.Round(large.ModelBytes / 1e9, 1), download = large.Download()
        };
    }

    public static void MapLargeModel(this WebApplication app)
    {
        app.MapGet("/model/large", (LargeModel large, ProviderStore store, LlamaServerProcess process) => Results.Ok(View(large, store, process)));
        // The person's choice (Settings, Privacy and AI, or Nexus's use_large_model card).
        app.MapPost("/model/large/use", async (LargeModelUse request, LargeModel large, ProviderStore store, LlamaServerProcess process,
            IEnumerable<IModelProvider> providers, ActivityJournal journal, CancellationToken ct) =>
        {
            if (!large.Configured) return Results.Problem("This computer has no large model set up.", statusCode: 409);
            if (request.On && !large.Downloaded) return Results.Problem($"{large.Name} is not downloaded yet.", statusCode: 409);
            store.Select(request.On ? ProviderNames.LlamaCpp : ProviderNames.Local);
            // One model in memory at a time: 16 GB does not hold both.
            try
            {
                if (request.On)
                {
                    var local = providers.First(p => p.Name == ProviderNames.Local);
                    if (local.SupportsUnload) await local.UnloadAsync(ct);
                }
                else await process.StopAsync(ct);
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException) { }
            await journal.AppendAsync(new JournalRecord(JournalKinds.Setting, "user",
                request.On ? $"Nexus answers with the large model ({large.Name})" : "Nexus answers with the standard model", Ref: "model:large"), ct);
            return Results.Ok(View(large, store, process));
        }).RequireRateLimiting("journal");
    }
}
