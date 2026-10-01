using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Settings;
using PrimeDictate.Core.Storage;

namespace PrimeDictate.Core.Tests;

public sealed class ParityAuditTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-audit-" + Guid.NewGuid().ToString("N"));

    public ParityAuditTests() => Directory.CreateDirectory(this.root);

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    [Fact]
    public void One_unknown_enum_value_in_the_wpf_file_does_not_discard_the_import()
    {
        File.WriteAllText(Path.Combine(this.root, "settings.json"), """
            {
              "FirstRunCompleted": true,
              "TranscriptionBackend": "SomethingNew",
              "TranscriptionComputeInterface": "Quantum",
              "OverlayMode": 99,
              "OllamaMode": "Poetry",
              "SelectedModelId": "base.en",
              "InputGainMultiplier": 2.5,
              "VoiceShellCommands": [ { "Phrase": "go", "Command": "x", "CompletionBehavior": "Later", "Enabled": true } ]
            }
            """);
        var load = new DictationSettingsStore(new AppDataPaths(this.root)).Load();

        Assert.True(load.ImportedFromWpf, load.Warning);
        Assert.Null(load.Warning);
        Assert.Equal(LegacyBackend.Whisper, load.Settings.TranscriptionBackend);
        Assert.Null(load.Settings.TranscriptionComputeInterface);
        Assert.Equal(OverlayStyle.CompactMicrophone, load.Settings.OverlayMode);
        Assert.Equal(OllamaMode.Default, load.Settings.OllamaMode);
        Assert.Equal(VoiceShellCommandCompletionBehavior.Stop, Assert.Single(load.Settings.VoiceShellCommands).CompletionBehavior);
        Assert.Equal(2.5, load.Settings.InputGainMultiplier);
    }

    [Fact]
    public void Known_enum_names_numbers_and_the_compute_setting_still_read()
    {
        File.WriteAllText(Path.Combine(this.root, "settings.json"), """
            { "TranscriptionBackend": "Parakeet", "TranscriptionComputeInterface": "Npu", "OverlayMode": "FullPanel", "OllamaMode": 6 }
            """);
        var settings = new DictationSettingsStore(new AppDataPaths(this.root)).Load().Settings;

        Assert.Equal(LegacyBackend.Parakeet, settings.TranscriptionBackend);
        Assert.Equal(LegacyComputeInterface.Npu, settings.TranscriptionComputeInterface);
        Assert.Equal(OverlayStyle.FullPanel, settings.OverlayMode);
        Assert.Equal(OllamaMode.VibeCoding, settings.OllamaMode);
    }

    [Fact]
    public void A_file_that_cannot_be_parsed_never_marks_first_run_as_finished()
    {
        File.WriteAllText(Path.Combine(this.root, "settings.json"), "{ not json");
        var load = new DictationSettingsStore(new AppDataPaths(this.root)).Load();

        Assert.NotNull(load.Warning);
        Assert.False(load.Settings.FirstRunCompleted);
    }

    [Theory]
    [InlineData(true, "http://192.168.1.20:11434", true)]
    [InlineData(true, "https://ollama.example.com", true)]
    [InlineData(true, "http://localhost:11434", false)]
    [InlineData(true, "http://127.0.0.1:11434", false)]
    [InlineData(false, "http://192.168.1.20:11434", false)]
    public void A_remote_ollama_endpoint_from_the_wpf_app_keeps_working_after_the_import(bool enabled, string endpoint, bool expectedAllowed)
    {
        File.WriteAllText(
            Path.Combine(this.root, "settings.json"),
            $$"""{ "EnableOllamaPostProcessing": {{enabled.ToString().ToLowerInvariant()}}, "OllamaEndpoint": "{{endpoint}}" }""");
        var settings = new DictationSettingsStore(new AppDataPaths(this.root)).Load().Settings;

        Assert.Equal(expectedAllowed, settings.OllamaAllowRemoteEndpoint);
    }

    [Fact]
    public void Dictation_asks_for_english_as_the_wpf_app_did()
    {
        Assert.Equal("en", new DictationSettings().ToOptions().Language);
    }

    [Fact]
    public void History_details_use_the_wpf_wording()
    {
        var entry = new DictationHistoryEntry(
            Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, "final text", DictationDeliveryStatus.SkippedFocusChanged,
            "Notepad", "notepad", "Untitled", null, 1.5, false, "raw text", "the prompt");
        var text = DictationHistoryFilter.DetailsText(entry);

        Assert.Contains("Delivery: Skipped — focus changed", text);
        Assert.Contains("Ollama System Prompt:", text);
        Assert.Contains("Final Injected Transcript:", text);
        Assert.DoesNotContain("SkippedFocusChanged", text);
        Assert.Equal("Typed into app", DictationHistoryFilter.DeliveryDisplay(DictationDeliveryStatus.Injected));
        Assert.Equal("Command ran", DictationHistoryFilter.DeliveryDisplay(DictationDeliveryStatus.CommandExecuted));
    }

    [Fact]
    public void Transcription_defaults_are_seeded_from_the_dictation_model_and_old_files_still_load()
    {
        var seeded = TranscriptionPreferencesSeed.From("parakeet-onnx:parakeet-tdt-0.6b-v3", LiveTextModes.Draft);
        Assert.Equal("parakeet-onnx:parakeet-tdt-0.6b-v3", seeded.AsrModelId);
        Assert.Equal(LiveTextModes.Draft, seeded.LiveTextMode);
        Assert.Null(TranscriptionPreferencesSeed.From("  ", LiveTextModes.Off).AsrModelId);

        var path = Path.Combine(this.root, "transcription-settings.json");
        File.WriteAllText(path, """{ "SchemaVersion": 1, "AsrModelId": "x", "RequestedBackend": "gpu", "SpeakerDetection": true, "InputDeviceId": "{0.0.1.00000000}.{a}" }""");
        var loaded = new TranscriptionPreferencesStore(path).Load(() => new TranscriptionPreferences()).Preferences;
        Assert.Equal("x", loaded.AsrModelId);
    }
}
