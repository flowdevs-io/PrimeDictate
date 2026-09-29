using PrimeDictate.Core.Settings;

namespace PrimeDictate;

/// <summary>
/// First-run transcription defaults copied from the dictation settings. Dictation's
/// <c>settings.json</c> is only read here, never rewritten.
/// </summary>
internal static class TranscriptionPreferencesSeed
{
    public static TranscriptionPreferences FromAppSettings(AppSettings settings) => new()
    {
        AsrModelId = LegacyModelIds.From(settings.TranscriptionBackend, settings.SelectedModelId),
        RequestedBackend = settings.TranscriptionComputeInterface switch
        {
            TranscriptionComputeInterface.Gpu => "gpu",
            TranscriptionComputeInterface.Npu => "npu",
            _ => "cpu"
        },
        InputDeviceId = settings.SelectedInputDeviceId
    };
}

/// <summary>Stable model IDs for the existing backends, used until the model registry owns them.</summary>
internal static class LegacyModelIds
{
    public static string From(TranscriptionBackendKind backend, string? selectedModelId)
    {
        var family = backend switch
        {
            TranscriptionBackendKind.Parakeet => "parakeet-onnx",
            TranscriptionBackendKind.Moonshine => "moonshine",
            TranscriptionBackendKind.WhisperNet => "whisper-net",
            TranscriptionBackendKind.QualcommQnn => "qualcomm-qnn",
            _ => "whisper-onnx"
        };
        return string.IsNullOrWhiteSpace(selectedModelId) ? $"{family}:auto" : $"{family}:{selectedModelId.Trim()}";
    }
}
