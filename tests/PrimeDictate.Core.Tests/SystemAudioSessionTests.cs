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

    private async Task<(int Segments, float[] Saved)> RunQuietAsync(bool autoGain)
    {
        var lease = new CombinedAudioSourceTests.FakeLease(16_000, "speakers");
        await using var store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(Path.Combine(this.root, autoGain ? "on" : "off")));
        await store.InitializeAsync(default);
        var options = new LiveSessionOptions(
            new TranscriptionSessionOptions("fake:model", null, "cpu", "en", null, AudioRetention.KeepAudio, DownmixMode.Average, null, null, null),
            "t", store.GetSessionMediaDirectory, TimeSpan.FromSeconds(1), null, TranscriptSourceType.SystemAudio, autoGain);
        await using var session = new LiveTranscriptionSession(new Device(lease), new Provider(), new MicrophoneCoordinator(), new ModelLeaseScheduler(), store, options);
        await session.StartAsync(default);
        for (var i = 0; i < 300; i++)
        {
            lease.PushSine(0.008f, 160); // RMS about 0.0057: below the speech threshold, above the gain gate
        }

        lease.Push(0f, 16_000);
        await Task.Delay(1000);
        await session.StopAsync(default);
        var saved = new List<float>();
        await foreach (var f in new WavAudioDecoder().DecodeAsync(Assert.Single(session.Host!.Document.Audio).Path, 0, default))
        {
            saved.AddRange(f.Samples.ToArray());
        }

        return (session.Host.Document.ActiveSegments.Count(), saved.ToArray());
    }

    [Fact]
    public async Task Quiet_system_audio_is_transcribed_with_auto_gain_and_the_saved_file_is_untouched()
    {
        var off = await this.RunQuietAsync(autoGain: false);
        var on = await this.RunQuietAsync(autoGain: true);

        Assert.Equal(0, off.Segments);
        Assert.True(on.Segments > 0);
        // Saved recording is as captured either way (16-bit rounding aside).
        Assert.InRange(on.Saved.Max(Math.Abs), 0.0075f, 0.0085f);
        Assert.Equal(off.Saved.Length, on.Saved.Length);
    }

    [Fact]
    public void Auto_gain_lifts_quiet_speech_keeps_silence_silent_and_never_clips()
    {
        var gain = new AutoGain();
        var quiet = new float[16_000 * 3];
        for (var i = 0; i < quiet.Length; i++)
        {
            quiet[i] = 0.008f * MathF.Sin(i * 0.3f);
        }

        var lifted = gain.Process(quiet);
        var tail = lifted.AsSpan(lifted.Length - 8_000);
        var rms = MathF.Sqrt(tail.ToArray().Select(v => v * v).Average());
        Assert.InRange(rms, 0.07f, 0.13f);
        Assert.All(gain.Process(new float[16_000]), v => Assert.Equal(0f, v));
        Assert.All(new AutoGain(new AutoGainOptions { MaxGain = 30f }).Process(quiet.Select(v => v * 500).ToArray()), v => Assert.InRange(v, -1f, 1f));
        Assert.Equal(0.5f, AutoGain.Limit(0.5f));
    }

    [Fact]
    public void Auto_gain_does_not_lift_noise_below_the_gate_and_respects_the_ceiling()
    {
        var gain = new AutoGain(new AutoGainOptions { MaxGain = 4f });
        var noise = new float[16_000 * 2];
        for (var i = 0; i < noise.Length; i++)
        {
            noise[i] = 0.002f * MathF.Sin(i * 0.7f);
        }

        Assert.Equal(noise.Max(), gain.Process(noise).Max(), 4);
        var quiet = noise.Select(v => v * 4).ToArray(); // above the gate, needs far more than 4x
        gain.Process(quiet);
        Assert.InRange(gain.CurrentGain, 1f, 4.01f);
    }
}
