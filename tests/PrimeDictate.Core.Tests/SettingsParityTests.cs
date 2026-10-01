using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Storage;
using PrimeDictate.Platforms.Speech;

namespace PrimeDictate.Core.Tests;

/// <summary>Settings validation, first-run flag, custom model path and development-root discovery (the WPF parity items).</summary>
public sealed class SettingsParityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-parity-" + Guid.NewGuid().ToString("N"));

    public SettingsParityTests() => Directory.CreateDirectory(this.root);

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private string Touch(string folder, params string[] files)
    {
        var dir = Path.Combine(this.root, folder);
        Directory.CreateDirectory(dir);
        foreach (var f in files)
        {
            File.WriteAllText(Path.Combine(dir, f), "x");
        }

        return dir;
    }

    private static readonly string[] ParakeetFiles = ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"];

    // --- shortcuts ---

    [Fact]
    public void Hotkeys_must_be_valid_and_different()
    {
        var a = new HotkeyGesture("VcSpace", true, true, false);
        var b = new HotkeyGesture("VcEnter", true, true, false);
        var c = new HotkeyGesture("VcH", true, true, false);
        Assert.Null(DictationSettingsValidator.ValidateHotkeys(a, b, c));
        Assert.Contains("different", DictationSettingsValidator.ValidateHotkeys(a, a, c));
        Assert.Contains("different", DictationSettingsValidator.ValidateHotkeys(a, b, b));
        Assert.Contains("different", DictationSettingsValidator.ValidateHotkeys(c, b, c));
        Assert.StartsWith("Emergency stop:", DictationSettingsValidator.ValidateHotkeys(a, new HotkeyGesture("VcEnter", false, false, false), c));
        Assert.StartsWith("Start / stop dictation:", DictationSettingsValidator.ValidateHotkeys(new HotkeyGesture("VcLeftShift", true, false, false), b, c));
    }

    // --- voice phrases ---

    [Fact]
    public void Voice_commands_need_at_least_one_phrase_but_blanks_default()
    {
        Assert.Equal("Voice commands need at least one phrase.", DictationSettingsValidator.ValidateVoicePhrases(true, "", "  ", null, out _));

        Assert.Null(DictationSettingsValidator.ValidateVoicePhrases(true, "send it", "", "", out var phrases));
        Assert.Equal("send it", phrases.Commit);
        Assert.Equal(VoiceCommandProcessor.DefaultStopPhrase, phrases.Stop);
        Assert.Equal(VoiceCommandProcessor.DefaultHistoryPhrase, phrases.History);

        // Off, all blank: nothing to complain about, defaults are kept.
        Assert.Null(DictationSettingsValidator.ValidateVoicePhrases(false, "", "", "", out phrases));
        Assert.Equal(VoiceCommandProcessor.DefaultDictationPhrase, phrases.Commit);
    }

    [Fact]
    public void The_same_words_are_a_duplicate_however_they_are_written()
    {
        var error = DictationSettingsValidator.ValidateVoicePhrases(true, "Thank you!", "thank   you", "show me the money", out _);
        Assert.Equal("Use different phrases for Emergency stop and Start / stop voice commands.", error);
        Assert.NotNull(DictationSettingsValidator.ValidateVoicePhrases(true, "alpha", "bravo", "ALPHA", out _));
        // A blank phrase that falls back to a default another phrase already uses is a duplicate too.
        Assert.NotNull(DictationSettingsValidator.ValidateVoicePhrases(true, "potato farmer", "", "x", out _));
    }

    [Fact]
    public void Shell_command_phrases_cannot_reuse_a_reserved_or_wake_phrase_or_each_other()
    {
        var voice = new EffectiveVoicePhrases("thank you", "potato farmer", "show me the money");
        VoiceShellCommand Cmd(string phrase) => new() { Phrase = phrase, Command = "x" };

        Assert.Null(DictationSettingsValidator.ValidateShellCommands(voice, [Cmd("open notes"), Cmd("lock screen")], false, null));
        Assert.Contains("conflicts with Stop phrase", DictationSettingsValidator.ValidateShellCommands(voice, [Cmd("Potato, Farmer")], false, null));
        Assert.Contains("conflicts with computer command \"open notes\"", DictationSettingsValidator.ValidateShellCommands(voice, [Cmd("open notes"), Cmd("OPEN NOTES!")], false, null));
        Assert.Contains("at least one letter or number", DictationSettingsValidator.ValidateShellCommands(voice, [Cmd("...")], false, null));

        // The wake phrase only counts while the wake word is on.
        Assert.Null(DictationSettingsValidator.ValidateShellCommands(voice, [Cmd("okay computer")], false, "okay computer"));
        Assert.Contains("wake phrase", DictationSettingsValidator.ValidateShellCommands(voice, [Cmd("okay computer")], true, "okay computer"));
        Assert.Contains("wake phrase", DictationSettingsValidator.ValidateShellCommands(voice, [Cmd("okay computer")], true, null));
    }

    [Fact]
    public void The_wake_phrase_cannot_be_a_voice_phrase()
    {
        var voice = new EffectiveVoicePhrases("thank you", "potato farmer", "show me the money");
        Assert.Null(DictationSettingsValidator.ValidateWakePhrase(true, "okay computer", voice));
        Assert.Null(DictationSettingsValidator.ValidateWakePhrase(false, "thank you", voice));
        Assert.Contains("wake phrase", DictationSettingsValidator.ValidateWakePhrase(true, "Thank you.", voice));
    }

    // --- Ollama ---

    [Fact]
    public void Ollama_needs_a_http_endpoint_and_a_model_only_when_on()
    {
        Assert.Null(DictationSettingsValidator.ValidateOllama(false, "", ""));
        Assert.Null(DictationSettingsValidator.ValidateOllama(true, "http://localhost:11434", "gemma:2b"));
        Assert.NotNull(DictationSettingsValidator.ValidateOllama(true, "localhost:11434", "gemma:2b"));
        Assert.NotNull(DictationSettingsValidator.ValidateOllama(true, "ftp://host", "gemma:2b"));
        Assert.NotNull(DictationSettingsValidator.ValidateOllama(true, "http://localhost:11434", " "));
        Assert.False(DictationSettingsValidator.IsRemoteEndpoint("http://localhost:11434"));
        Assert.False(DictationSettingsValidator.IsRemoteEndpoint("http://127.0.0.1:11434"));
        Assert.True(DictationSettingsValidator.IsRemoteEndpoint("http://192.168.1.20:11434"));
        Assert.True(DictationSettingsValidator.IsRemoteEndpoint("https://ollama.example.com"));
    }

    // --- startup normalisation ---

    [Fact]
    public void Normalisation_repairs_duplicate_or_invalid_shortcuts_and_blank_or_clashing_phrases()
    {
        var settings = new DictationSettings
        {
            DictationHotkey = HotkeyDto.From(new HotkeyGesture("VcSpace", true, true, false)),
            StopHotkey = HotkeyDto.From(new HotkeyGesture("VcSpace", true, true, false)),
            HistoryHotkey = new HotkeyDto { KeyCode = "VcH" },
            VoiceDictationPhrase = "",
            VoiceStopPhrase = "Send It",
            VoiceHistoryPhrase = "send it",
            WakeWordPhrase = " "
        };

        Assert.True(DictationSettingsValidator.Normalize(settings));
        Assert.Equal(HotkeyDto.From(HotkeyGesture.Default), settings.DictationHotkey, new HotkeyDtoComparer());
        Assert.Equal(HotkeyGesture.DefaultStop, settings.StopHotkey.ToGesture(HotkeyGesture.Default));
        Assert.Equal(HotkeyGesture.DefaultHistory, settings.HistoryHotkey.ToGesture(HotkeyGesture.Default));
        Assert.Equal(VoiceCommandProcessor.DefaultDictationPhrase, settings.VoiceDictationPhrase);
        Assert.Equal("Send It", settings.VoiceStopPhrase);
        Assert.Equal(VoiceCommandProcessor.DefaultHistoryPhrase, settings.VoiceHistoryPhrase);
        Assert.Equal(WakePhrase.Default, settings.WakeWordPhrase);
        Assert.False(DictationSettingsValidator.Normalize(settings));
    }

    [Fact]
    public void Normalisation_moves_the_stop_shortcut_when_its_default_is_the_dictation_shortcut()
    {
        var settings = new DictationSettings
        {
            DictationHotkey = HotkeyDto.From(HotkeyGesture.DefaultStop),
            StopHotkey = HotkeyDto.From(HotkeyGesture.DefaultStop)
        };
        Assert.True(DictationSettingsValidator.Normalize(settings));
        var stop = settings.StopHotkey.ToGesture(HotkeyGesture.Default);
        Assert.Equal(new HotkeyGesture("VcEnter", true, false, true), stop);
        Assert.Null(DictationSettingsValidator.ValidateHotkeys(
            settings.DictationHotkey.ToGesture(HotkeyGesture.Default), stop, settings.HistoryHotkey.ToGesture(HotkeyGesture.Default)));
    }

    [Fact]
    public void A_stop_phrase_that_equals_the_default_history_phrase_clears_the_history_phrase()
    {
        var settings = new DictationSettings { VoiceStopPhrase = "show me the money", VoiceHistoryPhrase = "" };
        Assert.True(DictationSettingsValidator.Normalize(settings));
        Assert.Equal(string.Empty, settings.VoiceHistoryPhrase);
    }

    private sealed class HotkeyDtoComparer : IEqualityComparer<HotkeyDto>
    {
        public bool Equals(HotkeyDto? x, HotkeyDto? y) => x!.KeyCode == y!.KeyCode && x.Ctrl == y.Ctrl && x.Shift == y.Shift && x.Alt == y.Alt;

        public int GetHashCode(HotkeyDto obj) => obj.KeyCode.GetHashCode();
    }

    // --- first run ---

    [Fact]
    public void First_run_follows_the_wpf_flag_and_a_new_app_file_counts_as_finished()
    {
        var store = new DictationSettingsStore(new AppDataPaths(this.root));
        Assert.False(store.Load().Settings.FirstRunCompleted);

        File.WriteAllText(Path.Combine(this.root, "settings.json"), """{ "FirstRunCompleted": false, "SelectedModelId": "base.en" }""");
        Assert.False(store.Load().Settings.FirstRunCompleted);
        File.WriteAllText(Path.Combine(this.root, "settings.json"), """{ "FirstRunCompleted": true }""");
        Assert.True(store.Load().Settings.FirstRunCompleted);

        // The new app's own file wins, and one written before the flag existed means setup happened.
        File.WriteAllText(Path.Combine(this.root, "dictation-settings.json"), """{ "SelectedModelId": "base.en" }""");
        Assert.True(store.Load().Settings.FirstRunCompleted);
        File.WriteAllText(Path.Combine(this.root, "dictation-settings.json"), """{ "FirstRunCompleted": false }""");
        Assert.False(store.Load().Settings.FirstRunCompleted);
    }

    [Fact]
    public void The_flag_round_trips_through_the_new_app_file()
    {
        var store = new DictationSettingsStore(new AppDataPaths(this.root));
        var settings = store.Load().Settings;
        store.Save(settings);
        Assert.False(store.Load().Settings.FirstRunCompleted);
        settings.FirstRunCompleted = true;
        store.Save(settings);
        Assert.True(store.Load().Settings.FirstRunCompleted);
    }

    [Fact]
    public void Loading_normalises_a_hand_edited_file()
    {
        File.WriteAllText(Path.Combine(this.root, "dictation-settings.json"), """
            { "FirstRunCompleted": true, "VoiceDictationPhrase": "", "VoiceStopPhrase": "", "VoiceHistoryPhrase": "" }
            """);
        var settings = new DictationSettingsStore(new AppDataPaths(this.root)).Load().Settings;
        Assert.Equal(VoiceCommandProcessor.DefaultDictationPhrase, settings.VoiceDictationPhrase);
        Assert.Equal(VoiceCommandProcessor.DefaultStopPhrase, settings.VoiceStopPhrase);
    }

    // --- custom model path ---

    [Fact]
    public void The_wpf_model_path_is_imported_and_drives_the_model_when_it_validates()
    {
        var external = this.Touch("elsewhere/my-parakeet", ParakeetFiles);
        File.WriteAllText(
            Path.Combine(this.root, "settings.json"),
            "{ \"FirstRunCompleted\": true, \"TranscriptionBackend\": \"Parakeet\", \"SelectedModelId\": \"parakeet-tdt-0.6b-v3\", \"ModelPath\": " + System.Text.Json.JsonSerializer.Serialize(external) + " }");
        var settings = new DictationSettingsStore(new AppDataPaths(this.root)).Load().Settings;
        Assert.Equal(external, settings.ModelPath);

        var custom = Assert.IsType<InstalledSpeechModel>(SpeechModelLocator.CustomModel(settings));
        Assert.True(custom.IsCustom);
        Assert.Equal(LegacyBackend.Parakeet, custom.Backend);
        Assert.Equal("parakeet-onnx:my-parakeet", SpeechModelLocator.WantedModelId(settings));
        var all = SpeechModelLocator.DiscoverFor(Path.Combine(this.root, "models"), settings, []);
        Assert.Equal(custom.ModelId, all[0].ModelId);
    }

    [Fact]
    public void A_missing_or_incomplete_model_path_falls_back_to_the_selected_model()
    {
        var incomplete = this.Touch("half", "encoder.int8.onnx", "tokens.txt");
        var settings = new DictationSettings { TranscriptionBackend = LegacyBackend.Parakeet, SelectedModelId = "parakeet-tdt-0.6b-v3", ModelPath = incomplete };
        Assert.Null(SpeechModelLocator.CustomModel(settings));
        Assert.Equal("parakeet-onnx:parakeet-tdt-0.6b-v3", SpeechModelLocator.WantedModelId(settings));
        settings.ModelPath = Path.Combine(this.root, "gone");
        Assert.Null(SpeechModelLocator.CustomModel(settings));
    }

    [Fact]
    public void A_path_that_is_a_catalog_install_keeps_the_catalog_id_and_replaces_the_installed_entry()
    {
        var managed = Path.Combine(this.root, "models");
        var option = SpeechModelCatalog.Options.Single(o => o.Id == "parakeet-tdt-0.6b-v3");
        var managedDir = SpeechModelLocator.InstallPath(managed, option);
        Directory.CreateDirectory(managedDir);
        foreach (var f in ParakeetFiles)
        {
            File.WriteAllText(Path.Combine(managedDir, f), "x");
        }

        var copy = this.Touch("usb/" + option.InstallDirectoryName, ParakeetFiles);
        var settings = new DictationSettings { TranscriptionBackend = LegacyBackend.Parakeet, SelectedModelId = option.Id, ModelPath = copy };
        var all = SpeechModelLocator.DiscoverFor(managed, settings, []);
        var entry = Assert.Single(all, m => m.ModelId == "parakeet-onnx:parakeet-tdt-0.6b-v3");
        Assert.True(entry.IsCustom);
        Assert.Equal(Path.GetFullPath(copy), entry.Directory);
    }

    [Fact]
    public void Each_family_validates_a_custom_path_with_its_own_rules()
    {
        var whisper = this.Touch("w", "x-encoder.int8.onnx", "x-decoder.int8.onnx", "x-tokens.txt");
        Assert.True(SpeechModelLocator.TryResolveCustom(whisper, null, out var w, out _));
        Assert.Equal(LegacyBackend.Whisper, w.Backend);

        var parakeet = this.Touch("p", ParakeetFiles);
        Assert.True(SpeechModelLocator.TryResolveCustom(parakeet, null, out var p, out _));
        Assert.Equal(LegacyBackend.Parakeet, p.Backend);

        var moonshine = this.Touch("m", "preprocess.onnx", "encode.int8.onnx", "uncached_decode.int8.onnx", "cached_decode.int8.onnx", "tokens.txt");
        Assert.True(SpeechModelLocator.TryResolveCustom(moonshine, null, out var m, out _));
        Assert.Equal(LegacyBackend.Moonshine, m.Backend);

        var ggml = Path.Combine(this.root, "ggml-custom.en.bin");
        File.WriteAllText(ggml, "weights");
        Assert.True(SpeechModelLocator.TryResolveCustom(ggml, null, out var g, out _));
        Assert.Equal(LegacyBackend.WhisperNet, g.Backend);
        Assert.Equal("custom.en", g.Id);
        Assert.True(g.IsEnglishOnly);

        var empty = this.Touch("e", "tokens.txt");
        Assert.False(SpeechModelLocator.TryResolveCustom(empty, LegacyBackend.Parakeet, out _, out var problem));
        Assert.Contains("not a model", problem);
        Assert.False(SpeechModelLocator.TryResolveCustom(Path.Combine(this.root, "nope"), null, out _, out problem));
        Assert.Contains("does not exist", problem);
        Assert.False(SpeechModelLocator.TryResolveCustom(LegacyBackend.Parakeet, empty, out _, out problem));
        Assert.Contains("encoder.int8.onnx", problem);
        Assert.False(SpeechModelLocator.TryResolveCustom(LegacyBackend.Moonshine, empty, out _, out problem));
        Assert.Contains("preprocess.onnx", problem);
        Assert.False(SpeechModelLocator.TryResolveCustom(LegacyBackend.WhisperNet, Path.Combine(this.root, "none.bin"), out _, out problem));
        Assert.Contains("GGML", problem);
    }

    [Fact]
    public void A_cut_off_catalog_ggml_file_is_rejected_but_a_complete_one_keeps_its_catalog_id()
    {
        var option = SpeechModelCatalog.Options.Single(o => o.Backend == LegacyBackend.WhisperNet && o.Id == "tiny.en");
        var folder = Path.Combine(this.root, "ggml");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, option.FileName!);
        File.WriteAllText(file, "short");
        Assert.False(SpeechModelLocator.TryResolveCustom(LegacyBackend.WhisperNet, file, out _, out var problem));
        Assert.Contains("incomplete", problem);
        using (var stream = File.Create(file))
        {
            stream.SetLength(option.ApproximateBytes);
        }

        Assert.True(SpeechModelLocator.TryResolveCustom(LegacyBackend.WhisperNet, file, out var model, out _));
        Assert.Equal("whisper-net:tiny.en", model.ModelId);
    }

    // --- development roots ---

    [Fact]
    public void Extra_roots_are_searched_after_the_managed_folder_for_every_family()
    {
        var managed = Path.Combine(this.root, "managed");
        var repo = Path.Combine(this.root, "repo", "models");
        var parakeetOption = SpeechModelCatalog.Options.Single(o => o.Id == "parakeet-tdt-0.6b-v3");
        this.Touch(Path.Combine("repo", "models", "parakeet", parakeetOption.InstallDirectoryName), ParakeetFiles);
        this.Touch(Path.Combine("repo", "models", "whisper", "sherpa-onnx-whisper-tiny.en"), "a-encoder.int8.onnx", "a-decoder.int8.onnx", "a-tokens.txt");
        var ggml = SpeechModelCatalog.Options.Single(o => o.Backend == LegacyBackend.WhisperNet && o.Id == "tiny.en");
        var ggmlPath = SpeechModelLocator.InstallPath(repo, ggml);
        Directory.CreateDirectory(Path.GetDirectoryName(ggmlPath)!);
        using (var stream = File.Create(ggmlPath))
        {
            stream.SetLength(ggml.ApproximateBytes);
        }

        Assert.Empty(SpeechModelLocator.Discover(managed));
        var ids = SpeechModelLocator.Discover(managed, [repo]).Select(m => m.ModelId).Order().ToArray();
        Assert.Equal(["parakeet-onnx:parakeet-tdt-0.6b-v3", "whisper-net:tiny.en", "whisper-onnx:tiny.en"], ids);

        // The managed copy wins when both exist.
        this.Touch(Path.Combine("managed", "parakeet", parakeetOption.InstallDirectoryName), ParakeetFiles);
        var parakeet = Assert.Single(SpeechModelLocator.Discover(managed, [repo]), m => m.Backend == LegacyBackend.Parakeet);
        Assert.StartsWith(Path.GetFullPath(managed), parakeet.Directory);
    }

    [Fact]
    public void Development_roots_are_the_app_folder_and_the_working_directory_with_its_parents()
    {
        var app = Path.Combine(this.root, "app");
        var work = Path.Combine(this.root, "a", "b", "c");
        var roots = SpeechModelLocator.DevelopmentRoots(app, work);
        Assert.Equal(Path.Combine(app, "models"), roots[0]);
        Assert.Equal(Path.Combine(work, "models"), roots[1]);
        Assert.Contains(Path.Combine(this.root, "a", "b", "models"), roots);
        Assert.Contains(Path.Combine(this.root, "a", "models"), roots);
        Assert.Equal(roots.Count, roots.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(roots.Count <= 9);
    }
}
