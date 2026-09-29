namespace PrimeDictate.Core.Coordination;

public enum ModelLeasePriority
{
    /// <summary>Imported-file jobs. They take one lease per window so live work can interleave.</summary>
    Background = 0,

    /// <summary>Live sessions; always granted ahead of queued background work.</summary>
    Live = 1
}

/// <summary>
/// Serializes access to native model instances that are not thread-safe. Each model key has
/// one holder at a time; waiters are granted by priority, then arrival order.
/// </summary>
public sealed class ModelLeaseScheduler
{
    private readonly object sync = new();
    private readonly Dictionary<string, KeyState> keys = new(StringComparer.Ordinal);
    private long arrivalCounter;

    public int QueuedCount(string modelKey)
    {
        lock (this.sync)
        {
            return this.keys.TryGetValue(modelKey, out var state) ? state.Waiters.Count : 0;
        }
    }

    public ValueTask<IDisposable> AcquireAsync(string modelKey, ModelLeasePriority priority, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelKey);
        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (this.sync)
        {
            if (!this.keys.TryGetValue(modelKey, out var state))
            {
                state = new KeyState();
                this.keys[modelKey] = state;
            }

            if (!state.Held)
            {
                state.Held = true;
                return ValueTask.FromResult<IDisposable>(new Lease(this, modelKey));
            }

            waiter = new Waiter(priority, this.arrivalCounter++);
            state.Waiters.Add(waiter);
        }

        if (cancellationToken.CanBeCanceled)
        {
            waiter.Registration = cancellationToken.Register(() => this.Cancel(modelKey, waiter, cancellationToken));
        }

        return new ValueTask<IDisposable>(waiter.Completion.Task);
    }

    private void Cancel(string modelKey, Waiter waiter, CancellationToken cancellationToken)
    {
        lock (this.sync)
        {
            if (!this.keys.TryGetValue(modelKey, out var state) || !state.Waiters.Remove(waiter))
            {
                return;
            }
        }

        waiter.Completion.TrySetCanceled(cancellationToken);
    }

    private void Release(string modelKey)
    {
        Waiter? next;
        lock (this.sync)
        {
            var state = this.keys[modelKey];
            next = state.Waiters
                .OrderByDescending(w => w.Priority)
                .ThenBy(w => w.Arrival)
                .FirstOrDefault();
            if (next is null)
            {
                state.Held = false;
                this.keys.Remove(modelKey);
                return;
            }

            state.Waiters.Remove(next);
        }

        next.Registration.Dispose();
        next.Completion.TrySetResult(new Lease(this, modelKey));
    }

    private sealed class KeyState
    {
        public bool Held { get; set; }

        public List<Waiter> Waiters { get; } = [];
    }

    private sealed class Waiter(ModelLeasePriority priority, long arrival)
    {
        public ModelLeasePriority Priority { get; } = priority;

        public long Arrival { get; } = arrival;

        public TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Registration { get; set; }
    }

    private sealed class Lease(ModelLeaseScheduler scheduler, string modelKey) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 0)
            {
                scheduler.Release(modelKey);
            }
        }
    }
}
