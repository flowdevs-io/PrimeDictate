using Microsoft.Data.Sqlite;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Sessions;
using PrimeDictate.Core.Storage;
using PrimeDictate.Core.Transcripts;
using PrimeDictate.Platforms;
using PrimeDictate.Platforms.Nemotron;
using static PrimeDictate.Core.Tests.TestData;

namespace PrimeDictate.Core.Tests;

/// <summary>A recording stays on disk, so whatever happens to its transcript, it must always be possible to make one from it.</summary>
public sealed class RecordingRerunTests : IDisposable
{
    private static readonly SpeechModelChoice Chosen = new("fake:model", "Fake model", "en", false, null);

    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-rerun-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    private AppDataPaths Paths => new(this.root);

    private TranscriptionWorkspaceService Workspace(ITranscriptionProvider? provider = null, IAudioSource? source = null) =>
        new(this.Paths, source, probeMicrophone: false) { ProviderOverride = provider is null ? null : _ => provider };

    /// <summary>A record-only meeting on disk: no text yet, 12 s of stereo with the microphone at 9-11 s and the system audio at 2-5 s.</summary>
    private async Task<Guid> SeedMeetingAsync(TranscriptSessionStatus status, bool pending = false, IReadOnlyList<TranscriptSpeaker>? speakers = null)
    {
        await using var store = SqliteTranscriptionSessionStore.Create(this.Paths);
        await store.InitializeAsync(default);
        var doc = NewDocument() with { SourceType = TranscriptSourceType.Meeting, Duration = TimeSpan.FromSeconds(12) };
        doc = TranscriptDocumentReducer.Apply(doc, new SessionStarted(doc.SessionId, Run(1, "record-only")), Now);
        var directory = store.GetSessionMediaDirectory(doc.SessionId);
        Directory.CreateDirectory(directory);
        var stereo = Path.Combine(directory, "recording-16k-stereo.wav");
        using (var writer = new WavFileWriter(stereo, 16_000, 2))
        {
            var samples = new float[12 * 16_000 * 2];
            for (var i = 0; i < 12 * 16_000; i++)
            {
                var t = i / 16_000d;
                var tone = MathF.Sin(i * 0.07f);
                samples[i * 2] = t is >= 9 and < 11 ? 0.4f * tone : 0f;
                samples[(i * 2) + 1] = t is >= 2 and < 5 ? 0.2f * tone : 0f;
            }

            writer.Write(samples);
        }

        if (pending)
        {
            FinalPassPending.Mark(directory);
        }

        await store.SaveCheckpointAsync(doc with { Status = status, Speakers = speakers ?? [], Audio = [new AudioReference(AudioReferenceKind.Owned, stereo, null)] }, default);
        return doc.SessionId;
    }

    private sealed class FailingProvider : ITranscriptionProvider
    {
        public string ModelId => "fake:model";

        public TranscriptionProviderCapabilities Capabilities { get; } = new(true, LiveRecognitionMode.BufferedWindows, TimingCapabilities.SegmentTimestamps, false, null, TimeSpan.FromSeconds(30), ["en"], 16_000);

        public EffectiveRuntime Runtime { get; } = new("fake", "1", "cpu", "cpu", null);

        public ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the model went away");

        public ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTextProvider(string text) : ITranscriptionProvider
    {
        public string ModelId => "fake:model";

        public TranscriptionProviderCapabilities Capabilities { get; } = new(true, LiveRecognitionMode.BufferedWindows, TimingCapabilities.SegmentTimestamps, false, null, TimeSpan.FromSeconds(30), ["en"], 16_000);

        public EffectiveRuntime Runtime { get; } = new("fake", "1", "cpu", "cpu", null);

        public ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RecognizedSegment>>([new RecognizedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(samples.Length / 16_000d), text, null, null, null, TimingProvenance.ApproximateChunk)]);

        public ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Going_back_to_a_recording_gives_the_recording_itself_so_nothing_saved_after_Stop_can_undo_it()
    {
        await using var workspace = this.Workspace(source: new LiveSourceTests.FakeSource(1, new float[16_000]));
        await workspace.InitializeAsync(default);
        var session = await workspace.StartLiveAsync(TranscriptionWorkspaceService.RecordOnly, null, AudioRetention.KeepAudio, "Meeting", default);
        var id = session.Host!.Document.SessionId;

        // The window after clicking another session and back on it.
        var opened = await workspace.OpenSessionAsync(id, default);
        await workspace.StopLiveAsync(default);
        // What the window does after Stop: it notes what happened on the document it has open and saves that.
        opened!.AddNote("Something happened.");
        await opened.CheckpointAsync(default);

        var saved = (await workspace.ListSessionsAsync(default)).Single(s => s.SessionId == id);
        Assert.Same(session.Host, opened);
        Assert.Equal(TranscriptSessionStatus.Completed, opened.Document.Status);
        Assert.Equal(TranscriptSessionStatus.Completed, saved.Status);
        Assert.NotNull(saved.Duration);
    }

    [Fact]
    public async Task A_session_opened_twice_is_one_document_until_it_is_deleted()
    {
        var id = await this.SeedMeetingAsync(TranscriptSessionStatus.Completed);
        await using var workspace = this.Workspace();
        await workspace.InitializeAsync(default);

        var first = await workspace.OpenSessionAsync(id, default);
        var second = await workspace.OpenSessionAsync(id, default);
        Assert.Same(first, second);

        await workspace.DeleteSessionAsync(id, default);
        Assert.Null(await workspace.OpenSessionAsync(id, default));
    }

    [Fact]
    public async Task A_record_only_meeting_is_transcribed_by_the_chosen_model_with_You_and_the_other_side_apart()
    {
        var id = await this.SeedMeetingAsync(TranscriptSessionStatus.Completed);
        await using (var workspace = this.Workspace(new MeetingFinalPassTests.ChannelProvider([])))
        {
            await workspace.InitializeAsync(default);
            var host = (await workspace.OpenSessionAsync(id, default))!;
            Assert.True(workspace.HasMeetingChannels(host.Document));

            var result = await workspace.RunFinalPassAsync(host, Chosen, null, default);

            // Without Nemotron installed the transcript is still made, and says why the people on the call have one label.
            Assert.Equal("The Nemotron diarizer is not installed.", result!.DiarizerProblem);
        }

        await using var store = SqliteTranscriptionSessionStore.Create(this.Paths);
        await store.InitializeAsync(default);
        var saved = (await store.LoadAsync(id, default))!;
        Assert.Equal(TranscriptSessionStatus.Completed, saved.Status);
        Assert.Equal(2, saved.ActiveResultVersion);
        Assert.Equal(["remote", "local"], saved.ActiveSegments.Select(s => s.Speakers[0].SpeakerId));
        Assert.Contains("Rerun (cuda:0)", saved.Notes.Last(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TranscriptSessionStatus.Completed)]
    [InlineData(TranscriptSessionStatus.Interrupted)]
    [InlineData(TranscriptSessionStatus.Failed)]
    [InlineData(TranscriptSessionStatus.Canceled)]
    public async Task A_meeting_that_ended_any_way_can_be_transcribed_from_its_recording(TranscriptSessionStatus status)
    {
        var id = await this.SeedMeetingAsync(status);
        await using var workspace = this.Workspace(new MeetingFinalPassTests.ChannelProvider([]));
        await workspace.InitializeAsync(default);
        var host = (await workspace.OpenSessionAsync(id, default))!;

        Assert.NotNull(await workspace.RunFinalPassAsync(host, Chosen, null, default));

        Assert.Equal(TranscriptSessionStatus.Completed, host.Document.Status);
        Assert.NotEmpty(host.Document.ActiveSegments);
    }

    [Fact]
    public async Task A_meeting_with_speaker_labels_can_be_rerun_though_the_pass_after_Stop_leaves_it_alone()
    {
        var id = await this.SeedMeetingAsync(TranscriptSessionStatus.Completed, speakers: [new TranscriptSpeaker("speaker-1", "Speaker 1", "Dana")]);
        await using var workspace = this.Workspace(new MeetingFinalPassTests.ChannelProvider([]));
        await workspace.InitializeAsync(default);
        var host = (await workspace.OpenSessionAsync(id, default))!;

        Assert.NotNull(await workspace.RunFinalPassAsync(host, Chosen, null, default));

        Assert.Equal("Dana", host.Document.Speakers.Single(s => s.Id == "speaker-1").Name);
    }

    [Fact]
    public async Task A_canceled_pass_keeps_its_marker_and_the_next_launch_offers_it_again()
    {
        var id = await this.SeedMeetingAsync(TranscriptSessionStatus.Completed, pending: true);
        await using var workspace = this.Workspace(new MeetingFinalPassTests.ChannelProvider([]));
        await workspace.InitializeAsync(default);
        var host = (await workspace.OpenSessionAsync(id, default))!;
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.RunFinalPassAsync(host, Chosen, null, canceled.Token));

        Assert.Equal([id], await workspace.FinalPassPendingSessionsAsync(default));
        Assert.Equal(1, host.Document.ActiveResultVersion);
    }

    [Fact]
    public async Task A_pass_that_had_its_turn_clears_the_marker()
    {
        var id = await this.SeedMeetingAsync(TranscriptSessionStatus.Completed, pending: true);
        await using var workspace = this.Workspace(new MeetingFinalPassTests.ChannelProvider([]));
        await workspace.InitializeAsync(default);
        var host = (await workspace.OpenSessionAsync(id, default))!;

        Assert.NotNull(await workspace.RunFinalPassAsync(host, Chosen, null, default));

        Assert.Empty(await workspace.FinalPassPendingSessionsAsync(default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pass_that_could_not_run_clears_its_marker_unless_the_app_is_leaving(bool leaving)
    {
        var id = await this.SeedMeetingAsync(TranscriptSessionStatus.Completed, pending: true);
        await using var workspace = this.Workspace();
        await workspace.InitializeAsync(default);
        var host = (await workspace.OpenSessionAsync(id, default))!;
        if (leaving)
        {
            await workspace.StopLiveForExitAsync(TimeSpan.FromSeconds(2));
        }

        // No Nemotron is installed here, so the pass after Stop notes that and ends.
        Assert.Null(await workspace.RunFinalPassAsync(host, null, null, default));

        Assert.Equal(leaving, (await workspace.FinalPassPendingSessionsAsync(default)).Contains(id));
    }

    [Fact]
    public async Task A_failed_rerun_leaves_the_transcript_and_the_marker_as_they_were_and_says_why()
    {
        var id = await this.SeedMeetingAsync(TranscriptSessionStatus.Completed, pending: true);
        await using var workspace = this.Workspace(new FailingProvider());
        await workspace.InitializeAsync(default);
        var host = (await workspace.OpenSessionAsync(id, default))!;

        Assert.Null(await workspace.RunFinalPassAsync(host, Chosen, null, default));

        Assert.Equal(1, host.Document.ActiveResultVersion);
        Assert.Equal(TranscriptSessionStatus.Completed, host.Document.Status);
        Assert.Contains("the model went away", host.Document.Notes.Last(), StringComparison.Ordinal);
        Assert.Equal([id], await workspace.FinalPassPendingSessionsAsync(default));
    }

    [Fact]
    public async Task A_recording_from_one_source_is_transcribed_from_its_saved_audio()
    {
        var tone = Enumerable.Range(0, 4 * 16_000).Select(i => 0.3f * MathF.Sin(i * 0.05f)).ToArray();
        await using var workspace = this.Workspace(new FixedTextProvider("hello"), new LiveSourceTests.FakeSource(1, tone));
        await workspace.InitializeAsync(default);
        var session = await workspace.StartLiveAsync(TranscriptionWorkspaceService.RecordOnly, null, AudioRetention.KeepAudio, "Recording", default);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (session.Elapsed < TimeSpan.FromSeconds(3.9) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await workspace.StopLiveAsync(default);
        var host = session.Host!;
        Assert.False(workspace.HasMeetingChannels(host.Document));

        await workspace.RerunAsync(host, Chosen, null, default);

        Assert.Equal(TranscriptSessionStatus.Completed, host.Document.Status);
        Assert.Equal(2, host.Document.ActiveResultVersion);
        Assert.Equal("hello", Assert.Single(host.Document.ActiveSegments).RawText);
    }
}
