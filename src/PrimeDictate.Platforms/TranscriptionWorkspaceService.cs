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
    private readonly Dictionary<string, ITranscriptionProvider> providers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim providerGate = new(1, 1);
    private NemotronWorker? nemotronWorker;
    private string? nemotronWorkerKey;
    private NemotronSetup? nemotron;
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

    private sealed record NemotronSetup(string WorkerPath, PinnedNemotronModel Asr, NemotronModelFiles Files);

    /// <summary>Everything the user can pick: installed Whisper ONNX models, plus Nemotron when its worker and pinned model files are present.</summary>
    public IReadOnlyList<SpeechModelChoice> AvailableModels()
    {
        this.whisperModels.Clear();
        var choices = new List<SpeechModelChoice>();
        foreach (var model in WhisperOnnxModelLocator.Discover(this.paths.ModelsDirectory))
        {
            this.whisperModels[model.ModelId] = model;
            choices.Add(new SpeechModelChoice(model.ModelId, model.DisplayName, model.IsEnglishOnly ? "en" : null, false, null));
        }

        this.nemotron = FindNemotron();
        if (this.nemotron is { } n)
        {
            var speakers = n.Files.DiarizerPath is not null;
            choices.Add(new SpeechModelChoice(
                $"nemotron:{n.Asr.Id}",
                "Nemotron 3.5 (CPU)" + (speakers ? ", speakers" : string.Empty),
                null,
                speakers,
                speakers ? null : "Speaker detection needs the Nemotron-3-Diarization file next to the speech model."));
        }

        return choices;
    }

    private NemotronSetup? FindNemotron()
    {
        var folder = Path.Combine(this.paths.ModelsDirectory, "nemotron");
        var exeName = OperatingSystem.IsWindows() ? "nemo-speech.exe" : "nemo-speech";
        var worker = Environment.GetEnvironmentVariable("PRIMEDICTATE_NEMO_SPEECH");
        if (string.IsNullOrWhiteSpace(worker) || !File.Exists(worker))
        {
            worker = new[] { Path.Combine(folder, exeName), Path.Combine(AppContext.BaseDirectory, exeName) }.FirstOrDefault(File.Exists);
        }

        if (worker is null || !Directory.Exists(folder))
        {
            return null;
        }

        foreach (var asr in new[] { NemotronPins.Multilingual, NemotronPins.EnglishOnly })
        {
            if (NemotronModelFiles.TryFind(folder, asr) is { } files)
            {
                return new NemotronSetup(worker, asr, files);
            }
        }

        return null;
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

    private static TranscriptionSessionOptions Options(SpeechModelChoice model, string? deviceId, AudioRetention retention) =>
        new(model.ModelId, null, "cpu", model.Language, null, retention, DownmixMode.Average, null, null, deviceId);

    private async Task<ITranscriptionProvider> ProviderAsync(SpeechModelChoice model, CancellationToken cancellationToken)
    {
        await this.providerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.providers.TryGetValue(model.ModelId, out var existing))
            {
                return existing;
            }

            ITranscriptionProvider provider;
            if (this.whisperModels.TryGetValue(model.ModelId, out var whisper))
            {
                provider = new SherpaWhisperProvider(whisper);
            }
            else if (this.nemotron is { } setup && model.ModelId.StartsWith("nemotron:", StringComparison.Ordinal))
            {
                // One worker at a time: it holds a multi-hundred-MB model in memory.
                if (this.nemotronWorker is not null && this.nemotronWorkerKey != model.ModelId)
                {
                    await this.nemotronWorker.DisposeAsync().ConfigureAwait(false);
                    this.nemotronWorker = null;
                }

                this.nemotronWorker ??= await NemotronWorker.StartAsync(setup.WorkerPath, setup.Files, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
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
        Task.Run(async () => await this.runner.RunAsync(host, request, this.decoder, await this.ProviderAsync(model, cancellationToken).ConfigureAwait(false), progress, cancellationToken).ConfigureAwait(false), CancellationToken.None);

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
        foreach (var provider in this.providers.Values)
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }

        if (this.nemotronWorker is not null)
        {
            await this.nemotronWorker.DisposeAsync().ConfigureAwait(false);
        }

        await this.store.DisposeAsync().ConfigureAwait(false);
        if (this.audioSource is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
