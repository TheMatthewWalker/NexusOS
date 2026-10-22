using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

// Optional Core-managed llama.cpp server. When NEXUS_LLAMA_SERVER_BIN is unset,
// Core talks to an independently started server instead. The process is started
// with an argument list (never a shell), bound to loopback, and stopped to
// release model memory on unload or Core shutdown.
public sealed class LlamaServerProcess : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IHttpClientFactory _factory;
    private readonly TimeSpan _startTimeout;
    private Process? _process;

    public bool IsConfigured { get; }
    public string? Executable { get; }
    public string? ModelPath { get; }
    public int Port { get; }
    public int GpuLayers { get; }
    public int ContextTokens { get; }
    public string Endpoint => $"http://127.0.0.1:{Port}";
    public bool IsRunning { get { lock (_gate) return _process is { HasExited: false }; } }
    public int? ProcessId { get { lock (_gate) return _process is { HasExited: false } p ? p.Id : null; } }

    public LlamaServerProcess(IHttpClientFactory factory, IConfiguration config, InferenceTokenSettings tokens)
    {
        _factory = factory;
        Executable = config["NEXUS_LLAMA_SERVER_BIN"];
        IsConfigured = !string.IsNullOrWhiteSpace(Executable);
        _startTimeout = TimeSpan.FromSeconds(ParseInt(config["NEXUS_LLAMA_START_TIMEOUT_SECONDS"], 60, 5, 600,
            "NEXUS_LLAMA_START_TIMEOUT_SECONDS"));
        Port = ParseInt(config["NEXUS_LLAMA_PORT"], 8080, 1024, 65535, "NEXUS_LLAMA_PORT");
        GpuLayers = ParseInt(config["NEXUS_LLAMA_GPU_LAYERS"], 0, 0, 999, "NEXUS_LLAMA_GPU_LAYERS");
        ContextTokens = tokens.Budget.ContextTokens;
        if (!IsConfigured) return;
        ModelPath = config["NEXUS_LLAMA_MODEL_PATH"];
        if (!Path.IsPathFullyQualified(Executable!) || !File.Exists(Executable))
            throw new InvalidOperationException("NEXUS_LLAMA_SERVER_BIN must be an absolute path to an existing llama-server.");
        if (string.IsNullOrWhiteSpace(ModelPath) || !Path.IsPathFullyQualified(ModelPath) || !File.Exists(ModelPath))
            throw new InvalidOperationException("NEXUS_LLAMA_MODEL_PATH must be an absolute path to an existing GGUF model.");
    }

    private static int ParseInt(string? raw, int fallback, int min, int max, string name)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new InvalidOperationException($"{name} must be an integer between {min} and {max}.");
        return value;
    }

    public IReadOnlyList<string> Arguments() =>
    [
        "--model", ModelPath!, "--host", "127.0.0.1", "--port", Port.ToString(CultureInfo.InvariantCulture),
        "--ctx-size", ContextTokens.ToString(CultureInfo.InvariantCulture),
        "--n-gpu-layers", GpuLayers.ToString(CultureInfo.InvariantCulture)
    ];

    public async Task EnsureRunningAsync(CancellationToken ct)
    {
        if (!IsConfigured) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_process is { HasExited: false }) return;
            _process?.Dispose();
            var start = new ProcessStartInfo
            {
                FileName = Executable!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };
            foreach (var argument in Arguments()) start.ArgumentList.Add(argument);
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            try { process.Start(); }
            catch (Win32Exception)
            {
                process.Dispose();
                throw new ProviderException("The managed llama.cpp server could not be started.", 503);
            }
            // Drain logs without retaining them: they can echo prompt text.
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;

            var client = _factory.CreateClient("ai");
            var deadline = DateTime.UtcNow + _startTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                {
                    _process = null;
                    process.Dispose();
                    throw new ProviderException("The managed llama.cpp server exited during startup. Check the model path and memory.", 503);
                }
                if (await ProviderHttp.ProbeAsync(client, Endpoint + "/health", ct)) return;
                await Task.Delay(250, ct);
            }
            Kill(process);
            _process = null;
            throw new ProviderException("The managed llama.cpp server did not become healthy before the timeout.", 504);
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_process is null) return;
            Kill(_process);
            await _process.WaitForExitAsync(ct);
            _process.Dispose();
            _process = null;
        }
        finally { _gate.Release(); }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    public void Dispose()
    {
        if (_process is not null)
        {
            Kill(_process);
            _process.WaitForExit(5000);
            _process.Dispose();
            _process = null;
        }
        _gate.Dispose();
    }
}
