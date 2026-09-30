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

public sealed class RecordOnlyTests
{
    [Fact]
    public async Task Record_only_provider_hears_nothing()
    {
        await using var provider = new PrimeDictate.Platforms.Speech.RecordOnlyProvider();
        Assert.Empty(await provider.RecognizeWindowAsync(new float[16_000], "en", CancellationToken.None));
        Assert.Equal("record-only", TranscriptionWorkspaceService.RecordOnly.ModelId);
    }

    [Fact]
    public void Zero_system_speakers_says_why()
    {
        var silent = new PrimeDictate.Platforms.Nemotron.FinalPassResult(2, 3, 0, 0, 0, null, null);
        Assert.Contains("Nothing was recognized on the system audio", silent.Describe());
        var failed = silent with { DiarizerProblem = "exited with code 1" };
        Assert.Contains("could not be told apart: exited with code 1", failed.Describe());
        var heard = silent with { Overlay = new PrimeDictate.Core.Transcripts.DiarizationOverlay([new("speaker_1", 0, 2), new("speaker_2", 2, 4)]) };
        Assert.Contains("diarizer heard 2 speakers", heard.Describe());
        var normal = silent with { SystemLines = 4, SpeakerCount = 2, OverlapSeconds = 1.5 };
        Assert.Contains("2 speakers on the system audio, 1.5 s", normal.Describe());
    }
}
