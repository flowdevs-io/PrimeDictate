using Microsoft.Data.Sqlite;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Coordination;
using PrimeDictate.Core.Pipeline;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Sessions;
using PrimeDictate.Core.Storage;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Core.Tests;

/// <summary>Exit must never leave a recording Running, even when the device stalls or a stop hangs.</summary>
public sealed class ExitAbortTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-exit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.root, true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Provider : ITranscriptionProvider
    {
        public string ModelId => "fake:model";
        public TranscriptionProviderCapabilities Capabilities { get; } = new(true, LiveRecognitionMode.BufferedWindows, TimingCapabilities.SegmentTimestamps, false, null, TimeSpan.FromSeconds(30), ["en"], 16_000);
        public EffectiveRuntime Runtime { get; } = new("fake", "1", "cpu", "cpu", null);
        public ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RecognizedSegment>>([]);
        public ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A device that delivered some audio and then stalled: it ignores cancellation and never finishes disposing.</summary>
    private sealed class StalledLease : IAudioCaptureLease
    {
        private readonly TaskCompletionSource never = new();

        public AudioFormat Format { get; } = new(16_000, 1, AudioSampleFormat.Float32);

        public string DeviceId => "stalled";

        public string DeviceName => "stalled";

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var samples = new float[1600];
            Array.Fill(samples, 0.1f);
            for (long i = 0; i < 5; i++)
            {
                yield return AudioFrame.CopyFrom(samples, this.Format, i, i * 1600);
            }

            await this.never.Task; // deliberately not cancellable
        }

        public ValueTask PauseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => new(this.never.Task);
    }

    private sealed class Device(IAudioCaptureLease lease) : IAudioSource
    {
        public ValueTask<IReadOnlyList<AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<AudioInputDevice>>([]);
        public ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken) => ValueTask.FromResult(lease);
    }

    private LiveSessionOptions Options(SqliteTranscriptionSessionStore store) => new(
        new TranscriptionSessionOptions("fake:model", null, "cpu", "en", null, AudioRetention.KeepAudio, DownmixMode.Average, null, null, null),
        "t", store.GetSessionMediaDirectory, TimeSpan.FromSeconds(1), null, TranscriptSourceType.Microphone);

    [Fact]
    public async Task Abort_ends_a_recording_whose_stop_hangs_as_canceled_and_keeps_the_wav()
    {
        await using var store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(this.root));
        await store.InitializeAsync(default);
        var session = new LiveTranscriptionSession(new Device(new StalledLease()), new Provider(), new MicrophoneCoordinator(), new ModelLeaseScheduler(), store, this.Options(store));
        await session.StartAsync(default);
        await Task.Delay(300);

        var stop = session.StopAsync(default);
        await Assert.ThrowsAsync<TimeoutException>(() => stop.WaitAsync(TimeSpan.FromMilliseconds(500)));
        // The UI's Stop is now stuck, so a second call returns at once and finalizes nothing.
        await session.DiscardAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(TranscriptSessionStatus.Finalizing, session.Host!.Document.Status);

        await session.AbortAsync(TimeSpan.FromMilliseconds(200)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TranscriptSessionStatus.Canceled, session.Host.Document.Status);
        var saved = await store.LoadAsync(session.Host.Document.SessionId, default);
        Assert.Equal(TranscriptSessionStatus.Canceled, saved!.Status);
        var wav = Assert.Single(saved.Audio).Path;
        Assert.True(File.Exists(wav));
        Assert.True(new FileInfo(wav).Length > 44);
    }

    [Fact]
    public async Task Abort_on_a_running_recording_with_a_stalled_device_does_not_wait_for_the_device()
    {
        await using var store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(this.root));
        await store.InitializeAsync(default);
        var session = new LiveTranscriptionSession(new Device(new StalledLease()), new Provider(), new MicrophoneCoordinator(), new ModelLeaseScheduler(), store, this.Options(store));
        await session.StartAsync(default);

        await session.AbortAsync(TimeSpan.FromMilliseconds(200)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TranscriptSessionStatus.Canceled, session.Host!.Document.Status);
    }

    [Fact]
    public async Task Sweep_cancels_sessions_left_in_flight_and_leaves_finished_ones()
    {
        await using var store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(this.root));
        await store.InitializeAsync(default);
        var ids = new Dictionary<TranscriptSessionStatus, Guid>();
        foreach (var status in new[] { TranscriptSessionStatus.Running, TranscriptSessionStatus.Paused, TranscriptSessionStatus.Finalizing, TranscriptSessionStatus.Completed })
        {
            var id = Guid.NewGuid();
            ids[status] = id;
            var now = DateTimeOffset.UtcNow;
            await store.SaveCheckpointAsync(new TranscriptDocument { SessionId = id, Title = status.ToString(), SourceType = TranscriptSourceType.Microphone, CreatedAt = now, UpdatedAt = now, Status = status }, default);
        }

        var changed = await store.CancelInFlightSessionsAsync("closed", default);

        Assert.Equal(3, changed);
        foreach (var (status, id) in ids)
        {
            var loaded = await store.LoadAsync(id, default);
            Assert.Equal(status == TranscriptSessionStatus.Completed ? TranscriptSessionStatus.Completed : TranscriptSessionStatus.Canceled, loaded!.Status);
        }
    }

    [Fact]
    public async Task Exit_leaves_a_cut_off_record_only_meeting_recoverable_and_the_next_launch_picks_it_up()
    {
        var paths = new AppDataPaths(this.root);
        Guid meeting = Guid.NewGuid(), discarded = Guid.NewGuid(), dictation = Guid.NewGuid();
        await using (var seed = SqliteTranscriptionSessionStore.Create(paths))
        {
            await seed.InitializeAsync(default);
            foreach (var (id, type, marker) in new[] { (meeting, TranscriptSourceType.Meeting, true), (discarded, TranscriptSourceType.Meeting, false), (dictation, TranscriptSourceType.Microphone, false) })
            {
                var now = DateTimeOffset.UtcNow;
                await seed.SaveCheckpointAsync(new TranscriptDocument { SessionId = id, Title = "t", SourceType = type, CreatedAt = now, UpdatedAt = now, Status = TranscriptSessionStatus.Running }, default);
                var directory = seed.GetSessionMediaDirectory(id);
                Directory.CreateDirectory(directory);
                new WavFileWriter(Path.Combine(directory, "recording-16k-stereo.wav"), 16_000, 2).Dispose();
                if (marker)
                {
                    PrimeDictate.Platforms.Nemotron.FinalPassPending.Mark(directory);
                }
            }
        }

        await using (var first = new PrimeDictate.Platforms.TranscriptionWorkspaceService(paths, null, probeMicrophone: false))
        {
            await first.StopLiveForExitAsync(TimeSpan.FromSeconds(2));
        }

        await using var second = new PrimeDictate.Platforms.TranscriptionWorkspaceService(paths, null, probeMicrophone: false);
        await second.InitializeAsync(default);
        var sessions = await second.ListSessionsAsync(default);
        Assert.Equal(TranscriptSessionStatus.Interrupted, sessions.Single(x => x.SessionId == meeting).Status);
        Assert.Equal(TranscriptSessionStatus.Canceled, sessions.Single(x => x.SessionId == discarded).Status);
        Assert.Equal(TranscriptSessionStatus.Canceled, sessions.Single(x => x.SessionId == dictation).Status);
        // The next launch offers exactly the cut-off meeting to the after-Stop pass.
        Assert.Equal([meeting], await second.FinalPassPendingSessionsAsync(default));
    }
}
