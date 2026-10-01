using PrimeDictate.Core.Dictation;
using PrimeDictate.Platforms.Dictation;
using PrimeDictate.Platforms.Speech;

namespace PrimeDictate.Core.Tests;

public sealed class DictationHostKeyTests
{
    [Fact]
    public void Same_model_id_in_another_folder_is_a_different_provider()
    {
        var a = new InstalledSpeechModel(PrimeDictate.Core.Dictation.LegacyBackend.Whisper, "base.en", "Base", @"C:\a", true);
        var b = a with { Directory = @"C:\b" };
        Assert.Equal(a.ModelId, b.ModelId);
        Assert.NotEqual(a.ProviderKey, b.ProviderKey);
    }

    [Fact]
    public void Wake_key_changes_with_the_microphone_and_the_model_but_not_the_phrase()
    {
        var settings = new DictationSettings();
        var before = DictationHost.WakeKey(settings);
        settings.WakeWordPhrase = "hey there";
        Assert.Equal(before, DictationHost.WakeKey(settings));
        settings.SelectedInputDeviceId = "other-mic";
        Assert.NotEqual(before, DictationHost.WakeKey(settings));
        var withMic = DictationHost.WakeKey(settings);
        settings.ModelPath = @"C:\models\custom";
        Assert.NotEqual(withMic, DictationHost.WakeKey(settings));
    }
}
