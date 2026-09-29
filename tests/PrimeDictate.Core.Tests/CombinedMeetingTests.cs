using Microsoft.Data.Sqlite;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Coordination;
using PrimeDictate.Core.Pipeline;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Sessions;
using PrimeDictate.Core.Storage;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Core.Tests;

/// <summary>The combined lease and the meeting session must agree on channel order: left is the microphone, right is system audio.</summary>
public sealed class CombinedMeetingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-combined-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
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

    private sealed class Device(IAudioCaptureLease lease) : IAudioSource
    {
        public ValueTask<IReadOnlyList<AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AudioInputDevice>>([]);

        public ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken) => ValueTask.FromResult(lease);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Microphone_lands_in_the_left_channel_and_system_audio_in_the_right_of_the_saved_file(bool microphoneSpeaking)
    {
        var mic = new CombinedAudioSourceTests.FakeLease(48_000, "mic");
        var system = new CombinedAudioSourceTests.FakeLease(16_000, "speakers");
        await using var store = SqliteTranscriptionSessionStore.Create(new AppDataPaths(this.root));
        await store.InitializeAsync(default);
        var options = new LiveSessionOptions(
            new TranscriptionSessionOptions("fake:model", null, "cpu", "en", null, AudioRetention.KeepAudio, DownmixMode.Average, null, null, null),
            "t", store.GetSessionMediaDirectory, TimeSpan.FromSeconds(60), null, TranscriptSourceType.Meeting);
        var source = new CombinedAudioSource(new Device(mic), new Device(system));
        await using var session = new LiveTranscriptionSession(source, new Provider(), new MicrophoneCoordinator(), new ModelLeaseScheduler(), store, options);
        await session.StartAsync(default);

        // Only one side speaks; the other stays silent but keeps delivering.
        for (var i = 0; i < 10; i++)
        {
            mic.Push(microphoneSpeaking ? 0.5f : 0f, 4_800);
            system.Push(microphoneSpeaking ? 0f : 0.5f, 1_600);
        }

        await Task.Delay(300);
        await session.StopAsync(default);

        var audio = Assert.Single(session.Host!.Document.Audio);
        var samples = new List<float>();
        await foreach (var f in new WavAudioDecoder().DecodeAsync(audio.Path, 0, default))
        {
            Assert.Equal(2, f.Format.Channels);
            samples.AddRange(f.Samples.ToArray());
        }

        var left = samples.Where((_, i) => i % 2 == 0).Sum(Math.Abs);
        var right = samples.Where((_, i) => i % 2 == 1).Sum(Math.Abs);
        Assert.True(left > 0 || right > 0);
        if (microphoneSpeaking)
        {
            Assert.True(left > 100 * Math.Max(right, 1e-3f));
        }
        else
        {
            Assert.True(right > 100 * Math.Max(left, 1e-3f));
        }
    }
}
