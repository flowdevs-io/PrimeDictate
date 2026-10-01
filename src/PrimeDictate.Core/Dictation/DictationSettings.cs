using System.Text.Json;
using System.Text.Json.Serialization;
using PrimeDictate.Core.Storage;

namespace PrimeDictate.Core.Dictation;

public enum OverlayStyle
{
    CompactMicrophone = 0,
    FullPanel = 1
}

/// <summary>
/// Dictation settings. Property names and value formats match the WPF app's <c>settings.json</c> for the fields that
/// exist there, so the same file can be read as-is. Fields the WPF app has no equivalent for are new.
/// </summary>
public sealed class DictationSettings
{
    public HotkeyDto DictationHotkey { get; set; } = HotkeyDto.From(HotkeyGesture.Default);

    public HotkeyDto StopHotkey { get; set; } = HotkeyDto.From(HotkeyGesture.DefaultStop);

    public HotkeyDto HistoryHotkey { get; set; } = HotkeyDto.From(HotkeyGesture.DefaultHistory);

    /// <summary>Model id from the WPF catalog (for example "base.en"). Whisper ONNX only for now.</summary>
    public string? SelectedModelId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LegacyBackend TranscriptionBackend { get; set; } = LegacyBackend.Whisper;

    public string? SelectedInputDeviceId { get; set; }

    public double InputGainMultiplier { get; set; } = 1.0;

    public int AutoCommitSilenceSeconds { get; set; } = 3;

    public bool SendEnterAfterCommit { get; set; }

    public bool ReturnToStartTargetOnCommit { get; set; }

    /// <summary>The WPF "Request exclusive mic access while dictating" setting (same name, so it imports as is). Windows only.</summary>
    public bool ExclusiveMicAccessWhileDictating { get; set; }

    public bool PlayAudioCues { get; set; } = true;

    /// <summary>Where the ONNX speech models run: <c>auto</c>, <c>cpu</c> or <c>cuda</c>. Applies at the next start; PRIMEDICTATE_ONNX_DEVICE overrides it.</summary>
    public string OnnxDevice { get; set; } = "auto";

    /// <summary>
    /// The WPF app's compute setting (<c>Cpu</c>, <c>Gpu</c> or <c>Npu</c>), read from its <c>settings.json</c>. Null when the file had none.
    /// Only used for Whisper.net, and only until <see cref="WhisperNetDevice"/> is chosen in this app.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LegacyComputeInterface? TranscriptionComputeInterface { get; set; }

    /// <summary>Where Whisper.net models run: <c>auto</c>, <c>cpu</c> or <c>gpu</c>. Null follows the WPF value, else Auto. Applies at the next start; PRIMEDICTATE_WHISPERNET_DEVICE overrides it.</summary>
    public string? WhisperNetDevice { get; set; }

    /// <summary>The Whisper.net device this configuration asks for: an explicit choice, else the WPF compute setting, else Auto.</summary>
    public WhisperNetDevicePreference ResolveWhisperNetDevice() =>
        this.WhisperNetDevice is { Length: > 0 } chosen
            ? WhisperNetDevicePreferences.Parse(chosen)
            : this.TranscriptionComputeInterface switch
            {
                LegacyComputeInterface.Cpu => WhisperNetDevicePreference.Cpu,
                LegacyComputeInterface.Gpu => WhisperNetDevicePreference.Gpu,
                LegacyComputeInterface.Npu => WhisperNetDevicePreference.Npu,
                _ => WhisperNetDevicePreference.Auto
            };

    /// <summary>Typing speed used for the time-saved figure. Valid range 20 to 120.</summary>
    public int BaselineTypingSpeedWpm { get; set; } = DictationStatsStore.DefaultBaselineWpm;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OverlayStyle OverlayMode { get; set; } = OverlayStyle.CompactMicrophone;

    public bool IsOverlaySticky { get; set; }

    /// <summary>
    /// Where the user dragged the overlay: the bottom-center point it grows up from, in screen pixels. Null keeps the
    /// default, a little above the bottom middle of the primary screen. New app only (the WPF overlay did not remember).
    /// </summary>
    public int? OverlayAnchorX { get; set; }

    public int? OverlayAnchorY { get; set; }

    public List<ReplacementDto> TranscriptReplacements { get; set; } = [];

    /// <summary>Opt-in: listen on the idle microphone for the wake phrase and start dictation.</summary>
    public bool EnableWakeWord { get; set; }

    public string WakeWordPhrase { get; set; } = WakePhrase.Default;

    public bool EnableVoiceCommands { get; set; } = true;

    public string VoiceDictationPhrase { get; set; } = VoiceCommandProcessor.DefaultDictationPhrase;

    public string VoiceStopPhrase { get; set; } = VoiceCommandProcessor.DefaultStopPhrase;

    public string VoiceHistoryPhrase { get; set; } = VoiceCommandProcessor.DefaultHistoryPhrase;

    /// <summary>
    /// Phrases that run a command on this computer while dictating. Same shape as the WPF app's list, so imported commands
    /// keep their On state (the user chose them there). Commands added in this app start Off.
    /// </summary>
    public List<VoiceShellCommand> VoiceShellCommands { get; set; } = [];

    public VoiceCommandOptions ToVoiceCommandOptions() => new(
        this.EnableVoiceCommands,
        this.VoiceDictationPhrase?.Trim() ?? string.Empty,
        this.VoiceStopPhrase?.Trim() ?? string.Empty,
        this.VoiceHistoryPhrase?.Trim() ?? string.Empty,
        (this.VoiceShellCommands ?? []).Where(c => c is not null).ToList());

    public bool EnableOllamaPostProcessing { get; set; }

    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    public string OllamaModel { get; set; } = "gemma:2b";

    public OllamaMode OllamaMode { get; set; } = OllamaMode.Default;

    /// <summary>New: allow a non-loopback Ollama endpoint. Off by default so speech stays on this computer.</summary>
    public bool OllamaAllowRemoteEndpoint { get; set; }

    public OllamaOptions ToOllamaOptions() => new(
        this.EnableOllamaPostProcessing, this.OllamaEndpoint, this.OllamaModel, this.OllamaMode, this.OllamaAllowRemoteEndpoint);

    /// <summary>New: type even where the platform cannot check which window is in front. Off by default.</summary>
    public bool TypeWithoutFocusGuard { get; set; }

    public DictationOptions ToOptions() => new DictationOptions
    {
        InputDeviceId = this.SelectedInputDeviceId,
        InputGain = this.InputGainMultiplier,
        AutoCommitSilence = TimeSpan.FromSeconds(this.AutoCommitSilenceSeconds),
        SendEnterAfterCommit = this.SendEnterAfterCommit,
        ReturnToStartTarget = this.ReturnToStartTargetOnCommit,
        ExclusiveMicAccess = this.ExclusiveMicAccessWhileDictating,
        TypeWithoutFocusGuard = this.TypeWithoutFocusGuard,
        Replacements = this.TranscriptReplacements
            .Where(r => !string.IsNullOrWhiteSpace(r.Find))
            .Select(r => new ReplacementRule(r.Find, r.Replace))
            .ToList()
    }.Normalized();

    public IReadOnlyDictionary<HotkeyAction, HotkeyGesture> ToBindings() => new Dictionary<HotkeyAction, HotkeyGesture>
    {
        [HotkeyAction.ToggleDictation] = this.DictationHotkey.ToGesture(HotkeyGesture.Default),
        [HotkeyAction.EmergencyStop] = this.StopHotkey.ToGesture(HotkeyGesture.DefaultStop),
        [HotkeyAction.ShowHistory] = this.HistoryHotkey.ToGesture(HotkeyGesture.DefaultHistory)
    };

    /// <summary>The provider model id this setting selects (for example <c>parakeet-onnx:parakeet-tdt-0.6b-v3</c>), or null when none is selected.</summary>
    public string? ResolveModelId() =>
        string.IsNullOrWhiteSpace(this.SelectedModelId) ? null : $"{this.TranscriptionBackend.ModelIdPrefix()}:{this.SelectedModelId.Trim()}";
}

/// <summary>Backend names as the WPF settings file spells them.</summary>
public enum LegacyBackend
{
    Whisper = 0,
    Parakeet = 1,
    Moonshine = 2,
    WhisperNet = 3,
    QualcommQnn = 4
}

/// <summary>The WPF app's <c>TranscriptionComputeInterface</c> values.</summary>
public enum LegacyComputeInterface
{
    Cpu = 0,
    Gpu = 1,
    Npu = 2
}

/// <summary>Where Whisper.net runs. Npu is the WPF OpenVINO option and only takes effect when the model's OpenVINO files are installed.</summary>
public enum WhisperNetDevicePreference
{
    Auto = 0,
    Cpu = 1,
    Gpu = 2,
    Npu = 3
}

public static class WhisperNetDevicePreferences
{
    public static WhisperNetDevicePreference Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "cpu" => WhisperNetDevicePreference.Cpu,
        "gpu" or "cuda" => WhisperNetDevicePreference.Gpu,
        "npu" => WhisperNetDevicePreference.Npu,
        _ => WhisperNetDevicePreference.Auto
    };
}

public sealed class HotkeyDto
{
    public string KeyCode { get; set; } = "";

    public bool Ctrl { get; set; }

    public bool Shift { get; set; }

    public bool Alt { get; set; }

    public static HotkeyDto From(HotkeyGesture g) => new() { KeyCode = g.Key, Ctrl = g.Ctrl, Shift = g.Shift, Alt = g.Alt };

    /// <summary>A missing or invalid gesture falls back to the default rather than leaving an action unbound.</summary>
    public HotkeyGesture ToGesture(HotkeyGesture fallback)
    {
        var gesture = new HotkeyGesture(this.KeyCode, this.Ctrl, this.Shift, this.Alt);
        return gesture.IsValid(out _) ? gesture : fallback;
    }
}

public sealed class ReplacementDto
{
    public string Find { get; set; } = "";

    public string Replace { get; set; } = "";
}

public sealed record DictationSettingsLoad(DictationSettings Settings, bool ImportedFromWpf, string? Warning);

/// <summary>
/// Loads dictation settings from <c>dictation-settings.json</c>; on first run it imports the WPF app's
/// <c>settings.json</c> read-only. The WPF file is never written, so both apps keep working side by side.
/// </summary>
public sealed class DictationSettingsStore(AppDataPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Path => System.IO.Path.Combine(paths.Root, "dictation-settings.json");

    public string WpfSettingsPath => System.IO.Path.Combine(paths.Root, "settings.json");

    public DictationSettingsLoad Load()
    {
        foreach (var (file, imported) in new[] { (this.Path, false), (this.WpfSettingsPath, true) })
        {
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                var settings = JsonSerializer.Deserialize<DictationSettings>(File.ReadAllText(file), JsonOptions);
                if (settings is not null)
                {
                    return new DictationSettingsLoad(settings, imported, null);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return new DictationSettingsLoad(new DictationSettings(), false, $"Could not read {file}: {ex.Message}. Using defaults; the file was left untouched.");
            }
        }

        return new DictationSettingsLoad(new DictationSettings(), false, null);
    }

    public void Save(DictationSettings settings)
    {
        Directory.CreateDirectory(paths.Root);
        var temp = this.Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, this.Path, overwrite: true);
    }
}

public static class LegacyBackendExtensions
{
    public static string ModelIdPrefix(this LegacyBackend backend) => backend switch
    {
        LegacyBackend.Parakeet => "parakeet-onnx",
        LegacyBackend.Moonshine => "moonshine-onnx",
        LegacyBackend.WhisperNet => "whisper-net",
        _ => "whisper-onnx"
    };
}
