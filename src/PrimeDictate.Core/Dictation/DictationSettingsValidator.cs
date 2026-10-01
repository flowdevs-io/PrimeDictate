using System.Net;

namespace PrimeDictate.Core.Dictation;

/// <summary>The voice phrases that will be saved: blank ones replaced by the defaults, as the WPF Settings window did.</summary>
public sealed record EffectiveVoicePhrases(string Commit, string Stop, string History);

/// <summary>
/// Rules a settings change must satisfy before it is saved, ported from the WPF Settings window (<c>TryBuildSettings</c>) and its
/// startup normalisation (<c>NormalizeShortcutSettings</c>). Pure functions, so the window only shows what they return.
/// </summary>
public static class DictationSettingsValidator
{
    /// <summary>Null when the three shortcuts are valid and all different. Otherwise a message for the user.</summary>
    public static string? ValidateHotkeys(HotkeyGesture dictation, HotkeyGesture stop, HotkeyGesture history)
    {
        foreach (var (gesture, label) in new[] { (dictation, "Start / stop dictation"), (stop, "Emergency stop"), (history, "Open history") })
        {
            if (!gesture.IsValid(out var error))
            {
                return $"{label}: {error}";
            }
        }

        return dictation == stop || dictation == history || stop == history
            ? "Each keyboard shortcut must use a different key combination."
            : null;
    }

    /// <summary>
    /// Blank phrases take their defaults; with voice commands on, all three blank is an error, and two phrases that say the same
    /// words are an error. Null means valid.
    /// </summary>
    public static string? ValidateVoicePhrases(bool voiceCommandsEnabled, string? commit, string? stop, string? history, out EffectiveVoicePhrases effective)
    {
        var c = commit?.Trim() ?? string.Empty;
        var s = stop?.Trim() ?? string.Empty;
        var h = history?.Trim() ?? string.Empty;
        effective = new EffectiveVoicePhrases(
            c.Length == 0 ? VoiceCommandProcessor.DefaultDictationPhrase : c,
            s.Length == 0 ? VoiceCommandProcessor.DefaultStopPhrase : s,
            h.Length == 0 ? VoiceCommandProcessor.DefaultHistoryPhrase : h);
        if (voiceCommandsEnabled && c.Length == 0 && s.Length == 0 && h.Length == 0)
        {
            return "Voice commands need at least one phrase.";
        }

        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return TryReserve(owners, effective.Commit, "Start / stop", out var error)
            && TryReserve(owners, effective.Stop, "Emergency stop", out error)
            && TryReserve(owners, effective.History, "Open history", out error)
                ? null
                : error;
    }

    /// <summary>
    /// Checks the phrases of the commands that run programs against the three voice phrases and each other (and the wake phrase, when
    /// the wake word is on: saying it would otherwise start a dictation and run a command at once). Incomplete rows are the editor's concern.
    /// </summary>
    public static string? ValidateShellCommands(EffectiveVoicePhrases reserved, IEnumerable<VoiceShellCommand> commands, bool wakeEnabled, string? wakePhrase)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Reserve(owners, reserved.Commit, "Start / stop phrase");
        Reserve(owners, reserved.Stop, "Stop phrase");
        Reserve(owners, reserved.History, "History phrase");
        if (wakeEnabled)
        {
            Reserve(owners, WakePhrase.Normalize(wakePhrase), "wake phrase");
        }

        foreach (var command in commands)
        {
            var phrase = command.Phrase.Trim();
            var key = PhraseKey(phrase);
            if (key.Length == 0)
            {
                return "Computer command phrases need at least one letter or number.";
            }

            if (owners.TryGetValue(key, out var existing))
            {
                return $"The computer command phrase \"{phrase}\" conflicts with {existing}.";
            }

            owners.Add(key, $"computer command \"{phrase}\"");
        }

        return null;
    }

    /// <summary>The wake phrase against the three voice phrases (only when the wake word is on).</summary>
    public static string? ValidateWakePhrase(bool wakeEnabled, string? wakePhrase, EffectiveVoicePhrases voice)
    {
        if (!wakeEnabled)
        {
            return null;
        }

        var key = PhraseKey(WakePhrase.Normalize(wakePhrase));
        foreach (var (phrase, label) in new[] { (voice.Commit, "start / stop"), (voice.Stop, "emergency stop"), (voice.History, "history") })
        {
            if (key == PhraseKey(phrase))
            {
                return $"The wake phrase and the {label} voice phrase must be different.";
            }
        }

        return null;
    }

    /// <summary>With rewriting on, the endpoint must be an http(s) address and a model must be named. Null means valid.</summary>
    public static string? ValidateOllama(bool enabled, string? endpoint, string? model)
    {
        if (!enabled)
        {
            return null;
        }

        if (!Uri.TryCreate((endpoint ?? string.Empty).Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return "The Ollama endpoint must be an http or https address, for example http://localhost:11434.";
        }

        return string.IsNullOrWhiteSpace(model) ? "Name the Ollama model to use, for example gemma:2b." : null;
    }

    /// <summary>True when the endpoint is not on this computer (it would be refused unless remote endpoints are allowed).</summary>
    public static bool IsRemoteEndpoint(string? endpoint) =>
        Uri.TryCreate((endpoint ?? string.Empty).Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && !(uri.IsLoopback || (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip)));

    /// <summary>
    /// The WPF startup normalisation: an invalid or duplicate shortcut falls back to its default (or an alternative when the default is
    /// taken), a blank phrase gets its default, and a stop or history phrase equal to an earlier one is cleared. Returns true when something changed.
    /// </summary>
    public static bool Normalize(DictationSettings settings)
    {
        var changed = false;
        var dictation = Usable(settings.DictationHotkey);
        var stop = Usable(settings.StopHotkey);
        var history = Usable(settings.HistoryHotkey);
        var hotkeysChanged = false;
        if (dictation is null)
        {
            dictation = HotkeyGesture.Default;
            hotkeysChanged = true;
        }

        if (stop is null || stop == dictation)
        {
            stop = HotkeyGesture.DefaultStop == dictation ? new HotkeyGesture("VcEnter", true, false, true) : HotkeyGesture.DefaultStop;
            hotkeysChanged = true;
        }

        if (history is null || history == dictation || history == stop)
        {
            history = HotkeyGesture.DefaultHistory == dictation || HotkeyGesture.DefaultHistory == stop
                ? new HotkeyGesture("VcH", true, false, true)
                : HotkeyGesture.DefaultHistory;
            hotkeysChanged = true;
        }

        if (hotkeysChanged)
        {
            settings.DictationHotkey = HotkeyDto.From(dictation);
            settings.StopHotkey = HotkeyDto.From(stop);
            settings.HistoryHotkey = HotkeyDto.From(history);
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.VoiceDictationPhrase))
        {
            settings.VoiceDictationPhrase = VoiceCommandProcessor.DefaultDictationPhrase;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.WakeWordPhrase))
        {
            settings.WakeWordPhrase = WakePhrase.Default;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.VoiceStopPhrase) || Same(settings.VoiceStopPhrase, settings.VoiceDictationPhrase))
        {
            settings.VoiceStopPhrase = DefaultUnlessTaken(VoiceCommandProcessor.DefaultStopPhrase, settings.VoiceDictationPhrase);
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.VoiceHistoryPhrase) || Same(settings.VoiceHistoryPhrase, settings.VoiceDictationPhrase) || Same(settings.VoiceHistoryPhrase, settings.VoiceStopPhrase))
        {
            settings.VoiceHistoryPhrase = DefaultUnlessTaken(VoiceCommandProcessor.DefaultHistoryPhrase, settings.VoiceDictationPhrase, settings.VoiceStopPhrase);
            changed = true;
        }

        if (settings.VoiceShellCommands is null)
        {
            settings.VoiceShellCommands = [];
            changed = true;
        }

        if (settings.TranscriptReplacements is null)
        {
            settings.TranscriptReplacements = [];
            changed = true;
        }

        return changed;
    }

    /// <summary>Lower-cased words made of letters and digits, single-spaced: "Thank you!" and "thank   you" are the same phrase.</summary>
    public static string PhraseKey(string? phrase)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var character in phrase ?? string.Empty)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return string.Join(' ', words);
    }

    private static HotkeyGesture? Usable(HotkeyDto? dto)
    {
        if (dto is null)
        {
            return null;
        }

        var gesture = new HotkeyGesture(dto.KeyCode, dto.Ctrl, dto.Shift, dto.Alt);
        return gesture.IsValid(out _) ? gesture : null;
    }

    private static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string DefaultUnlessTaken(string defaultPhrase, params string?[] existing) =>
        existing.Any(e => Same(defaultPhrase, e)) ? string.Empty : defaultPhrase;

    private static bool TryReserve(Dictionary<string, string> owners, string phrase, string owner, out string error)
    {
        var key = PhraseKey(phrase);
        if (key.Length > 0 && owners.TryGetValue(key, out var existing))
        {
            error = $"Use different phrases for {owner} and {existing} voice commands.";
            return false;
        }

        if (key.Length > 0)
        {
            owners.Add(key, owner);
        }

        error = string.Empty;
        return true;
    }

    private static void Reserve(Dictionary<string, string> owners, string phrase, string owner)
    {
        var key = PhraseKey(phrase);
        if (key.Length > 0)
        {
            owners[key] = owner;
        }
    }
}
