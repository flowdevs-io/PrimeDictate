namespace PrimeDictate.Core.Coordination;

/// <summary>
/// Background microphone users (dictation, wake word) that yield to a transcription session.
/// </summary>
public interface IMicrophoneConsumer
{
    string Name { get; }

    /// <summary>True while the consumer is mid-utterance and must not be interrupted.</summary>
    bool IsBusy { get; }

    /// <summary>True when the user has this consumer enabled and it currently holds or listens on the microphone.</summary>
    bool IsActive { get; }

    ValueTask SuspendAsync(string reason, CancellationToken cancellationToken);

    ValueTask ResumeAsync(CancellationToken cancellationToken);
}

public sealed class MicrophoneBusyException(string message) : InvalidOperationException(message);

/// <summary>
/// Grants one exclusive microphone session at a time. Acquiring suspends active background
/// consumers and releasing restores exactly those consumers, so the user's prior choice
/// (for example, wake word on or off) is preserved.
/// </summary>
public sealed class MicrophoneCoordinator
{
    private readonly object sync = new();
    private readonly List<IMicrophoneConsumer> consumers = [];
    private Lease? current;

    public event EventHandler? Changed;

    public string? CurrentOwner
    {
        get
        {
            lock (this.sync)
            {
                return this.current?.Owner;
            }
        }
    }

    /// <summary>Why background consumers are suspended, for display; null when nothing holds the microphone.</summary>
    public string? SuspensionReason
    {
        get
        {
            lock (this.sync)
            {
                return this.current?.Reason;
            }
        }
    }

    public void Register(IMicrophoneConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (this.sync)
        {
            if (!this.consumers.Contains(consumer))
            {
                this.consumers.Add(consumer);
            }
        }
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(string owner, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        Lease lease;
        IMicrophoneConsumer[] toSuspend;
        lock (this.sync)
        {
            if (this.current is not null)
            {
                throw new MicrophoneBusyException($"The microphone is in use by {this.current.Owner}.");
            }

            var busy = this.consumers.FirstOrDefault(c => c.IsBusy);
            if (busy is not null)
            {
                throw new MicrophoneBusyException($"{busy.Name} is in progress. Finish it before starting {owner}.");
            }

            toSuspend = this.consumers.Where(c => c.IsActive).ToArray();
            lease = new Lease(this, owner, $"Paused while {owner} is using the microphone.");
            this.current = lease;
        }

        var suspended = new List<IMicrophoneConsumer>();
        try
        {
            foreach (var consumer in toSuspend)
            {
                await consumer.SuspendAsync(lease.Reason, cancellationToken).ConfigureAwait(false);
                suspended.Add(consumer);
            }
        }
        catch
        {
            await ResumeAllAsync(suspended).ConfigureAwait(false);
            lock (this.sync)
            {
                this.current = null;
            }

            throw;
        }

        lease.Suspended = suspended;
        this.Changed?.Invoke(this, EventArgs.Empty);
        return lease;
    }

    private async ValueTask ReleaseAsync(Lease lease)
    {
        lock (this.sync)
        {
            if (!ReferenceEquals(this.current, lease))
            {
                return;
            }

            this.current = null;
        }

        await ResumeAllAsync(lease.Suspended).ConfigureAwait(false);
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    private static async ValueTask ResumeAllAsync(IReadOnlyList<IMicrophoneConsumer> consumers)
    {
        List<Exception>? errors = null;
        foreach (var consumer in consumers)
        {
            try
            {
                await consumer.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }

        if (errors is not null)
        {
            throw new AggregateException("One or more microphone consumers failed to resume.", errors);
        }
    }

    private sealed class Lease(MicrophoneCoordinator owner, string name, string reason) : IAsyncDisposable
    {
        private int disposed;

        public string Owner { get; } = name;

        public string Reason { get; } = reason;

        public IReadOnlyList<IMicrophoneConsumer> Suspended { get; set; } = [];

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref this.disposed, 1) == 0 ? owner.ReleaseAsync(this) : ValueTask.CompletedTask;
    }
}
