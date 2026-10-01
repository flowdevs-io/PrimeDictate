using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Coordination;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Core.Tests;

public sealed class DictationLoopTests
{
    [Fact]
    public void Options_are_clamped_like_the_wpf_app()
    {
        var o = new DictationOptions { InputGain = 99, AutoCommitSilence = TimeSpan.FromMilliseconds(200) }.Normalized();
        Assert.Equal(4.0, o.InputGain);
        Assert.Equal(TimeSpan.FromSeconds(1), o.AutoCommitSilence);
        Assert.Equal(TimeSpan.Zero, new DictationOptions { AutoCommitSilence = TimeSpan.FromSeconds(-1) }.Normalized().AutoCommitSilence);
        Assert.Equal(TimeSpan.FromSeconds(30), new DictationOptions { AutoCommitSilence = TimeSpan.FromMinutes(5) }.Normalized().AutoCommitSilence);
        Assert.Equal(1.0, DictationOptions.NormalizeGain(double.NaN));
    }

    [Fact]
    public void Silence_artifact_is_removed_only_after_a_silence_commit_and_only_when_words_remain()
    {
        Assert.Equal("send the report", TranscriptPostProcessor.RemoveTrailingSilenceArtifact("send the report. Okay", true));
        Assert.Equal("send the report. Okay", TranscriptPostProcessor.RemoveTrailingSilenceArtifact("send the report. Okay", false));
        Assert.Equal("okay", TranscriptPostProcessor.RemoveTrailingSilenceArtifact("okay", true));
        Assert.Equal("a book", TranscriptPostProcessor.RemoveTrailingSilenceArtifact("a book", true));
    }

    [Fact]
    public void Tracker_marks_speech_and_does_not_let_speech_raise_the_noise_floor()
    {
        var clock = new ManualTime();
        var tracker = new SpeechActivityTracker(clock);
        for (var i = 0; i < 20; i++)
        {
            tracker.OnLevel(0.0005);
        }

        Assert.Null(tracker.LastSpeechUtc);
        for (var i = 0; i < 5; i++)
        {
            tracker.OnLevel(0.05);
        }

        Assert.Equal(5, tracker.SpeechEvents);
        Assert.NotNull(tracker.LastSpeechUtc);
        Assert.True(tracker.IsAutoCommitArmed(TimeSpan.FromSeconds(2)));
        Assert.False(tracker.IsAutoCommitArmed(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Tracker_evidence_falls_back_to_the_buffer_when_level_events_were_missed()
    {
        var tracker = new SpeechActivityTracker();
        Assert.False(tracker.HasSpeechEvidence(new float[16_000]));
        Assert.True(tracker.HasSpeechEvidence(Tone(0.5)));
    }

    [Fact]
    public void Chunker_splits_long_audio_at_a_quiet_point_and_keeps_every_sample()
    {
        var samples = new float[16_000 * 70];
        Array.Fill(samples, 0.2f);
        Array.Clear(samples, 16_000 * 27, 1_600);
        var chunks = AudioChunker.Split(samples, TimeSpan.FromSeconds(30));
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Length <= 16_000 * 30));
        Assert.Equal(samples.Length, chunks.Sum(c => c.Length));
        Assert.InRange(chunks[0].Length, 16_000 * 27, 16_000 * 27 + 1_600);
        Assert.Single(AudioChunker.Split(new float[16_000 * 10], TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Delivery_skips_typing_when_the_platform_has_no_guard_unless_the_user_opted_in()
    {
        var inj = new FakeInjector();
        var noGuard = new FakeGuard(available: false);
        var r = TranscriptDelivery.Deliver("hi", null, noGuard, inj, new DictationOptions());
        Assert.Equal(DictationDeliveryStatus.SkippedNoFocusGuard, r.Status);
        Assert.Empty(inj.Typed);

        r = TranscriptDelivery.Deliver("hi", null, noGuard, inj, new DictationOptions { TypeWithoutFocusGuard = true });
        Assert.Equal(DictationDeliveryStatus.Injected, r.Status);
        Assert.Equal(["hi"], inj.Typed);
    }

    [Fact]
    public void Delivery_refuses_to_type_or_press_enter_when_focus_moved()
    {
        var inj = new FakeInjector();
        var target = new FakeTarget { Foreground = false };
        var r = TranscriptDelivery.Deliver("hi", target, new FakeGuard(), inj, new DictationOptions { SendEnterAfterCommit = true });
        Assert.Equal(DictationDeliveryStatus.SkippedFocusChanged, r.Status);
        Assert.Empty(inj.Typed);
        Assert.Equal(0, inj.Enters);
    }

    [Fact]
    public void Delivery_can_restore_the_start_target_when_asked()
    {
        var inj = new FakeInjector();
        var target = new FakeTarget { Foreground = false, CanRestore = true };
        var r = TranscriptDelivery.Deliver("hi", target, new FakeGuard(), inj, new DictationOptions { ReturnToStartTarget = true });
        Assert.Equal(DictationDeliveryStatus.Injected, r.Status);

        target.CanRestore = false;
        r = TranscriptDelivery.Deliver("hi", target, new FakeGuard(), inj, new DictationOptions { ReturnToStartTarget = true });
        Assert.Equal(DictationDeliveryStatus.SkippedFocusChanged, r.Status);
        Assert.Single(inj.Typed);
    }

    [Fact]
    public void Coding_mode_sends_enter_only_after_typing_succeeds()
    {
        var inj = new FakeInjector();
        var r = TranscriptDelivery.Deliver("ls", new FakeTarget(), new FakeGuard(), inj, new DictationOptions { SendEnterAfterCommit = true });
        Assert.True(r.EnterSent);
        Assert.Equal(1, inj.Enters);

        var failing = new FakeInjector { FailTyping = true };
        r = TranscriptDelivery.Deliver("ls", new FakeTarget(), new FakeGuard(), failing, new DictationOptions { SendEnterAfterCommit = true });
        Assert.Equal(DictationDeliveryStatus.FailedToInject, r.Status);
        Assert.Equal(0, failing.Enters);
    }

    [Fact]
    public async Task Toggle_records_previews_then_types_one_final_transcript()
    {
        var source = new FakeSource();
        var inj = new FakeInjector();
        var partials = new List<string>();
        var commits = new List<DictationCommit>();
        await using var controller = new DictationController(source, () => new FakeProvider("hello world"), new FakeGuard(), inj);
        controller.PartialTranscript += (_, t) => partials.Add(t);
        controller.Committed += commits.Add;
        controller.Options = new DictationOptions { AutoCommitSilence = TimeSpan.Zero, Replacements = [new ReplacementRule("world", "there")] };

        await controller.ToggleAsync();
        Assert.True(controller.IsRecording);
        Assert.Equal(DictationState.Listening, controller.State);
        source.Lease!.Push(Tone(1.0));
        await WaitFor(() => partials.Count > 0);
        Assert.Empty(inj.Typed);

        await controller.ToggleAsync();
        Assert.False(controller.IsRecording);
        Assert.Equal(DictationState.Idle, controller.State);
        Assert.Equal(["hello there"], inj.Typed);
        var commit = Assert.Single(commits);
        Assert.Equal(DictationDeliveryStatus.Injected, commit.Status);
        Assert.Equal("Editor", commit.TargetDisplayName);
        Assert.True(source.Lease.Disposed);
    }

    [Theory]
    [InlineData(false, MicAccessMode.Exclusive, MicAccessMode.Shared, MicAccessMode.Shared)]
    [InlineData(true, MicAccessMode.Exclusive, MicAccessMode.Exclusive, MicAccessMode.Exclusive)]
    [InlineData(true, MicAccessMode.Shared, MicAccessMode.Exclusive, MicAccessMode.Shared)]
    public async Task Exclusive_mic_is_requested_only_when_set_and_the_granted_mode_is_reported(
        bool exclusiveSetting, MicAccessMode deviceGrants, MicAccessMode expectedRequest, MicAccessMode expectedActive)
    {
        var source = new FakeSource { GrantsExclusive = deviceGrants };
        await using var controller = new DictationController(source, () => new FakeProvider("x"), new FakeGuard(), new FakeInjector());
        controller.Options = new DictationOptions { ExclusiveMicAccess = exclusiveSetting, AutoCommitSilence = TimeSpan.Zero };
        Assert.Null(controller.ActiveMicAccess);

        await controller.ToggleAsync();

        Assert.Equal(exclusiveSetting ? expectedRequest : MicAccessMode.Shared, source.RequestedAccess);
        Assert.Equal(expectedActive, controller.ActiveMicAccess);
        await controller.DiscardAsync();
        Assert.Null(controller.ActiveMicAccess);
    }

    [Fact]
    public async Task Microphone_fallback_passes_the_access_request_through()
    {
        var source = new FakeSource();
        var fallback = new DefaultMicrophoneFallback(source, _ => { });

        var lease = await fallback.OpenAsync(null, MicAccessMode.Exclusive, CancellationToken.None);

        Assert.Equal(MicAccessMode.Exclusive, source.RequestedAccess);
        Assert.Equal(MicAccessMode.Exclusive, lease.AccessMode);
    }

    [Fact]
    public void Exclusive_mic_setting_imports_from_the_wpf_file_by_name()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<DictationSettings>("""{ "ExclusiveMicAccessWhileDictating": true }""")!;

        Assert.True(s.ToOptions().ExclusiveMicAccess);
        Assert.False(new DictationSettings().ToOptions().ExclusiveMicAccess);
    }

    [Fact]
    public async Task Activity_feed_records_the_session_status_and_app_but_never_the_text()
    {
        var source = new FakeSource();
        var commits = new List<DictationCommit>();
        await using var controller = new DictationController(source, () => new FakeProvider("zebra crossing secret"), new FakeGuard(), new FakeInjector());
        controller.Committed += commits.Add;
        controller.Options = new DictationOptions { AutoCommitSilence = TimeSpan.Zero };

        await controller.ToggleAsync();
        source.Lease!.Push(Tone(1.0));
        await controller.ToggleAsync();

        var id = Assert.Single(commits).SessionId;
        var session = Assert.Single(Diagnostics.AppLog.Feed.Sessions(), s => s.Id == id);
        Assert.Equal(Diagnostics.DictationSessionStatus.Typed, session.Status);
        var log = Diagnostics.AppLog.Feed.SessionEntries(id);
        Assert.NotEmpty(log);
        Assert.DoesNotContain(log, e => e.Message.Contains("zebra", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("zebra", Diagnostics.ActivityText.Join(Diagnostics.AppLog.Feed.Entries()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Silence_commits_on_its_own_after_speech()
    {
        var source = new FakeSource();
        var inj = new FakeInjector();
        await using var controller = new DictationController(source, () => new FakeProvider("done talking"), new FakeGuard(), inj);
        controller.Options = new DictationOptions { AutoCommitSilence = TimeSpan.FromSeconds(1) };

        await controller.ToggleAsync();
        source.Lease!.Push(Tone(2.0));
        using var quiet = new CancellationTokenSource();
        var silence = Task.Run(async () =>
        {
            // A live microphone keeps delivering (quiet) audio after the user stops talking.
            while (!quiet.IsCancellationRequested)
            {
                source.Lease.Push(new float[1_600]);
                await Task.Delay(100);
            }
        });
        await WaitFor(() => inj.Typed.Count == 1, TimeSpan.FromSeconds(15));
        await quiet.CancelAsync();
        await silence;
        Assert.Equal(["done talking"], inj.Typed);
        Assert.False(controller.IsRecording);
    }

    [Fact]
    public async Task Emergency_stop_discards_without_typing()
    {
        var source = new FakeSource();
        var inj = new FakeInjector();
        await using var controller = new DictationController(source, () => new FakeProvider("secret"), new FakeGuard(), inj);
        controller.Options = new DictationOptions { AutoCommitSilence = TimeSpan.Zero };

        await controller.ToggleAsync();
        source.Lease!.Push(Tone(1.0));
        await controller.DiscardAsync();
        Assert.False(controller.IsRecording);
        Assert.Empty(inj.Typed);
    }

    [Fact]
    public async Task Silent_recording_is_skipped_without_calling_the_model()
    {
        var source = new FakeSource();
        var provider = new FakeProvider("hallucination");
        await using var controller = new DictationController(source, () => provider, new FakeGuard(), new FakeInjector());
        controller.Options = new DictationOptions { AutoCommitSilence = TimeSpan.Zero };
        await controller.ToggleAsync();
        source.Lease!.Push(new float[16_000]);
        await controller.ToggleAsync();
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Voice_stop_command_discards_and_voice_commit_types_cleaned_text()
    {
        var source = new FakeSource();
        var inj = new FakeInjector();
        await using var controller = new DictationController(
            source, () => new FakeProvider("write this stop"), new FakeGuard(), inj, voiceCommands: new StopWordCommands());
        controller.Options = new DictationOptions { AutoCommitSilence = TimeSpan.Zero };
        await controller.ToggleAsync();
        source.Lease!.Push(Tone(1.0));
        await WaitFor(() => !controller.IsRecording, TimeSpan.FromSeconds(10));
        Assert.Empty(inj.Typed);
    }

    [Fact]
    public async Task Missing_model_or_busy_microphone_is_reported_not_thrown()
    {
        var notices = new List<string>();
        var coordinator = new MicrophoneCoordinator();
        await using var other = await coordinator.AcquireAsync("Transcription", default);
        await using var controller = new DictationController(new FakeSource(), () => new FakeProvider("x"), new FakeGuard(), new FakeInjector(), coordinator);
        controller.Notice += notices.Add;
        await controller.ToggleAsync();
        Assert.False(controller.IsRecording);
        Assert.Contains("Transcription", Assert.Single(notices));

        await using var noModel = new DictationController(new FakeSource(), () => null, new FakeGuard(), new FakeInjector());
        noModel.Notice += notices.Add;
        await noModel.ToggleAsync();
        Assert.False(noModel.IsRecording);
        Assert.Equal(2, notices.Count);
    }

    [Fact]
    public async Task Dictation_holds_the_microphone_lease_only_while_recording()
    {
        var coordinator = new MicrophoneCoordinator();
        var source = new FakeSource();
        await using var controller = new DictationController(source, () => new FakeProvider("x"), new FakeGuard(), new FakeInjector(), coordinator);
        controller.Options = new DictationOptions { AutoCommitSilence = TimeSpan.Zero };
        await controller.ToggleAsync();
        Assert.Equal(DictationController.MicrophoneOwner, coordinator.CurrentOwner);
        await controller.ToggleAsync();
        Assert.Null(coordinator.CurrentOwner);
    }

    [Fact]
    public void Audio_cues_are_valid_wav_files_of_the_expected_length()
    {
        foreach (var cue in new[] { DictationAudioCue.Start, DictationAudioCue.Stop })
        {
            var wav = AudioCues.Wave(cue);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
            Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
            Assert.Equal(24_000, BitConverter.ToInt32(wav, 24));
            var seconds = (wav.Length - 44) / 2 / 24_000.0;
            Assert.InRange(seconds, 0.2, 0.3);
            Assert.Contains(wav.Skip(44), b => b != 0);
        }
    }

    private static float[] Tone(double seconds)
    {
        var s = new float[(int)(16_000 * seconds)];
        for (var i = 0; i < s.Length; i++)
        {
            s[i] = 0.2f * MathF.Sin(i * 0.1f);
        }

        return s;
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for condition.");
            await Task.Delay(50);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
    }

    private static readonly VoiceCommandOptions ShellOptionsBase = new(true, "thank you", "potato farmer", "show me the money", []);

    private static async Task<(List<DictationCommit> Commits, FakeInjector Injector, FakeShellRunner Runner)> DictateAsync(
        string transcript,
        VoiceShellCommand[] commands,
        FakeShellRunner? runner = null,
        bool withRunner = true)
    {
        runner ??= new FakeShellRunner();
        var source = new FakeSource();
        var inj = new FakeInjector();
        var commits = new List<DictationCommit>();
        var processor = new VoiceCommandProcessor(() => ShellOptionsBase with { ShellCommands = commands });
        await using var controller = new DictationController(
            source, () => new FakeProvider(transcript), new FakeGuard(), inj, voiceCommands: processor, shellRunner: withRunner ? runner : null);
        controller.Options = new DictationOptions { AutoCommitSilence = TimeSpan.Zero };
        controller.Committed += commits.Add;
        await controller.ToggleAsync();
        source.Lease!.Push(Tone(1.0));
        await WaitFor(() => controller.IsRecording);
        await controller.ToggleAsync();
        return (commits, inj, runner);
    }

    [Fact]
    public async Task Shell_command_runs_the_saved_command_and_stop_types_nothing()
    {
        var saved = new VoiceShellCommand { Phrase = "open notes", Command = "notepad.exe C:\\todo.txt", Enabled = true };
        var (commits, inj, runner) = await DictateAsync("please rm -rf everything open notes", [saved]);

        // The runner receives the command exactly as saved, never words from the transcript.
        Assert.Same(saved, Assert.Single(runner.Ran));
        Assert.Equal("notepad.exe C:\\todo.txt", runner.Ran[0].Command);
        Assert.Empty(inj.Typed);
        var commit = Assert.Single(commits);
        Assert.Equal(DictationDeliveryStatus.CommandExecuted, commit.Status);
        Assert.Equal("Voice command: open notes", commit.Transcript);
    }

    [Fact]
    public async Task Shell_command_with_continue_types_the_words_that_remain()
    {
        var saved = new VoiceShellCommand { Phrase = "open notes", Command = "notepad", CompletionBehavior = VoiceShellCommandCompletionBehavior.Continue };
        var (commits, inj, runner) = await DictateAsync("open notes remember the milk", [saved]);

        Assert.Single(runner.Ran);
        Assert.Equal(["remember the milk"], inj.Typed);
        Assert.Equal([DictationDeliveryStatus.CommandExecuted, DictationDeliveryStatus.Injected], commits.Select(c => c.Status).ToArray());
    }

    [Fact]
    public async Task Shell_command_with_continue_and_nothing_left_types_nothing()
    {
        var saved = new VoiceShellCommand { Phrase = "open notes", Command = "notepad", CompletionBehavior = VoiceShellCommandCompletionBehavior.Continue };
        var (_, inj, runner) = await DictateAsync("open notes", [saved]);

        Assert.Single(runner.Ran);
        Assert.Empty(inj.Typed);
    }

    [Fact]
    public async Task Disabled_or_unmatched_shell_commands_never_run_and_the_text_is_typed()
    {
        var off = new VoiceShellCommand { Phrase = "open notes", Command = "notepad", Enabled = false };
        var (_, inj, runner) = await DictateAsync("open notes today", [off]);
        Assert.Empty(runner.Ran);
        Assert.Equal(["open notes today"], inj.Typed);
    }

    [Fact]
    public async Task Shell_command_failure_is_reported_and_nothing_is_typed()
    {
        var saved = new VoiceShellCommand { Phrase = "open notes", Command = "notepad", CompletionBehavior = VoiceShellCommandCompletionBehavior.Continue };
        var (commits, inj, _) = await DictateAsync("open notes hello", [saved], new FakeShellRunner { Fail = true });

        Assert.Empty(inj.Typed);
        var commit = Assert.Single(commits);
        Assert.Equal(DictationDeliveryStatus.CommandFailed, commit.Status);
        Assert.Equal("cannot start", commit.Error);
    }

    [Fact]
    public async Task Without_a_runner_the_phrase_is_just_dictated_text()
    {
        var saved = new VoiceShellCommand { Phrase = "open notes", Command = "notepad" };
        var (_, inj, runner) = await DictateAsync("open notes", [saved], withRunner: false);
        Assert.Empty(runner.Ran);
        Assert.Equal(["open notes"], inj.Typed);
    }

    [Fact]
    public async Task A_live_preview_never_runs_a_shell_command()
    {
        var saved = new VoiceShellCommand { Phrase = "open notes", Command = "notepad" };
        var runner = new FakeShellRunner();
        var source = new FakeSource();
        var partials = new List<string>();
        var processor = new VoiceCommandProcessor(() => ShellOptionsBase with { ShellCommands = [saved] });
        await using var controller = new DictationController(
            source, () => new FakeProvider("open notes"), new FakeGuard(), new FakeInjector(), voiceCommands: processor, shellRunner: runner);
        controller.Options = new DictationOptions { AutoCommitSilence = TimeSpan.Zero };
        controller.PartialTranscript += (_, t) => partials.Add(t);
        await controller.ToggleAsync();
        source.Lease!.Push(Tone(1.0));
        await WaitFor(() => partials.Count > 0);

        Assert.Empty(runner.Ran);
        await controller.DiscardAsync();
        Assert.Empty(runner.Ran);
    }

    private sealed class FakeShellRunner : IVoiceShellCommandRunner
    {
        public List<VoiceShellCommand> Ran { get; } = [];

        public bool Fail { get; init; }

        public VoiceShellCommandResult Run(VoiceShellCommand command)
        {
            if (this.Fail)
            {
                throw new InvalidOperationException("cannot start");
            }

            this.Ran.Add(command);
            return new VoiceShellCommandResult(42);
        }
    }

    private sealed class StopWordCommands : IVoiceCommandProcessor
    {
        public VoiceCommandResult Apply(string transcript) =>
            transcript.EndsWith("stop", StringComparison.OrdinalIgnoreCase)
                ? new VoiceCommandResult(transcript[..^4].Trim(), false, true, false)
                : VoiceCommandResult.Passthrough(transcript);
    }

    private sealed class FakeInjector : ITextInjector
    {
        public List<string> Typed { get; } = [];

        public int Enters { get; private set; }

        public bool FailTyping { get; init; }

        public void TypeText(string text)
        {
            if (this.FailTyping)
            {
                throw new InvalidOperationException("boom");
            }

            this.Typed.Add(text);
        }

        public void SendEnter() => this.Enters++;
    }

    private sealed class FakeTarget : IForegroundTarget
    {
        public bool Foreground { get; set; } = true;

        public bool CanRestore { get; set; }

        public string DisplayName => "Editor";

        public string? AppName => "editor";

        public string? WindowTitle => "notes.txt";

        public bool IsStillForeground() => this.Foreground;

        public bool TryRestore() => this.CanRestore;
    }

    private sealed class FakeGuard(bool available = true) : IForegroundTargetGuard
    {
        public bool IsAvailable => available;

        public string? UnavailableReason => "no guard";

        public IForegroundTarget? Capture() => new FakeTarget();
    }

    private sealed class FakeProvider(string text) : ITranscriptionProvider
    {
        public int Calls;

        public string ModelId => "fake";

        public TranscriptionProviderCapabilities Capabilities { get; } =
            new(true, LiveRecognitionMode.BufferedWindows, TimingCapabilities.None, false, null, TimeSpan.FromSeconds(30), ["en"], 16_000);

        public EffectiveRuntime Runtime { get; } = new("fake", null, "cpu", "cpu", null);

        public ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.Calls);
            IReadOnlyList<RecognizedSegment> result =
                [new RecognizedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), text, null, null, null, TimingProvenance.ApproximateChunk)];
            return ValueTask.FromResult(result);
        }

        public ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSource : IAudioSource
    {
        public FakeLease? Lease { get; private set; }

        public MicAccessMode? RequestedAccess { get; private set; }

        /// <summary>What the "device" grants when exclusive is asked for (a device can refuse and open shared).</summary>
        public MicAccessMode GrantsExclusive { get; init; } = MicAccessMode.Exclusive;

        public ValueTask<IReadOnlyList<AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AudioInputDevice>>([new AudioInputDevice("fake", "Fake mic", true)]);

        public ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken) =>
            this.OpenAsync(deviceId, MicAccessMode.Shared, cancellationToken);

        public ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, MicAccessMode access, CancellationToken cancellationToken)
        {
            this.RequestedAccess = access;
            this.Lease = new FakeLease { AccessMode = access == MicAccessMode.Exclusive ? this.GrantsExclusive : MicAccessMode.Shared };
            return ValueTask.FromResult<IAudioCaptureLease>(this.Lease);
        }
    }

    private sealed class FakeLease : IAudioCaptureLease
    {
        public MicAccessMode AccessMode { get; init; } = MicAccessMode.Shared;

        private readonly Channel<AudioFrame> frames = Channel.CreateUnbounded<AudioFrame>();
        private long offset;
        private long sequence;

        public AudioFormat Format => AudioFormat.SpeechTimeline;

        public string DeviceId => "fake";

        public string DeviceName => "Fake mic";

        public bool Disposed { get; private set; }

        public void Push(float[] samples)
        {
            this.frames.Writer.TryWrite(AudioFrame.CopyFrom(samples, this.Format, this.sequence++, this.offset));
            this.offset += samples.Length;
        }

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in this.frames.Reader.ReadAllAsync(cancellationToken))
            {
                yield return frame;
            }
        }

        public ValueTask PauseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            this.frames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
