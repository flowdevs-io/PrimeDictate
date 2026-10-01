namespace PrimeDictate.Platforms.Speech;

/// <summary>
/// Shares one expensive resource (a loaded speech model) between several users. The first <see cref="Acquire"/> for a key creates
/// it, later ones get the same instance, and it is disposed when the last lease is released. Creation runs under the cache lock, so
/// two users asking for the same model at once load it once.
/// </summary>
public sealed class SharedResourceCache<T> where T : class, IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many resources are currently loaded.</summary>
    public int Count
    {
        get
        {
            lock (this.gate)
            {
                return this.entries.Count;
            }
        }
    }

    /// <summary>How many leases are open on <paramref name="key"/> (0 when it is not loaded).</summary>
    public int LeaseCount(string key)
    {
        lock (this.gate)
        {
            return this.entries.TryGetValue(key, out var entry) ? entry.Leases : 0;
        }
    }

    public Lease Acquire(string key, Func<T> create)
    {
        lock (this.gate)
        {
            if (!this.entries.TryGetValue(key, out var entry))
            {
                // A failed create leaves nothing behind, so the next caller tries again.
                entry = new Entry(create());
                this.entries[key] = entry;
            }

            entry.Leases++;
            return new Lease(this, key, entry.Value);
        }
    }

    private void Release(string key)
    {
        T? dispose = null;
        lock (this.gate)
        {
            if (this.entries.TryGetValue(key, out var entry) && --entry.Leases <= 0)
            {
                this.entries.Remove(key);
                dispose = entry.Value;
            }
        }

        // Outside the lock: unloading a large model can take a moment and must not stall other users.
        dispose?.Dispose();
    }

    private sealed class Entry(T value)
    {
        public T Value { get; } = value;

        public int Leases { get; set; }
    }

    /// <summary>One user's hold on a shared resource. Disposing it more than once is harmless.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly SharedResourceCache<T> owner;
        private readonly string key;
        private int released;

        internal Lease(SharedResourceCache<T> owner, string key, T value)
        {
            this.owner = owner;
            this.key = key;
            this.Value = value;
        }

        public T Value { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.released, 1) == 0)
            {
                this.owner.Release(this.key);
            }
        }
    }
}
