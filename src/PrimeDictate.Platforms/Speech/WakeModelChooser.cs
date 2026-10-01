using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Platforms.Speech;

/// <summary>
/// Picks the model that listens for the wake phrase on the idle microphone, the way the WPF app did: a small model of the same family
/// as the dictation model when one is installed (so Whisper.net users do not load an ONNX runtime just for the wake word, and Moonshine
/// users stay on Moonshine), then a small Whisper ONNX model, and otherwise the dictation model itself.
/// </summary>
public static class WakeModelChooser
{
    private static readonly string[] WhisperOnnxIds = ["tiny.en", "base.en", "tiny", "base", "distil-small.en"];
    private static readonly string[] WhisperNetIds = ["tiny.en", "base.en"];
    private static readonly string[] MoonshineIds = ["moonshine-tiny-v2-en", "moonshine-base-en"];

    /// <summary>
    /// The small wake model for <paramref name="dictationBackend"/>, or null to use the dictation model. When the family has no small
    /// model installed (Parakeet never has one), an installed small Whisper ONNX model is still lighter than the dictation model, so it is used.
    /// </summary>
    public static InstalledSpeechModel? ChooseSmall(IReadOnlyList<InstalledSpeechModel> installed, LegacyBackend dictationBackend)
    {
        var sameFamily = dictationBackend switch
        {
            LegacyBackend.WhisperNet => FirstOf(installed, LegacyBackend.WhisperNet, WhisperNetIds),
            LegacyBackend.Moonshine => FirstOf(installed, LegacyBackend.Moonshine, MoonshineIds),
            _ => null
        };
        return sameFamily ?? SmallestWhisperOnnx(installed);
    }

    private static InstalledSpeechModel? FirstOf(IReadOnlyList<InstalledSpeechModel> installed, LegacyBackend backend, string[] ids) =>
        ids.Select(id => installed.FirstOrDefault(m => m.Backend == backend && string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(m => m is not null);

    /// <summary>Of the installed small Whisper ONNX models, the smallest download is the lightest to keep loaded.</summary>
    private static InstalledSpeechModel? SmallestWhisperOnnx(IReadOnlyList<InstalledSpeechModel> installed) =>
        installed
            .Where(m => m.Backend == LegacyBackend.Whisper && WhisperOnnxIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase))
            .OrderBy(SizeOf)
            .FirstOrDefault();

    private static long SizeOf(InstalledSpeechModel model) =>
        SpeechModelCatalog.Options.FirstOrDefault(o => o.Backend == model.Backend && o.Id == model.Id)?.ApproximateBytes ?? long.MaxValue;
}
