using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Coordination;
using PrimeDictate.Core.Pipeline;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Sessions;
using PrimeDictate.Core.Storage;
using PrimeDictate.Core.Transcripts;
using PrimeDictate.Platforms.Speech;

namespace PrimeDictate.Core.Tests;

public sealed class RecordOnlyCaptureTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-recordonly", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    private LiveSessionOptions Options(SqliteTranscriptionSessionStore store) => new(
        new TranscriptionSessionOptions(RecordOnlyProvider.Id, null, "none", "en", null, AudioRetention.KeepAudio, DownmixMode.Average, null, null, null),
        "t", store.GetSessionMediaDirectory, TimeSpan.FromSeconds(60), null, TranscriptSourceType.Meeting);

    [Fact]
    public async Task A_two_minute_record_only_meeting_records_it_all_and_stop_completes()
    {
        var data = new float[120 * 16_000 * 2];
        for (var i = 0; i < 120 * 16_000; i++)
        {
            data[2 * i] = 0.3f * MathF.Sin(i * 0.05f);
            data[(2 * i) + 1] = 0.3f * MathF.Sin(i * 0.07f);
        }

        var store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(this.root));
        await store.InitializeAsync(default);
        await using var session = new LiveTranscriptionSession(new LiveSourceTests.FakeSource(2, data), new RecordOnlyProvider(), new MicrophoneCoordinator(), new ModelLeaseScheduler(), store, this.Options(store));
        await session.StartAsync(default);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (session.Elapsed < TimeSpan.FromSeconds(119.9) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(session.Elapsed >= TimeSpan.FromSeconds(119.9), $"capture stalled at {session.Elapsed}");
        await session.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(TranscriptSessionStatus.Completed, session.Host!.Document.Status);
        Assert.True(new FileInfo(Assert.Single(session.Host.Document.Audio).Path).Length >= 120 * 16_000 * 2 * 2, "recording lost samples");
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Through_the_combined_source_two_minutes_of_both_sides_also_record_fully()
    {
        var mic = new float[120 * 16_000];
        var system = new float[120 * 16_000];
        for (var i = 0; i < mic.Length; i++)
        {
            mic[i] = 0.3f * MathF.Sin(i * 0.05f);
            system[i] = 0.3f * MathF.Sin(i * 0.07f);
        }

        var store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(this.root));
        await store.InitializeAsync(default);
        var combined = new CombinedAudioSource(new LiveSourceTests.FakeSource(1, mic), new LiveSourceTests.FakeSource(1, system));
        await using var session = new LiveTranscriptionSession(combined, new RecordOnlyProvider(), new MicrophoneCoordinator(), new ModelLeaseScheduler(), store, this.Options(store));
        await session.StartAsync(default);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (session.Elapsed < TimeSpan.FromSeconds(119.5) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(session.Elapsed >= TimeSpan.FromSeconds(119.5), $"capture stalled at {session.Elapsed}");
        await session.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(TranscriptSessionStatus.Completed, session.Host!.Document.Status);
        await store.DisposeAsync();
    }

    /// <summary>Delivers ten frames, then a fault that is not an audio-source error.</summary>
    private sealed class FaultingSource : IAudioSource
    {
        public ValueTask<IReadOnlyList<AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<AudioInputDevice>>([]);

        public ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken) => ValueTask.FromResult<IAudioCaptureLease>(new Lease());

        private sealed class Lease : IAudioCaptureLease
        {
            public AudioFormat Format { get; } = new(16_000, 2, AudioSampleFormat.Float32);

            public string DeviceId => "fault";

            public string DeviceName => "fault";

            public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
            {
                var chunk = new float[1600 * 2];
                Array.Fill(chunk, 0.2f);
                for (long i = 0; i < 10; i++)
                {
                    yield return AudioFrame.CopyFrom(chunk, this.Format, i, i * 1600);
                    await Task.Yield();
                }

                throw new InvalidOperationException("boom");
            }

            public ValueTask PauseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

            public ValueTask ResumeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task An_unexpected_capture_fault_is_reported_and_stop_still_finishes_and_keeps_the_audio()
    {
        var store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(this.root));
        await store.InitializeAsync(default);
        var errors = new List<string>();
        await using var session = new LiveTranscriptionSession(new FaultingSource(), new RecordOnlyProvider(), new MicrophoneCoordinator(), new ModelLeaseScheduler(), store, this.Options(store));
        session.Error += errors.Add;
        await session.StartAsync(default);
        await Task.Delay(500);

        await session.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Contains(errors, e => e.Contains("stopped unexpectedly", StringComparison.Ordinal));
        var log = File.ReadAllText(PrimeDictate.Core.Diagnostics.AppLog.FilePath);
        Assert.Contains("[capture] System.InvalidOperationException: boom", log);
        Assert.Contains("ReadFramesAsync", log); // the stack trace names where it happened
        Assert.Equal(TranscriptSessionStatus.Failed, session.Host!.Document.Status);
        Assert.True(new FileInfo(Assert.Single(session.Host.Document.Audio).Path).Length > 10 * 1600 * 2 * 2 - 1);
        await store.DisposeAsync();
    }
}
