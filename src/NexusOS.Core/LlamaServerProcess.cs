using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

// Optional Core-managed llama.cpp server. When NEXUS_LLAMA_SERVER_BIN is unset,
// Core talks to an independently started server instead. The process is started
// with an argument list (never a shell), bound to loopback, and stopped to
// release model memory on unload or Core shutdown.
// Tool calling (Nexus's actions) needs llama-server's --jinja, which uses the
// model's own chat template. It is on unless NEXUS_LLAMA_JINJA=false. An older
// llama-server that does not know the flag exits at once; Core then starts it
// again without --jinja and the chat goes on without tools (ToolCalling false).
// The large model (LargeModel.cs) is this server with a mixture-of-experts
// model: its split between graphics memory and ordinary memory is worked out
// when it starts (--n-gpu-layers with --n-cpu-moe), and a llama-server too old
// for --n-cpu-moe gets whole layers on the card instead.
// A local model server Core starts on demand and stops to free memory: llama.cpp
// (this file) and colibri (Colibri.cs). Both speak the OpenAI chat API on loopback.
public interface IManagedModelServer
{
    bool IsConfigured { get; }
    bool IsRunning { get; }
    bool ToolCalling { get; }
    string Endpoint { get; }
    // The path answering 200 once the server is up.
    string HealthPath { get; }
    Task EnsureRunningAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}

public sealed class LlamaServerProcess : IManagedModelServer, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IHttpClientFactory _factory;
    private readonly TimeSpan _startTimeout;
    private readonly LargeModel? _large;
    private Process? _process;

    public bool IsConfigured { get; }
    public string? Executable { get; }
    public string? ModelPath { get; }
    public int Port { get; }
    public int GpuLayers { get; }
    public int ContextTokens { get; }
    public bool JinjaWanted { get; }
    private volatile bool _jinja;
    // Whether this server takes tools; false after it refused --jinja, until Core restarts.
    public bool ToolCalling => _jinja;
    public string Endpoint => $"http://127.0.0.1:{Port}";
    public string HealthPath => "/health";
    public bool IsRunning { get { lock (_gate) return _process is { HasExited: false }; } }
    public int? ProcessId { get { lock (_gate) return _process is { HasExited: false } p ? p.Id : null; } }

    public LlamaServerProcess(IHttpClientFactory factory, IConfiguration config, InferenceTokenSettings tokens, LargeModel? large = null)
    {
        _factory = factory;
        _large = large is { Configured: true } ? large : null;
        Executable = config["NEXUS_LLAMA_SERVER_BIN"];
        IsConfigured = !string.IsNullOrWhiteSpace(Executable);
        _startTimeout = TimeSpan.FromSeconds(ParseInt(config["NEXUS_LLAMA_START_TIMEOUT_SECONDS"], 60, 5, 600,
            "NEXUS_LLAMA_START_TIMEOUT_SECONDS"));
        Port = ParseInt(config["NEXUS_LLAMA_PORT"], 8080, 1024, 65535, "NEXUS_LLAMA_PORT");
        GpuLayers = ParseInt(config["NEXUS_LLAMA_GPU_LAYERS"], 0, 0, 999, "NEXUS_LLAMA_GPU_LAYERS");
        ContextTokens = tokens.Budget.ContextTokens;
        JinjaWanted = !string.Equals(config["NEXUS_LLAMA_JINJA"]?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        _jinja = JinjaWanted;
        if (!IsConfigured) return;
        ModelPath = config["NEXUS_LLAMA_MODEL_PATH"];
        if (!Path.IsPathFullyQualified(Executable!) || !File.Exists(Executable))
            throw new InvalidOperationException("NEXUS_LLAMA_SERVER_BIN must be an absolute path to an existing llama-server.");
        // The large model is downloaded after installation: its file may not be there yet.
        if (string.IsNullOrWhiteSpace(ModelPath) || !Path.IsPathFullyQualified(ModelPath) || (_large is null && !File.Exists(ModelPath)))
            throw new InvalidOperationException("NEXUS_LLAMA_MODEL_PATH must be an absolute path to an existing GGUF model.");
    }

    private static int ParseInt(string? raw, int fallback, int min, int max, string name)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new InvalidOperationException($"{name} must be an integer between {min} and {max}.");
        return value;
    }

    public IReadOnlyList<string> Arguments() => Arguments(ToolCalling, _moe);

    // False after this llama-server refused --n-cpu-moe, until Core restarts.
    private volatile bool _moe = true;

    private IReadOnlyList<string> Arguments(bool jinja, bool moe)
    {
        var gpuLayers = GpuLayers;
        var cpuMoe = 0;
        if (_large is not null)
        {
            var split = _large.Split();
            cpuMoe = moe ? split.CpuMoeLayers : 0;
            gpuLayers = moe || split.CpuMoeLayers == 0 ? split.GpuLayers
                : MoePlan.WholeLayers(_large.VramBytes(), _large.ModelBytes, _large.Layers);
        }
        var list = new List<string>
        {
            "--model", ModelPath!, "--host", "127.0.0.1", "--port", Port.ToString(CultureInfo.InvariantCulture),
            "--ctx-size", ContextTokens.ToString(CultureInfo.InvariantCulture),
            "--n-gpu-layers", gpuLayers.ToString(CultureInfo.InvariantCulture)
        };
        if (cpuMoe > 0) { list.Add("--n-cpu-moe"); list.Add(cpuMoe.ToString(CultureInfo.InvariantCulture)); }
        if (jinja) list.Add("--jinja");
        return list;
    }

    public async Task EnsureRunningAsync(CancellationToken ct)
    {
        if (!IsConfigured) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_process is { HasExited: false }) return;
            _process?.Dispose();
            _process = null;
            if (_large?.Blocked(false) is { } blocked)
            {
                // Remembered for a few minutes, so the next questions go to the standard model at once.
                _large.Failed(blocked);
                throw new ProviderException(blocked + " Nexus is using the standard model for now.", 503);
            }
            try
            {
                if (await StartAsync(_jinja, _moe, ct)) { _large?.Started(); return; }
                // Exited at once. A llama-server too old for --n-cpu-moe: whole layers on the card instead.
                if (_moe && _large is { } large && large.Split().CpuMoeLayers > 0)
                {
                    _moe = false;
                    if (await StartAsync(_jinja, false, ct)) { large.Started(); return; }
                }
                // Or too old for --jinja: once more without, and the chat goes on without tools.
                if (!_jinja) throw Exited();
                _jinja = false;
                if (!await StartAsync(false, _moe, ct)) throw Exited();
                _large?.Started();
            }
            catch (ProviderException ex)
            {
                _large?.Failed($"{_large.Name} could not start ({ex.Message.TrimEnd('.')}). Nexus is using the standard model for now.");
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private static ProviderException Exited() =>
        new("The managed llama.cpp server exited during startup. Check the model path and memory.", 503);

    // True once healthy; false if the process exited during startup. Called under _gate.
    private async Task<bool> StartAsync(bool jinja, bool moe, CancellationToken ct)
    {
        {
            var start = new ProcessStartInfo
            {
                FileName = Executable!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };
            foreach (var argument in Arguments(jinja, moe)) start.ArgumentList.Add(argument);
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
                    return false;
                }
                if (await ProviderHttp.ProbeAsync(client, Endpoint + "/health", ct)) return true;
                await Task.Delay(250, ct);
            }
            Kill(process);
            _process = null;
            throw new ProviderException("The managed llama.cpp server did not become healthy before the timeout.", 504);
        }
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
