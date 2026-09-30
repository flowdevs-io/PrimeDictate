using PrimeDictate.Platforms;
using Xunit;

namespace PrimeDictate.Core.Tests;

public sealed class LiveDraftModelTests
{
    private static SpeechModelChoice C(string id, string? lang = null) => new(id, id, lang, false, null);

    private static readonly SpeechModelChoice Nemotron = C("nemotron:x");

    [Fact]
    public void Nemotron_choice_drafts_with_parakeet_when_installed()
    {
        var all = new[] { C("whisper-onnx:base.en", "en"), C("parakeet-onnx:tdt"), Nemotron };
        Assert.Equal("parakeet-onnx:tdt", TranscriptionWorkspaceService.LiveDraftModel(Nemotron, all)!.ModelId);
    }

    [Fact]
    public void Falls_back_to_english_whisper_then_any_whisper()
    {
        var multi = C("whisper-onnx:small");
        var english = C("whisper-onnx:base.en", "en");
        Assert.Same(english, TranscriptionWorkspaceService.LiveDraftModel(Nemotron, [multi, english, Nemotron]));
        Assert.Same(multi, TranscriptionWorkspaceService.LiveDraftModel(Nemotron, [multi, Nemotron]));
        Assert.Null(TranscriptionWorkspaceService.LiveDraftModel(Nemotron, [Nemotron]));
    }

    [Fact]
    public void Other_choices_are_used_as_they_are()
    {
        var whisper = C("whisper-onnx:small");
        Assert.Same(whisper, TranscriptionWorkspaceService.LiveDraftModel(whisper, [C("parakeet-onnx:tdt"), whisper]));
    }
}
