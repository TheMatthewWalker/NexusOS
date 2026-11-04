// How many model requests run at once (NEXUS_MAX_CONCURRENT_INFERENCE, 1 by
// default: a local model answers one question at a time).
//
// Requests the person makes (a question, Fix it, a system change) wait for
// their turn instead of being turned away: on a computer without a graphics
// card one answer can take a while, and "busy, try again" is never the right
// reply to someone who asked. Background work (the shell's app suggestions)
// takes a slot only when one is free, and gives it up the moment the person
// asks something.
public sealed class InferenceConcurrencyGate
{
    private readonly SemaphoreSlim _slots;
    private readonly object _sync = new();
    private readonly HashSet<CancellationTokenSource> _background = [];
    public int Limit { get; }
    // How long a request waits for its turn before Core says it is still busy.
    public TimeSpan MaxWait { get; }

    public InferenceConcurrencyGate(IConfiguration config)
    {
        Limit = int.TryParse(config["NEXUS_MAX_CONCURRENT_INFERENCE"], out var configured)
            ? Math.Clamp(configured, 1, 8) : 1;
        MaxWait = TimeSpan.FromSeconds(int.TryParse(config["NEXUS_INFERENCE_WAIT_SECONDS"], out var wait)
            ? Math.Clamp(wait, 1, 3600) : 600);
        _slots = new SemaphoreSlim(Limit, Limit);
    }

    // The person's request: waits for a slot, first asking background work to stop.
    // Null only if it waited MaxWait (a stuck model), never because something else was running.
    public async ValueTask<IDisposable?> EnterAsync(CancellationToken ct)
    {
        if (_slots.Wait(0)) return new Lease(_slots, null, this);
        lock (_sync) foreach (var stop in _background) stop.Cancel();
        return await _slots.WaitAsync(MaxWait, ct) ? new Lease(_slots, null, this) : null;
    }

    // Background work: a slot only if one is free now. The lease's Token is
    // cancelled when the person asks for something; stop then and give it back.
    public BackgroundLease? TryEnterBackground(CancellationToken ct)
    {
        if (!_slots.Wait(0)) return null;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_sync) _background.Add(stop);
        return new BackgroundLease(new Lease(_slots, stop, this), stop.Token);
    }

    public sealed record BackgroundLease(IDisposable Lease, CancellationToken Token) : IDisposable
    {
        public void Dispose() => Lease.Dispose();
    }

    private void Forget(CancellationTokenSource stop)
    {
        lock (_sync) _background.Remove(stop);
        stop.Dispose();
    }

    private sealed class Lease(SemaphoreSlim slots, CancellationTokenSource? stop, InferenceConcurrencyGate gate) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (stop is not null) gate.Forget(stop);
            slots.Release();
        }
    }
}
