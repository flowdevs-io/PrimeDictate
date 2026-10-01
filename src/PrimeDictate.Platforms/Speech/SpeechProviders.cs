using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Providers;
using PrimeDictate.Platforms.Speech.Qualcomm;

namespace PrimeDictate.Platforms.Speech;

/// <summary>Creates the provider for an installed model. One place, so dictation and the transcription workspace choose the same engine for the same model.</summary>
public static class SpeechProviders
{
    public static ITranscriptionProvider Create(InstalledSpeechModel model) => model.Backend switch
    {
        LegacyBackend.WhisperNet => new WhisperNetProvider(model),
        LegacyBackend.Parakeet => new SherpaParakeetProvider(model),
        LegacyBackend.Moonshine => new SherpaMoonshineProvider(model),
        // As the WPF engine chose: the AI Hub package by its catalog id, otherwise Moonshine on the NPU.
        LegacyBackend.QualcommQnn => SpeechModelLocator.IsQualcommAihubWhisper(model)
            ? new QualcommAihubWhisperProvider(model)
            : new MoonshineQnnProvider(model),
        _ => new SherpaWhisperProvider(WhisperOnnxModelLocator.TryResolve(model.Directory, out var whisper)
            ? whisper
            : throw new FileNotFoundException($"The Whisper model folder is incomplete: {model.Directory}"))
    };
}
