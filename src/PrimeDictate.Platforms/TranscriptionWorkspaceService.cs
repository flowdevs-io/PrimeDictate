using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Coordination;
using PrimeDictate.Core.Export;
using PrimeDictate.Core.Pipeline;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Sessions;
using PrimeDictate.Core.Storage;
using PrimeDictate.Core.Transcripts;
using PrimeDictate.Platforms.Audio;
using PrimeDictate.Platforms.Media;
using PrimeDictate.Platforms.Nemotron;
using PrimeDictate.Platforms.Speech;

namespace PrimeDictate.Platforms;

/// <summary>A speech model the user can pick. Providers load lazily the first time they are needed.</summary>
public sealed record SpeechModelChoice(string ModelId, string DisplayName, string? Language, bool DetectsSpeakers, string? Note);

/// <summary>
/// Wires the transcription pipeline to real components for a desktop shell: SQLite session store,
/// installed Whisper ONNX models, WAV/ffmpeg decoding, and microphone capture. It exposes what a UI
/// needs and holds no UI types.
/// </summary>
public sealed class TranscriptionWorkspaceService : IAsyncDisposable
{
    private readonly AppDataPaths paths;
    private readonly SqliteTranscriptionSessionStore store;
    private readonly ModelLeaseScheduler scheduler = new();
    private readonly MicrophoneCoordinator microphone = new();
    private readonly IAudioDecoder decoder;
    private readonly Dictionary<string, InstalledWhisperModel> whisperModels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InstalledSpeechModel> onnxModels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ITranscriptionProvider> providers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim providerGate = new(1, 1);
    private NemotronWorker? nemotronWorker;
    private string? nemotronWorkerKey;
    private List<NemotronSetup> nemotron = [];
    private readonly FileTranscriptionRunner runner;
    private readonly IAudioSource? audioSource;
    private readonly ISystemAudioSource? systemAudioSource;
    private LiveTranscriptionSession? live;

    public TranscriptionWorkspaceService(AppDataPaths? paths = null, IAudioSource? audioSource = null, bool probeMicrophone = true)
    {
        this.paths = paths ?? AppDataPaths.Default;
        AppDataPaths.EnsurePrivateDirectory(this.paths.TranscriptionDirectory);
        this.store = SqliteTranscriptionSessionStore.Create(this.paths);
        var ffmpeg = FfmpegAudioDecoder.TryCreate();
        this.HasFfmpeg = ffmpeg is not null;
        this.decoder = new CompositeAudioDecoder(new WavAudioDecoder(), ffmpeg);
        this.runner = new FileTranscriptionRunner(this.scheduler, this.store);
        this.audioSource = audioSource;
        if (probeMicrophone)
        {
            this.systemAudioSource = SystemAudioSources.TryCreate(out var reason);
            this.SystemAudioUnavailableReason = reason;
        }

        if (this.audioSource is null && probeMicrophone)
        {
            try
            {
                this.audioSource = new MiniAudioCaptureSource();
            }
            catch (AudioSourceException ex)
            {
                this.MicrophoneUnavailableReason = ex.Message;
            }
        }
    }

    public bool HasFfmpeg { get; }

    public string? MicrophoneUnavailableReason { get; private set; }

    public bool CanRecord => this.audioSource is not null || this.systemAudioSource is not null;

    public bool CanRecordMicrophone => this.audioSource is not null;

    public bool CanRecordSystemAudio => this.systemAudioSource is not null;

    public string? SystemAudioUnavailableReason { get; private set; }

    public LiveTranscriptionSession? ActiveLiveSession => this.live;

    public AppDataPaths Paths => this.paths;

    /// <summary>Shared with dictation so a meeting and a dictation never hold the microphone at once.</summary>
    public MicrophoneCoordinator Microphone => this.microphone;

    public IAudioSource? MicrophoneSource => this.audioSource;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await this.store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var interrupted = await this.store.MarkInterruptedSessionsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var summary in interrupted)
        {
            // A killed recording leaves a WAV whose header was never finalized; make it playable again.
            var directory = this.store.GetSessionMediaDirectory(summary.SessionId);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var wav in Directory.EnumerateFiles(directory, "recording-16k-*.wav"))
            {
                try
                {
                    WavFileWriter.RepairHeader(wav);
                }
                catch (IOException)
                {
                    // Locked or unreadable: leave it as it is.
                }
            }
        }
    }

    private sealed record NemotronSetup(string WorkerPath, string? CudaWorkerPath, PinnedNemotronModel Asr, NemotronModelFiles Files);

    /// <summary>Raised with things the user should see, such as which backend Nemotron actually runs on or why it fell back.</summary>
    public event Action<string>? Notice;

    private string nemotronPreference = (Environment.GetEnvironmentVariable("PRIMEDICTATE_NEMO_DEVICE") ?? "auto").Trim().ToLowerInvariant();
    private string? nemotronWorkerPreference;

    /// <summary>
    /// Which device Nemotron should use: <c>auto</c> (CUDA when a CUDA worker is installed, else CPU), <c>cpu</c>,
    /// or <c>cuda:N</c>. Default comes from the PRIMEDICTATE_NEMO_DEVICE environment variable. Vulkan is not
    /// offered because it aborts at the pinned commit. Takes effect the next time the model is used.
    /// </summary>
    public string NemotronDevice
    {
        get => this.nemotronPreference;
        set => this.nemotronPreference = string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant();
    }

    /// <summary>The backend the running Nemotron worker reported, for example "cuda:0" or "cpu". Null until it has started.</summary>
    public string? NemotronBackend => this.nemotronWorker?.EffectiveBackend;

    private void RaiseNotice(string message) => this.Notice?.Invoke(message);

    private IEnumerable<string> CudaRuntimeDirectories(string workerPath)
    {
        var explicitBin = Environment.GetEnvironmentVariable("PRIMEDICTATE_CUDA_BIN");
        if (!string.IsNullOrWhiteSpace(explicitBin))
        {
            yield return explicitBin;
        }

        var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (!string.IsNullOrWhiteSpace(cudaPath))
        {
            yield return Path.Combine(cudaPath, "bin", "x64");
            yield return Path.Combine(cudaPath, "bin");
        }

        if (Path.GetDirectoryName(workerPath) is { } dir)
        {
            yield return dir;
        }
    }

    private Task<NemotronWorker> StartNemotronWorkerAsync(NemotronSetup setup, CancellationToken cancellationToken) =>
        NemotronWorker.StartPreferredAsync(setup.WorkerPath, setup.CudaWorkerPath, setup.Files, this.nemotronPreference, this.CudaRuntimeDirectories, this.RaiseNotice, cancellationToken);

    /// <summary>Everything the user can pick: installed Whisper ONNX models, plus Nemotron when its worker and pinned model files are present.</summary>
    public IReadOnlyList<SpeechModelChoice> AvailableModels()
    {
        this.whisperModels.Clear();
        this.onnxModels.Clear();
        var choices = new List<SpeechModelChoice>();
        foreach (var model in WhisperOnnxModelLocator.Discover(this.paths.ModelsDirectory))
        {
            this.whisperModels[model.ModelId] = model;
            choices.Add(new SpeechModelChoice(model.ModelId, model.DisplayName, model.IsEnglishOnly ? "en" : null, false, null));
        }

        foreach (var model in SpeechModelLocator.Discover(this.paths.ModelsDirectory).Where(m => m.Backend is LegacyBackend.Parakeet or LegacyBackend.Moonshine))
        {
            this.onnxModels[model.ModelId] = model;
            choices.Add(new SpeechModelChoice(model.ModelId, model.DisplayName, model.IsEnglishOnly ? "en" : null, false, null));
        }

        this.nemotron = FindNemotron();
        foreach (var n in this.nemotron)
        {
            var speakers = n.Files.DiarizerPath is not null;
            var gpu = n.CudaWorkerPath is not null && this.nemotronPreference != "cpu";
            var englishOnly = n.Asr == NemotronPins.EnglishOnly;
            choices.Add(new SpeechModelChoice(
                $"nemotron:{n.Asr.Id}",
                (englishOnly ? "Nemotron English" : "Nemotron 3.5") + (gpu ? " (GPU, CPU if it fails)" : " (CPU)") + (speakers ? ", speakers" : string.Empty),
                englishOnly ? "en-US" : null,
                speakers,
                speakers ? null : "Speaker detection needs the Nemotron-3-Diarization file next to the speech model."));
        }

        return choices;
    }

    /// <summary>
    /// The model that gives the live draft of a two-pass meeting. Nemotron is reserved for the final pass, so when it is
    /// the chosen model the draft comes from Parakeet if installed, else a Whisper model (English-only first, for speed).
    /// Any other choice is used as it is. Null when nothing but Nemotron is installed.
    /// </summary>
    /// <summary>"No live text": the meeting is only recorded, and the final pass transcribes it after Stop.</summary>
    public static SpeechModelChoice RecordOnly { get; } = new(RecordOnlyProvider.Id, "Record only", null, false, null);

    public static SpeechModelChoice? LiveDraftModel(SpeechModelChoice selected, IReadOnlyList<SpeechModelChoice> available)
    {
        if (!selected.ModelId.StartsWith("nemotron:", StringComparison.Ordinal))
        {
            return selected;
        }

        return available.FirstOrDefault(m => m.ModelId.StartsWith("parakeet", StringComparison.Ordinal))
            ?? available.FirstOrDefault(m => m.ModelId.StartsWith("whisper", StringComparison.Ordinal) && m.Language == "en")
            ?? available.FirstOrDefault(m => m.ModelId.StartsWith("whisper", StringComparison.Ordinal));
    }

    private List<NemotronSetup> FindNemotron()
    {
        var folder = Path.Combine(this.paths.ModelsDirectory, "nemotron");
        var exeName = OperatingSystem.IsWindows() ? "nemo-speech.exe" : "nemo-speech";
        var worker = Environment.GetEnvironmentVariable("PRIMEDICTATE_NEMO_SPEECH");
        if (string.IsNullOrWhiteSpace(worker) || !File.Exists(worker))
        {
            worker = new[] { Path.Combine(folder, exeName), Path.Combine(AppContext.BaseDirectory, exeName) }.FirstOrDefault(File.Exists);
        }

        // A CUDA build lives beside the CPU one, in models\nemotron\cuda, or wherever PRIMEDICTATE_NEMO_SPEECH_CUDA points.
        var cuda = Environment.GetEnvironmentVariable("PRIMEDICTATE_NEMO_SPEECH_CUDA");
        if (string.IsNullOrWhiteSpace(cuda) || !File.Exists(cuda))
        {
            cuda = new[] { Path.Combine(folder, "cuda", exeName) }.FirstOrDefault(File.Exists);
        }

        // With only a CUDA build installed it also serves as the CPU worker (--device cpu).
        worker ??= cuda;
        var found = new List<NemotronSetup>();
        if (worker is null || !Directory.Exists(folder))
        {
            return found;
        }

        // Both speech models are offered when both files are installed; only one runs at a time.
        foreach (var asr in new[] { NemotronPins.Multilingual, NemotronPins.EnglishOnly })
        {
            if (NemotronModelFiles.TryFind(folder, asr) is { } files)
            {
                found.Add(new NemotronSetup(worker, cuda, asr, files));
            }
        }

        return found;
    }

    public ValueTask<IReadOnlyList<TranscriptSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken) =>
        this.store.ListAsync(0, 200, cancellationToken);

    public async ValueTask<SessionDocumentHost?> OpenSessionAsync(Guid id, CancellationToken cancellationToken)
    {
        var document = await this.store.LoadAsync(id, cancellationToken).ConfigureAwait(false);
        return document is null ? null : new SessionDocumentHost(document, this.store);
    }

    public async Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
        await this.decoder.ProbeAsync(path, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Spoken language for multilingual models, for example en-US, or "auto" to let the model guess. Auto-detect on
    /// noisy system audio produced words in the wrong script, so the default is en-US
    /// (or PRIMEDICTATE_LANGUAGE). English-only models ignore it.
    /// </summary>
    public string Language { get; set; } = Environment.GetEnvironmentVariable("PRIMEDICTATE_LANGUAGE") is { Length: > 0 } configured ? configured : "en-US";

    private TranscriptionSessionOptions Options(SpeechModelChoice model, string? deviceId, AudioRetention retention) =>
        new(model.ModelId, null, "cpu", model.Language ?? this.Language, null, retention, DownmixMode.Average, null, null, deviceId);

    private async Task<ITranscriptionProvider> ProviderAsync(SpeechModelChoice model, CancellationToken cancellationToken)
    {
        await this.providerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A changed device preference restarts the worker, so drop its cached providers first.
            if (this.nemotronWorker is not null && this.nemotronWorkerPreference != this.nemotronPreference)
            {
                await this.nemotronWorker.DisposeAsync().ConfigureAwait(false);
                this.nemotronWorker = null;
                foreach (var stale in this.providers.Keys.Where(k => k.StartsWith("nemotron:", StringComparison.Ordinal)).ToList())
                {
                    await this.providers[stale].DisposeAsync().ConfigureAwait(false);
                    this.providers.Remove(stale);
                }
            }

            if (model.ModelId == RecordOnlyProvider.Id)
            {
                return new RecordOnlyProvider();
            }

            if (this.providers.TryGetValue(model.ModelId, out var existing))
            {
                return existing;
            }

            ITranscriptionProvider provider;
            if (this.whisperModels.TryGetValue(model.ModelId, out var whisper))
            {
                provider = new SherpaWhisperProvider(whisper);
            }
            else if (this.onnxModels.TryGetValue(model.ModelId, out var onnx))
            {
                provider = onnx.Backend == LegacyBackend.Parakeet ? new SherpaParakeetProvider(onnx) : new SherpaMoonshineProvider(onnx);
            }
            else if (this.nemotron.FirstOrDefault(n => $"nemotron:{n.Asr.Id}" == model.ModelId) is { } setup)
            {
                // One worker at a time: it holds a multi-hundred-MB model in memory. Providers of the old one die with it.
                if (this.nemotronWorker is not null && this.nemotronWorkerKey != model.ModelId)
                {
                    await this.nemotronWorker.DisposeAsync().ConfigureAwait(false);
                    this.nemotronWorker = null;
                    foreach (var stale in this.providers.Keys.Where(k => k.StartsWith("nemotron:", StringComparison.Ordinal)).ToList())
                    {
                        this.providers.Remove(stale);
                    }
                }

                if (this.nemotronWorker is null)
                {
                    this.nemotronWorker = await this.StartNemotronWorkerAsync(setup, cancellationToken).ConfigureAwait(false);
                    await NemotronRealtimeSession.WarmUpAsync(this.nemotronWorker.BaseAddress, this.nemotronWorker.ApiKey, this.nemotronWorker.HasDiarizer, cancellationToken).ConfigureAwait(false);
                }

                this.nemotronWorkerPreference = this.nemotronPreference;
                this.nemotronWorkerKey = model.ModelId;
                provider = new NemotronProvider(this.nemotronWorker, model.ModelId, model.DetectsSpeakers);
            }
            else
            {
                throw new InvalidOperationException($"Model {model.ModelId} is not installed.");
            }

            this.providers[model.ModelId] = provider;
            return provider;
        }
        finally
        {
            this.providerGate.Release();
        }
    }

    /// <summary>Imports a file, creates its session, and transcribes it. Returns the host as soon as the session exists.</summary>
    public async Task<(SessionDocumentHost Host, Task Completion)> ImportAsync(
        string path,
        SpeechModelChoice model,
        AudioRetention retention,
        IProgress<ProgressChanged>? progress,
        CancellationToken cancellationToken)
    {
        var probe = await this.decoder.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        var stream = probe.AudioStreams.FirstOrDefault()
            ?? throw new MediaDecodeException("no-audio", "This file has no audio track.");
        var options = Options(model, null, retention);
        // The media directory depends on the session id, which exists only after creation; the runner
        // receives it in the request used for the run, so create the session first.
        var probeRequest = new FileJobRequest(path, Path.GetFileNameWithoutExtension(path), options, stream.Index, true, string.Empty);
        var host = await this.runner.CreateSessionAsync(probeRequest, this.decoder, cancellationToken).ConfigureAwait(false);
        var request = probeRequest with { SessionMediaDirectory = this.store.GetSessionMediaDirectory(host.Document.SessionId) };
        var completion = this.RunAsync(host, request, model, progress, cancellationToken);
        return (host, completion);
    }

    /// <summary>Reruns recognition on an existing session with a chosen model. Earlier results and edits stay.</summary>
    public Task RerunAsync(SessionDocumentHost host, SpeechModelChoice model, IProgress<ProgressChanged>? progress, CancellationToken cancellationToken)
    {
        var doc = host.Document;
        var source = doc.Audio.FirstOrDefault(a => a.Kind == AudioReferenceKind.ExternalReference) ?? doc.Audio.FirstOrDefault()
            ?? throw new InvalidOperationException("This session has no audio to rerun. Its audio was deleted.");
        var request = new FileJobRequest(source.Path, doc.Title, Options(model, null, AudioRetention.KeepAudio), doc.Media?.SelectedTrack ?? 0, false, this.store.GetSessionMediaDirectory(doc.SessionId));
        return this.RunAsync(host, request, model, progress, cancellationToken);
    }

    private Task RunAsync(SessionDocumentHost host, FileJobRequest request, SpeechModelChoice model, IProgress<ProgressChanged>? progress, CancellationToken cancellationToken) =>
        Task.Run(
            async () =>
            {
                var provider = await this.ProviderAsync(model, cancellationToken).ConfigureAwait(false);
                NoteFallback(host, provider);
                await this.runner.RunAsync(host, request, this.decoder, provider, progress, cancellationToken).ConfigureAwait(false);
            },
            CancellationToken.None);

    /// <summary>A session that ran on a different backend than requested says so, next to its transcript.</summary>
    private static void NoteFallback(SessionDocumentHost host, ITranscriptionProvider provider)
    {
        if (provider.Runtime.FallbackReason is { } reason)
        {
            host.AddNote($"Requested {provider.Runtime.RequestedBackend}, ran on {provider.Runtime.EffectiveBackend}: {reason}");
        }
    }

    /// <param name="deviceId">Microphone device; for <see cref="TranscriptSourceType.SystemAudio"/> it is the output device (null is the default output).</param>
    /// <param name="autoGain">Lift quiet system audio before recognition (never changes the saved recording).</param>
    /// <param name="systemDeviceId">Output device for <see cref="TranscriptSourceType.Meeting"/>; null is the default output.</param>
    public async Task<LiveTranscriptionSession> StartLiveAsync(SpeechModelChoice model, string? deviceId, AudioRetention retention, string title, CancellationToken cancellationToken, TranscriptSourceType source = TranscriptSourceType.Microphone, string? systemDeviceId = null, bool autoGain = true)
    {
        var captureSource = this.SourceFor(source, systemDeviceId);

        if (this.live is not null)
        {
            throw new InvalidOperationException("A recording is already in progress.");
        }

        var session = new LiveTranscriptionSession(
            captureSource,
            await this.ProviderAsync(model, cancellationToken).ConfigureAwait(false),
            this.microphone,
            this.scheduler,
            this.store,
            new LiveSessionOptions(Options(model, deviceId, retention), title, this.store.GetSessionMediaDirectory, LiveSessionOptions.DefaultPreviewInterval, null, source, autoGain));
        await session.StartAsync(cancellationToken).ConfigureAwait(false);
        if (session.Host is { } liveHost)
        {
            NoteFallback(liveHost, await this.ProviderAsync(model, cancellationToken).ConfigureAwait(false));
        }

        this.live = session;
        return session;
    }

    private IAudioSource SourceFor(TranscriptSourceType mode, string? systemDeviceId)
    {
        var microphone = mode != TranscriptSourceType.SystemAudio
            ? this.audioSource ?? throw new AudioSourceException(AudioSourceErrorKind.Unknown, this.MicrophoneUnavailableReason ?? "No microphone is available.")
            : null;
        var system = mode != TranscriptSourceType.Microphone
            ? this.systemAudioSource ?? throw new AudioSourceException(AudioSourceErrorKind.NotSupported, this.SystemAudioUnavailableReason ?? "System audio capture is not available.")
            : null;
        return mode switch
        {
            TranscriptSourceType.Microphone => microphone!,
            TranscriptSourceType.SystemAudio => system!,
            _ => new CombinedAudioSource(microphone!, system!) { SystemDeviceId = systemDeviceId }
        };
    }

    public ValueTask<IReadOnlyList<AudioInputDevice>> ListSystemAudioDevicesAsync(CancellationToken cancellationToken) =>
        this.systemAudioSource is null ? ValueTask.FromResult<IReadOnlyList<AudioInputDevice>>([]) : this.systemAudioSource.ListDevicesAsync(cancellationToken);

    public async Task StopLiveAsync(CancellationToken cancellationToken)
    {
        var session = Interlocked.Exchange(ref this.live, null);
        if (session is not null)
        {
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The overlap-aware speaker timeline saved for a meeting, if one was computed.</summary>
    public DiarizationOverlay? LoadOverlay(Guid sessionId) =>
        DiarizationOverlay.TryLoad(this.store.GetSessionMediaDirectory(sessionId));

    /// <summary>
    /// After a meeting, runs the whole-file diarizer over the system channel so the timeline can show people talking
    /// over each other. Returns null with a reason when it was not possible; the live view stays as it was.
    /// </summary>
    public async Task<(DiarizationOverlay? Overlay, string? Reason)> BuildOverlayAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var directory = this.store.GetSessionMediaDirectory(sessionId);
        var stereo = Path.Combine(directory, "recording-16k-stereo.wav");
        if (!File.Exists(stereo))
        {
            return (null, "This session has no stereo recording.");
        }

        var setup = this.nemotron.FirstOrDefault(n => n.Files.DiarizerPath is not null);
        if (setup is null)
        {
            return (null, "The Nemotron diarizer file is not installed.");
        }

        var (overlay, error) = await this.RunDiarizerAsync(setup, stereo, directory, cancellationToken).ConfigureAwait(false);
        overlay?.Save(directory);
        return (overlay, error);
    }

    /// <summary>True when Nemotron and its diarizer are installed, so a finished meeting can get a final pass with speaker labels.</summary>
    public bool FinalPassAvailable => this.nemotron.Any(n => n.Files.DiarizerPath is not null);

    /// <summary>
    /// The second pass of a two-pass meeting: reads the saved stereo recording with Nemotron and the whole-recording
    /// diarizer and replaces the live draft with the final transcript, speaker labels and overlap. The notes say what
    /// happened and are saved. A failure leaves the draft as it was.
    /// </summary>
    public async Task<FinalPassResult?> RunFinalPassAsync(SessionDocumentHost host, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        try
        {
            return await this.RunFinalPassCoreAsync(host, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Whatever happened, the pass has had its turn; only an app exit before it ran leaves the marker for the next launch.
            this.ClearFinalPassPending(host.Document.SessionId);
        }
    }

    /// <summary>Remembers that this meeting still needs its after-Stop pass, so an exit before it runs does not leave a draft forever.</summary>
    public void MarkFinalPassPending(Guid sessionId) => FinalPassPending.Mark(this.store.GetSessionMediaDirectory(sessionId));

    public void ClearFinalPassPending(Guid sessionId)
    {
        try
        {
            FinalPassPending.Clear(this.store.GetSessionMediaDirectory(sessionId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Completed meetings whose after-Stop pass never ran, newest first.</summary>
    public async Task<IReadOnlyList<Guid>> FinalPassPendingSessionsAsync(CancellationToken cancellationToken)
    {
        var sessions = await this.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        return sessions
            .Where(x => x.SourceType == TranscriptSourceType.Meeting && x.Status == TranscriptSessionStatus.Completed && FinalPassPending.IsPending(this.store.GetSessionMediaDirectory(x.SessionId)))
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.SessionId)
            .ToList();
    }

    private async Task<FinalPassResult?> RunFinalPassCoreAsync(SessionDocumentHost host, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        async Task NoteAsync(string text)
        {
            host.AddNote(text);
            await host.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var document = host.Document;
        var directory = this.store.GetSessionMediaDirectory(document.SessionId);
        var stereo = Path.Combine(directory, "recording-16k-stereo.wav");
        var choice = this.AvailableModels().FirstOrDefault(m => m.ModelId.StartsWith("nemotron:", StringComparison.Ordinal) && m.DetectsSpeakers);
        var setup = choice is null ? null : this.nemotron.FirstOrDefault(n => $"nemotron:{n.Asr.Id}" == choice.ModelId);
        if (document.SourceType != TranscriptSourceType.Meeting || document.Status != TranscriptSessionStatus.Completed || !File.Exists(stereo) || choice is null || setup is null)
        {
            await NoteAsync("Final pass with speaker labels was not run: it needs a finished meeting with a stereo recording and Nemotron with its diarizer installed.").ConfigureAwait(false);
            return null;
        }

        if (document.Speakers.Any(s => s.Id is not ("local" or "remote")))
        {
            await NoteAsync("Final pass with speaker labels was not run: this meeting already has speaker labels from a live diarized pass.").ConfigureAwait(false);
            return null;
        }

        try
        {
            await NoteAsync($"Improving the transcript with speaker labels after Stop (running). The live text stays as the draft until it is done.").ConfigureAwait(false);
            await this.ProviderAsync(choice, cancellationToken).ConfigureAwait(false); // starts the worker if it is not running
            var worker = this.nemotronWorker ?? throw new InvalidOperationException("The Nemotron worker is not running.");
            await using var asr = new NemotronProvider(worker, choice.ModelId, false);
            var result = await new MeetingFinalPass(this.scheduler).RunAsync(
                host,
                stereo,
                asr,
                async ct => await this.RunDiarizerAsync(setup, stereo, directory, ct).ConfigureAwait(false),
                choice.Language ?? this.Language,
                directory,
                progress,
                cancellationToken).ConfigureAwait(false);
            await NoteAsync($"Final pass ({asr.Runtime.EffectiveBackend}): {result.MicrophoneLines} lines from the microphone, {result.SystemLines} from the system audio, {result.SpeakerCount} system speakers ({result.Overlay?.Mode ?? "no"} diarization), {result.OverlapSeconds:0.#} s of overlapping speech. These rows replace the live draft, which is kept as the earlier result. Channels: {result.ChannelReport}."
                + (result.DiarizerProblem is { } problem ? $" Speakers could not be told apart: {problem}" : result.SystemLines == 0 ? " " + result.Describe() : string.Empty)).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            await NoteAsync("Final pass canceled, so the live draft is kept.").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await NoteAsync($"Final pass failed, so the live draft is kept: {ex.Message}").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Diarizes the system channel. Accuracy first: recordings short enough use the offline (full attention) mode, longer
    /// ones the streaming mode. It uses the same device choice as the live worker and falls back to the CPU worker, and
    /// to streaming mode if the offline run fails.
    /// </summary>
    private async Task<(DiarizationOverlay? Overlay, string? Error)> RunDiarizerAsync(NemotronSetup setup, string stereo, string directory, CancellationToken cancellationToken)
    {
        var attempts = new List<(string Worker, string Device)>();
        if (setup.CudaWorkerPath is not null && this.nemotronPreference != "cpu")
        {
            attempts.Add((setup.CudaWorkerPath, this.nemotronPreference.StartsWith("cuda", StringComparison.Ordinal) ? this.nemotronPreference : "cuda:0"));
        }

        attempts.Add((setup.WorkerPath, "cpu"));
        var length = (await this.decoder.ProbeAsync(stereo, cancellationToken).ConfigureAwait(false)).Duration ?? TimeSpan.MaxValue;
        var modes = length <= NemotronDiarizer.OfflineLimit ? new[] { true, false } : new[] { false };
        DiarizationOverlay? overlay = null;
        string? error = null;
        foreach (var (worker, device) in attempts)
        {
            foreach (var offline in modes)
            {
                (overlay, error) = await NemotronDiarizer.RunAsync(
                    worker, setup.Files.DiarizerPath!, device, stereo, offline, directory, TimeSpan.FromMinutes(10), this.CudaRuntimeDirectories(worker), cancellationToken).ConfigureAwait(false);
                if (overlay is not null)
                {
                    return (overlay, null);
                }
            }
        }

        return (overlay, error);
    }

    /// <summary>
    /// Runs the whole-file diarizer for a finished meeting and always says what happened in the session's notes, saved to
    /// the database: that it started, and then that it worked or why it did not. The notes are saved here because nothing
    /// else writes the session again after Stop.
    /// </summary>
    public async Task<DiarizationOverlay?> RefineSpeakerTimelineAsync(SessionDocumentHost host, CancellationToken cancellationToken)
    {
        async Task NoteAsync(string text)
        {
            host.AddNote(text);
            await host.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var document = host.Document;
        if (document.SourceType != TranscriptSourceType.Meeting)
        {
            return null;
        }

        if (document.Status != TranscriptSessionStatus.Completed)
        {
            await NoteAsync($"Speaker timeline was not redrawn: the session ended as {document.Status}, not Completed.").ConfigureAwait(false);
            return null;
        }

        try
        {
            await NoteAsync("Redrawing the speaker timeline from the diarizer (running)…").ConfigureAwait(false);
            var (overlay, reason) = await this.BuildOverlayAsync(document.SessionId, cancellationToken).ConfigureAwait(false);
            if (overlay is null)
            {
                await NoteAsync($"Speaker timeline was not redrawn: {reason}").ConfigureAwait(false);
                return null;
            }

            await NoteAsync($"Speaker timeline redrawn from the diarizer: {overlay.SpeakerCount} system speakers, {overlay.OverlapSeconds:0.#} s of overlapping speech.").ConfigureAwait(false);
            return overlay;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await NoteAsync($"Speaker timeline was not redrawn: {ex.Message}").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// For app exit: stops an active recording the normal way so the audio and transcript are finalized and saved. If that does not
    /// finish within <paramref name="timeout"/> (a long final pass), the session is stopped as canceled instead; what was captured stays on disk.
    /// </summary>
    public async Task StopLiveForExitAsync(TimeSpan timeout)
    {
        if (this.live is null)
        {
            return;
        }

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await this.StopLiveAsync(cts.Token).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            this.RaiseNotice("The recording did not finish saving in time and was stopped as canceled; its audio and text so far are kept.");
            await this.DiscardLiveAsync().ConfigureAwait(false);
        }
    }

    public async Task DiscardLiveAsync()
    {
        var session = Interlocked.Exchange(ref this.live, null);
        if (session is not null)
        {
            await session.DiscardAsync().ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask<IReadOnlyList<AudioInputDevice>> ListMicrophonesAsync(CancellationToken cancellationToken) =>
        this.audioSource is null ? ValueTask.FromResult<IReadOnlyList<AudioInputDevice>>([]) : this.audioSource.ListDevicesAsync(cancellationToken);

    /// <summary>True while a live recording owns this session's files.</summary>
    public bool IsRecording(Guid id) => this.live?.Host?.Document.SessionId == id;

    public async ValueTask<SessionDeletionResult> DeleteSessionAsync(Guid id, CancellationToken cancellationToken)
    {
        // Deleting a session that is still recording stops it first so the audio writer releases its file.
        if (this.IsRecording(id))
        {
            await this.DiscardLiveAsync().ConfigureAwait(false);
        }

        return await this.store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public static async Task ExportAsync(TranscriptDocument document, string path, ExportOptions options, CancellationToken cancellationToken)
    {
        var text = TranscriptExporter.Export(document, options);
        await File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await this.DiscardLiveAsync().ConfigureAwait(false);
        // Stop the worker first: it holds the GPU, and the providers' sockets then close quickly instead of waiting on it.
        if (this.nemotronWorker is not null)
        {
            await this.nemotronWorker.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var provider in this.providers.Values)
        {
            try
            {
                await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }

        await this.store.DisposeAsync().ConfigureAwait(false);
        if (this.audioSource is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
