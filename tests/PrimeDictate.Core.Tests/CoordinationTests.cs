using PrimeDictate.Core.Coordination;

namespace PrimeDictate.Core.Tests;

public class CoordinationTests
{
    private sealed class FakeConsumer(string name, bool active, bool busy = false, bool failSuspend = false) : IMicrophoneConsumer
    {
        public string Name { get; } = name;

        public bool IsBusy { get; set; } = busy;

        public bool IsActive { get; set; } = active;

        public List<string> Calls { get; } = [];

        public ValueTask SuspendAsync(string reason, CancellationToken cancellationToken)
        {
            if (failSuspend)
            {
                throw new InvalidOperationException("device error");
            }

            this.Calls.Add("suspend");
            this.IsActive = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask ResumeAsync(CancellationToken cancellationToken)
        {
            this.Calls.Add("resume");
            this.IsActive = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Transcription_suspends_active_consumers_and_restores_only_those()
    {
        var coordinator = new MicrophoneCoordinator();
        var wake = new FakeConsumer("Wake word", active: true);
        var dictation = new FakeConsumer("Dictation", active: false);
        coordinator.Register(wake);
        coordinator.Register(dictation);

        await using (var lease = await coordinator.AcquireAsync("Transcription", CancellationToken.None))
        {
            Assert.Equal("Transcription", coordinator.CurrentOwner);
            Assert.Contains("Transcription", coordinator.SuspensionReason);
            Assert.Equal(["suspend"], wake.Calls);
            Assert.Empty(dictation.Calls);
        }

        Assert.Null(coordinator.CurrentOwner);
        Assert.Equal(["suspend", "resume"], wake.Calls);
        Assert.Empty(dictation.Calls);
        Assert.False(dictation.IsActive);
    }

    [Fact]
    public async Task Busy_dictation_or_existing_owner_blocks_acquire()
    {
        var coordinator = new MicrophoneCoordinator();
        var dictation = new FakeConsumer("Dictation", active: true, busy: true);
        coordinator.Register(dictation);
        var ex = await Assert.ThrowsAsync<MicrophoneBusyException>(async () => await coordinator.AcquireAsync("Transcription", CancellationToken.None));
        Assert.Contains("Dictation", ex.Message);
        Assert.Empty(dictation.Calls);

        dictation.IsBusy = false;
        await using var lease = await coordinator.AcquireAsync("Transcription", CancellationToken.None);
        await Assert.ThrowsAsync<MicrophoneBusyException>(async () => await coordinator.AcquireAsync("Another session", CancellationToken.None));
    }

    [Fact]
    public async Task Failed_suspend_rolls_back_and_releases()
    {
        var coordinator = new MicrophoneCoordinator();
        var ok = new FakeConsumer("Wake word", active: true);
        var broken = new FakeConsumer("Dictation", active: true, failSuspend: true);
        coordinator.Register(ok);
        coordinator.Register(broken);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await coordinator.AcquireAsync("Transcription", CancellationToken.None));
        Assert.Null(coordinator.CurrentOwner);
        Assert.Equal(["suspend", "resume"], ok.Calls);
    }

    [Fact]
    public async Task Double_dispose_releases_once()
    {
        var coordinator = new MicrophoneCoordinator();
        var wake = new FakeConsumer("Wake word", active: true);
        coordinator.Register(wake);
        var lease = await coordinator.AcquireAsync("Transcription", CancellationToken.None);
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Assert.Equal(["suspend", "resume"], wake.Calls);
    }

    [Fact]
    public async Task Live_work_is_granted_before_queued_file_windows()
    {
        var scheduler = new ModelLeaseScheduler();
        var order = new List<string>();
        var first = await scheduler.AcquireAsync("whisper", ModelLeasePriority.Background, CancellationToken.None);

        var file2 = scheduler.AcquireAsync("whisper", ModelLeasePriority.Background, CancellationToken.None).AsTask()
            .ContinueWith(t => { lock (order) { order.Add("file2"); } return t.Result; }, TaskScheduler.Default);
        var file3 = scheduler.AcquireAsync("whisper", ModelLeasePriority.Background, CancellationToken.None).AsTask()
            .ContinueWith(t => { lock (order) { order.Add("file3"); } return t.Result; }, TaskScheduler.Default);
        var live = scheduler.AcquireAsync("whisper", ModelLeasePriority.Live, CancellationToken.None).AsTask()
            .ContinueWith(t => { lock (order) { order.Add("live"); } return t.Result; }, TaskScheduler.Default);
        Assert.Equal(3, scheduler.QueuedCount("whisper"));

        first.Dispose();
        (await live).Dispose();
        (await file2).Dispose();
        (await file3).Dispose();

        Assert.Equal(["live", "file2", "file3"], order);
        Assert.Equal(0, scheduler.QueuedCount("whisper"));
    }

    [Fact]
    public async Task Different_models_do_not_block_each_other_and_cancel_removes_waiter()
    {
        var scheduler = new ModelLeaseScheduler();
        using var a = await scheduler.AcquireAsync("whisper", ModelLeasePriority.Background, CancellationToken.None);
        using var b = await scheduler.AcquireAsync("parakeet", ModelLeasePriority.Background, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var waiting = scheduler.AcquireAsync("whisper", ModelLeasePriority.Live, cts.Token).AsTask();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, scheduler.QueuedCount("whisper"));
    }
}
