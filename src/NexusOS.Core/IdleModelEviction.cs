public sealed class InferenceActivity
{
    private long _lastActivity = Environment.TickCount64;
    private long _generation;
    private int _inFlight;
    public bool HasActiveRequests => Volatile.Read(ref _inFlight) > 0;
    public void BeginRequest() { Interlocked.Increment(ref _inFlight); Touch(); }
    public void EndRequest() { Interlocked.Decrement(ref _inFlight); Touch(); }
    public long Generation => Interlocked.Read(ref _generation);
    public long MillisecondsIdle => Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref _lastActivity));
    public void Touch() { Interlocked.Exchange(ref _lastActivity, Environment.TickCount64); Interlocked.Increment(ref _generation); }
}

public sealed class IdleModelEvictionService(
    InferenceLifecycle lifecycle,
    InferenceActivity activity,
    IConfiguration config,
    ILogger<IdleModelEvictionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!int.TryParse(config["NEXUS_MODEL_IDLE_SECONDS"], out var idleSeconds) || idleSeconds < 30)
        {
            logger.LogInformation("Automatic model eviction disabled; set NEXUS_MODEL_IDLE_SECONDS to >= 30.");
            return;
        }

        var lastUnloadedGeneration = -1L;
        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await ticker.WaitForNextTickAsync(stoppingToken))
        {
            var idle = activity.MillisecondsIdle;
            if (activity.HasActiveRequests || idle < idleSeconds * 1000L || activity.Generation == lastUnloadedGeneration)
                continue;

            try
            {
                await lifecycle.UnloadAsync(stoppingToken);
                lastUnloadedGeneration = activity.Generation;
                logger.LogInformation("Requested idle model unload.");
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning("Idle unload failed ({Type}).", ex.GetType().Name);
            }
        }
    }
}
