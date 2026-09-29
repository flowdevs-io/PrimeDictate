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

    public bool PlayAudioCues { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OverlayStyle OverlayMode { get; set; } = OverlayStyle.CompactMicrophone;

    public bool IsOverlaySticky { get; set; }

    public List<ReplacementDto> TranscriptReplacements { get; set; } = [];

    /// <summary>New: type even where the platform cannot check which window is in front. Off by default.</summary>
    public bool TypeWithoutFocusGuard { get; set; }

    public DictationOptions ToOptions() => new DictationOptions
    {
        InputDeviceId = this.SelectedInputDeviceId,
        InputGain = this.InputGainMultiplier,
        AutoCommitSilence = TimeSpan.FromSeconds(this.AutoCommitSilenceSeconds),
        SendEnterAfterCommit = this.SendEnterAfterCommit,
        ReturnToStartTarget = this.ReturnToStartTargetOnCommit,
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

    /// <summary>The provider model id this setting selects, or null when the backend is not available in the new app yet.</summary>
    public string? ResolveModelId() =>
        this.TranscriptionBackend == LegacyBackend.Whisper && !string.IsNullOrWhiteSpace(this.SelectedModelId)
            ? $"whisper-onnx:{this.SelectedModelId.Trim()}"
            : null;
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
