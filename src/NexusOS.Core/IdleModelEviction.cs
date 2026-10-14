public sealed class InferenceActivity
{
    private long _lastActivity = Environment.TickCount64;
    public long MillisecondsIdle => Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref _lastActivity));
    public void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
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

        var lastUnloadedActivity = -1L;
        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await ticker.WaitForNextTickAsync(stoppingToken))
        {
            var idle = activity.MillisecondsIdle;
            if (idle < idleSeconds * 1000L || idle == lastUnloadedActivity)
                continue;

            try
            {
                await lifecycle.UnloadAsync(stoppingToken);
                lastUnloadedActivity = idle;
                logger.LogInformation("Requested idle model unload.");
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning("Idle unload failed ({Type}).", ex.GetType().Name);
            }
        }
    }
}
