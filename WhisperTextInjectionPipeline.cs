using SharpHook;
using SharpHook.Data;

namespace PrimeDictate;

/// <summary>
/// Transcribes through the selected engine, then updates the focused control via final-only Unicode input.
/// Target injection is intentionally final-only: partial hypotheses are not typed into editors because repeated
/// correction loops fight autocomplete, caret movement, and slow input targets.
/// </summary>
internal sealed class WhisperTextInjectionPipeline
{
    private readonly TranscriptionEngineHost transcriptionEngines = new();
    private readonly TranscriptionEngineHost wakeTranscriptionEngines = new();
    private readonly EventSimulator eventSimulator = new();
    private readonly object wakeSync = new();
    private TranscriptionEngineConfiguration dictationConfiguration = new(
        TranscriptionBackendKind.Whisper,
        TranscriptionComputeInterface.Cpu,
        SelectedModelId: null,
        ConfiguredModelPath: null);
    private TranscriptionEngineConfiguration? wakeConfiguration;
    private bool wakeReady;
    private bool wakeUsesDedicatedSmallModel;

    public string ConfigurationSummary => this.transcriptionEngines.ConfigurationSummary;

    public void UpdateConfiguration(
        TranscriptionBackendKind transcriptionBackend,
        TranscriptionComputeInterface transcriptionComputeInterface,
        string? selectedModelId,
        string? configuredModelPath)
    {
        this.dictationConfiguration = new TranscriptionEngineConfiguration(
            transcriptionBackend,
            transcriptionComputeInterface,
            string.IsNullOrWhiteSpace(selectedModelId) ? null : selectedModelId.Trim(),
            string.IsNullOrWhiteSpace(configuredModelPath) ? null : configuredModelPath.Trim());
        this.transcriptionEngines.UpdateConfiguration(
            transcriptionBackend,
            transcriptionComputeInterface,
            selectedModelId,
            configuredModelPath);
    }

    /// <summary>
    /// Configures wake STT: dedicated small CPU model when available, else the selected dictation model
    /// on the shared dictation host (avoids loading Large twice).
    /// </summary>
    public bool TryPrepareWakeTranscription(out string? errorMessage)
    {
        if (!WakeWordModelResolver.TryResolve(
                this.dictationConfiguration,
                out var resolved,
                out var usesDedicatedSmallModel,
                out errorMessage))
        {
            lock (this.wakeSync)
            {
                this.wakeReady = false;
                this.wakeConfiguration = null;
                this.wakeUsesDedicatedSmallModel = false;
            }

            return false;
        }

        lock (this.wakeSync)
        {
            if (this.wakeReady &&
                this.wakeUsesDedicatedSmallModel == usesDedicatedSmallModel &&
                this.wakeConfiguration is { } current &&
                current == resolved)
            {
                errorMessage = null;
                return true;
            }

            this.wakeUsesDedicatedSmallModel = usesDedicatedSmallModel;
            this.wakeConfiguration = resolved;
            this.wakeReady = true;

            if (usesDedicatedSmallModel)
            {
                this.wakeTranscriptionEngines.UpdateConfiguration(
                    resolved.Backend,
                    resolved.ComputeInterface,
                    resolved.SelectedModelId,
                    resolved.ConfiguredModelPath);
            }
        }

        if (usesDedicatedSmallModel)
        {
            AppLog.Info(
                $"Wake transcription ready: {this.wakeTranscriptionEngines.ConfigurationSummary} (dedicated small model, CPU).");
        }
        else
        {
            AppLog.Info(
                $"Wake transcription ready using selected dictation model on short idle windows " +
                $"({this.transcriptionEngines.ConfigurationSummary}). Install Tiny/Base for lighter wake listening.");
        }

        errorMessage = null;
        return true;
    }

    /// <summary>
    /// Full-buffer transcription with no target mutation.
    /// </summary>
    public async ValueTask<string> TranscribeAsync(
        PcmAudioBuffer audio,
        CancellationToken cancellationToken = default,
        bool logTranscript = true)
    {
        if (audio.IsEmpty)
        {
            return string.Empty;
        }

        var backend = this.transcriptionEngines.ConfiguredBackendName;
        if (logTranscript)
        {
            AppLog.Info(
                $"Transcription request: {this.ConfigurationSummary}; audio={audio.Duration.TotalSeconds:0.00}s, bytes={audio.Pcm16KhzMono.Length:N0}.");
        }

        var text = await this.transcriptionEngines.TranscribeAsync(audio, cancellationToken).ConfigureAwait(false);
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            if (logTranscript)
            {
                AppLog.Info($"{backend} returned no text for this audio buffer.");
            }

            return string.Empty;
        }

        if (logTranscript)
        {
            AppLog.Info($"Transcribed ({backend}, {text.Length:N0} chars): {text}");
        }

        return text;
    }

    /// <summary>
    /// Idle wake-phrase transcription (dedicated small host, or shared dictation host as fallback).
    /// </summary>
    public async ValueTask<string> TranscribeWakeAsync(
        PcmAudioBuffer audio,
        CancellationToken cancellationToken = default)
    {
        if (audio.IsEmpty)
        {
            return string.Empty;
        }

        bool useDedicated;
        lock (this.wakeSync)
        {
            if (!this.wakeReady)
            {
                throw new InvalidOperationException(
                    "Wake transcription is not ready. Select a model in Settings → Model.");
            }

            useDedicated = this.wakeUsesDedicatedSmallModel;
        }

        var host = useDedicated ? this.wakeTranscriptionEngines : this.transcriptionEngines;
        var text = await host.TranscribeAsync(audio, cancellationToken).ConfigureAwait(false);
        return text.Trim();
    }

    public void InjectTextToTarget(string text)
    {
        var target = text.Trim();
        if (target.Length == 0)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            WindowsUnicodeInput.SendText(target);
            return;
        }

        var textResult = this.eventSimulator.SimulateTextEntry(target);
        if (textResult != UioHookResult.Success)
        {
            throw new InvalidOperationException($"Text injection failed with status {textResult}.");
        }
    }

    public void SendEnterToTarget()
    {
        var keyResult = this.eventSimulator.SimulateKeyStroke(new[] { KeyCode.VcEnter });
        if (keyResult != UioHookResult.Success)
        {
            throw new InvalidOperationException($"Enter key simulation failed with status {keyResult}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.wakeTranscriptionEngines.DisposeAsync().ConfigureAwait(false);
        await this.transcriptionEngines.DisposeAsync().ConfigureAwait(false);
    }
}
