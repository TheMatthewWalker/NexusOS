public sealed class InferenceConcurrencyGate
{
    private readonly SemaphoreSlim _slots;
    public int Limit { get; }
    public InferenceConcurrencyGate(IConfiguration config)
    {
        Limit = int.TryParse(config["NEXUS_MAX_CONCURRENT_INFERENCE"], out var configured)
            ? Math.Clamp(configured, 1, 8) : 1;
        _slots = new SemaphoreSlim(Limit, Limit);
    }

    public async ValueTask<IDisposable?> TryEnterAsync(CancellationToken ct)
    {
        if (!await _slots.WaitAsync(0, ct)) return null;
        return new Lease(_slots);
    }

    private sealed class Lease(SemaphoreSlim slots) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) slots.Release();
        }
    }
}
