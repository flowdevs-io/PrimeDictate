using Microsoft.Data.Sqlite;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Coordination;
using PrimeDictate.Core.Pipeline;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Sessions;
using PrimeDictate.Core.Storage;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Core.Tests;

public sealed class SystemAudioSessionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-sys-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(this.root)) Directory.Delete(this.root, true);
    }

    private sealed class Provider : ITranscriptionProvider
    {
        public string ModelId => "fake:model";
        public TranscriptionProviderCapabilities Capabilities { get; } = new(true, LiveRecognitionMode.BufferedWindows, TimingCapabilities.SegmentTimestamps, false, null, TimeSpan.FromSeconds(30), ["en"], 16_000);
        public EffectiveRuntime Runtime { get; } = new("fake", "1", "cpu", "cpu", null);
        public ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RecognizedSegment>>([new RecognizedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(samples.Length / 16_000d), "hello", null, null, null, TimingProvenance.ApproximateChunk)]);
        public ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Device(IAudioCaptureLease lease) : IAudioSource
    {
        public ValueTask<IReadOnlyList<AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<AudioInputDevice>>([]);
        public ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken) => ValueTask.FromResult(lease);
    }

    [Fact]
    public async Task System_only_with_48k_stereo_frames_and_silence_gaps_transcribes_and_stops()
    {
        var lease = new CombinedAudioSourceTests.FakeLease(48_000, "speakers", channels: 2);
        await using var store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(this.root));
        await store.InitializeAsync(default);
        var options = new LiveSessionOptions(
            new TranscriptionSessionOptions("fake:model", null, "cpu", "en", null, AudioRetention.KeepAudio, DownmixMode.Average, null, null, null),
            "t", store.GetSessionMediaDirectory, TimeSpan.FromSeconds(1), null, TranscriptSourceType.SystemAudio);
        await using var session = new LiveTranscriptionSession(new Device(lease), new Provider(), new MicrophoneCoordinator(), new ModelLeaseScheduler(), store, options);
        await session.StartAsync(default);
        for (var i = 0; i < 100; i++)
        {
            lease.Push(0.1f, 480); // 10 ms of loud audio
        }

        lease.Push(0f, 4_800 * 3); // 300 ms silence-fill block
        await Task.Delay(1500);
        await session.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TranscriptSessionStatus.Completed, session.Host!.Document.Status);
        Assert.NotEmpty(session.Host.Document.ActiveSegments);
    }

    [Fact]
    public async Task Recording_keeps_the_wav_header_valid_so_a_killed_process_leaves_playable_audio()
    {
        var lease = new CombinedAudioSourceTests.FakeLease(16_000, "speakers");
        await using var store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(this.root));
        await store.InitializeAsync(default);
        var options = new LiveSessionOptions(
            new TranscriptionSessionOptions("fake:model", null, "cpu", "en", null, AudioRetention.KeepAudio, DownmixMode.Average, null, null, null),
            "t", store.GetSessionMediaDirectory, TimeSpan.FromSeconds(1), null, TranscriptSourceType.SystemAudio);
        await using var session = new LiveTranscriptionSession(new Device(lease), new Provider(), new MicrophoneCoordinator(), new ModelLeaseScheduler(), store, options);
        await session.StartAsync(default);
        for (var i = 0; i < 300; i++)
        {
            lease.Push(0.1f, 160); // 3 s
        }

        await Task.Delay(500);
        var path = Assert.Single(session.Host!.Document.Audio).Path;

        // Read the file as a killed process would leave it: without Stop or Dispose.
        var copy = path + ".copy";
        File.Copy(path, copy);
        var header = File.ReadAllBytes(copy);
        Assert.True(BitConverter.ToUInt32(header, 40) > 0, "data size in the header must be non-zero while recording");
        await session.StopAsync(default);
    }

    [Fact]
    public void RepairHeader_restores_the_sizes_of_a_wav_that_was_never_finalized()
    {
        Directory.CreateDirectory(this.root);
        var path = Path.Combine(this.root, "killed.wav");
        using (var writer = new WavFileWriter(path))
        {
            writer.Write(new float[16_000]);
            writer.Flush();
        }

        // Simulate a kill: header claims no data although the samples are on disk.
        using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            file.Seek(40, SeekOrigin.Begin);
            file.Write(new byte[4]);
        }

        Assert.True(WavFileWriter.RepairHeader(path));
        Assert.False(WavFileWriter.RepairHeader(path));
        Assert.Equal(44 + 32_000, new FileInfo(path).Length);
        Assert.Equal(32_000u, BitConverter.ToUInt32(File.ReadAllBytes(path), 40));
    }
}
