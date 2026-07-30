using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace PrimeDictate;

/// <summary>
/// Picks a transcription config for idle wake-phrase STT. Prefers a small Tiny/Base model when
/// installed; otherwise falls back to the user's selected dictation model so wake still runs.
/// </summary>
internal static class WakeWordModelResolver
{
    private static readonly HashSet<string> WhisperOnnxWakeModelIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "tiny.en",
        "base.en",
        "tiny",
        "base",
        "distil-small.en"
    };

    private static readonly string[] WhisperNetWakeModelIds = ["tiny.en", "base.en"];

    private static readonly string[] MoonshineWakeModelIds = ["moonshine-tiny-v2-en", "moonshine-base-en"];

    public static bool TryResolve(
        TranscriptionEngineConfiguration dictationConfiguration,
        out TranscriptionEngineConfiguration configuration,
        out bool usesDedicatedSmallModel,
        [NotNullWhen(false)] out string? errorMessage)
    {
        configuration = default;
        usesDedicatedSmallModel = false;
        errorMessage = null;

        if (TryResolveSmallModel(dictationConfiguration.Backend, out configuration))
        {
            usesDedicatedSmallModel = true;
            return true;
        }

        if (TryResolveDictationFallback(dictationConfiguration, out configuration))
        {
            usesDedicatedSmallModel = false;
            return true;
        }

        errorMessage =
            "Wake listening could not find a usable model. Download a model in Settings → Model " +
            "(Tiny/Base English is lightest for idle wake listening).";
        return false;
    }

    private static bool TryResolveSmallModel(
        TranscriptionBackendKind backend,
        out TranscriptionEngineConfiguration configuration)
    {
        configuration = default;
        return backend switch
        {
            TranscriptionBackendKind.WhisperNet => TryResolveWhisperNetSmall(out configuration),
            TranscriptionBackendKind.Whisper => TryResolveWhisperOnnxSmall(out configuration),
            TranscriptionBackendKind.Moonshine => TryResolveMoonshineSmall(out configuration),
            _ => false
        };
    }

    private static bool TryResolveDictationFallback(
        TranscriptionEngineConfiguration dictationConfiguration,
        out TranscriptionEngineConfiguration configuration)
    {
        configuration = default;
        if (!TryResolveDictationModelPath(dictationConfiguration, out var path, out var modelId))
        {
            return false;
        }

        // Mark as CPU in the wake plan; shared-host fallback still uses the live dictation host config.
        configuration = new TranscriptionEngineConfiguration(
            dictationConfiguration.Backend,
            TranscriptionComputeInterface.Cpu,
            modelId,
            path);
        return true;
    }

    private static bool TryResolveDictationModelPath(
        TranscriptionEngineConfiguration dictationConfiguration,
        [NotNullWhen(true)] out string? path,
        out string? modelId)
    {
        modelId = dictationConfiguration.SelectedModelId;
        var configuredPath = dictationConfiguration.ConfiguredModelPath?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredPath) &&
            (File.Exists(configuredPath) || Directory.Exists(configuredPath)))
        {
            path = configuredPath;
            return true;
        }

        path = null;
        return dictationConfiguration.Backend switch
        {
            TranscriptionBackendKind.WhisperNet =>
                WhisperNetModelCatalog.TryGetById(modelId, out var netOption) &&
                WhisperNetModelCatalog.TryResolveInstalledPath(netOption, out path),
            TranscriptionBackendKind.Whisper =>
                WhisperModelCatalog.TryGetById(modelId, out var whisperOption) &&
                WhisperModelCatalog.TryResolveInstalledPath(whisperOption, out path),
            TranscriptionBackendKind.Moonshine =>
                MoonshineModelCatalog.TryGetById(modelId, out var moonOption) &&
                MoonshineModelCatalog.TryResolveInstalledPath(moonOption, out path),
            TranscriptionBackendKind.Parakeet =>
                ParakeetModelCatalog.TryGetById(modelId, out var paraOption) &&
                ParakeetModelCatalog.TryResolveInstalledPath(paraOption, out path),
            TranscriptionBackendKind.QualcommQnn =>
                QualcommAihubWhisperModelCatalog.TryGetById(modelId, out var qnnOption) &&
                QualcommAihubWhisperModelCatalog.TryResolveInstalledPath(qnnOption, out path),
            _ => false
        };
    }

    private static bool TryResolveWhisperNetSmall(out TranscriptionEngineConfiguration configuration)
    {
        foreach (var modelId in WhisperNetWakeModelIds)
        {
            if (!WhisperNetModelCatalog.TryGetById(modelId, out var option))
            {
                continue;
            }

            if (!WhisperNetModelCatalog.TryResolveInstalledPath(option, out var path))
            {
                continue;
            }

            configuration = new TranscriptionEngineConfiguration(
                TranscriptionBackendKind.WhisperNet,
                TranscriptionComputeInterface.Cpu,
                option.Id,
                path);
            return true;
        }

        configuration = default;
        return false;
    }

    private static bool TryResolveWhisperOnnxSmall(out TranscriptionEngineConfiguration configuration)
    {
        WhisperModelOption? best = null;
        string? bestPath = null;
        foreach (var option in WhisperModelCatalog.Options)
        {
            if (!WhisperOnnxWakeModelIds.Contains(option.Id))
            {
                continue;
            }

            if (!WhisperModelCatalog.TryResolveInstalledPath(option, out var path))
            {
                continue;
            }

            if (best is null || option.ApproximateBytes < best.ApproximateBytes)
            {
                best = option;
                bestPath = path;
            }
        }

        if (best is null || bestPath is null)
        {
            configuration = default;
            return false;
        }

        configuration = new TranscriptionEngineConfiguration(
            TranscriptionBackendKind.Whisper,
            TranscriptionComputeInterface.Cpu,
            best.Id,
            bestPath);
        return true;
    }

    private static bool TryResolveMoonshineSmall(out TranscriptionEngineConfiguration configuration)
    {
        foreach (var modelId in MoonshineWakeModelIds)
        {
            if (!MoonshineModelCatalog.TryGetById(modelId, out var option))
            {
                continue;
            }

            if (!MoonshineModelCatalog.TryResolveInstalledPath(option, out var path))
            {
                continue;
            }

            configuration = new TranscriptionEngineConfiguration(
                TranscriptionBackendKind.Moonshine,
                TranscriptionComputeInterface.Cpu,
                option.Id,
                path);
            return true;
        }

        configuration = default;
        return false;
    }
}
