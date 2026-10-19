namespace AspireCTF.Api.Runner;

public sealed class DynamicConcurrencyGate
{
    private readonly object _sync = new();
    private int _limit;
    private int _active;
    private readonly Queue<TaskCompletionSource<IDisposable>> _waiters = new();

    public DynamicConcurrencyGate(int limit) => _limit = Math.Max(1, limit);

    public int Limit { get { lock (_sync) return _limit; } }

    public Task<IDisposable> WaitAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_active < _limit)
            {
                _active++;
                return Task.FromResult<IDisposable>(new Lease(this));
            }

            var waiter = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(waiter);
            ct.Register(() => waiter.TrySetCanceled(ct));
            return waiter.Task;
        }
    }

    public void SetLimit(int limit)
    {
        lock (_sync)
        {
            _limit = Math.Max(1, limit);
            ReleaseWaiters();
        }
    }

    private void Release()
    {
        lock (_sync)
        {
            _active = Math.Max(0, _active - 1);
            ReleaseWaiters();
        }
    }

    private void ReleaseWaiters()
    {
        while (_active < _limit && _waiters.Count > 0)
        {
            var waiter = _waiters.Dequeue();
            if (waiter.Task.IsCanceled || waiter.Task.IsCompleted) continue;
            _active++;
            waiter.TrySetResult(new Lease(this));
        }
    }

    private sealed class Lease(DynamicConcurrencyGate owner) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release();
        }
    }
}
