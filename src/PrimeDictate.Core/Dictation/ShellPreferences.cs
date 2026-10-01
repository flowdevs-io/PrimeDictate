using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrimeDictate.Core.Dictation;

/// <summary>What clicking the tray icon does. The first two names are the WPF app's, so its <c>settings.json</c> reads as-is.</summary>
public enum TrayClickBehavior
{
    SingleClickOpensWorkspace = 0,
    DoubleClickOpensWorkspace = 1,

    /// <summary>New: clicking the icon does nothing; the menu's "Open PrimeDictate" is the way in.</summary>
    ClickDoesNothing = 2
}

/// <summary>The color scheme of the app's windows. The WPF app was always dark; here the default follows the system.</summary>
public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2
}

/// <summary>
/// Reads an enum from its name (any case) or number, and falls back to <c>fallback</c> for a value this version does not know,
/// so a hand-edited or older file never stops the whole settings file from loading. <see cref="Legacy"/> maps old spellings.
/// </summary>
public class LenientEnumConverter<T>(T fallback) : JsonConverter<T>
    where T : struct, Enum
{
    /// <summary>Old names that mean a current value (the WPF app's pre-5.x tray names described Settings but opened the workspace).</summary>
    protected virtual IReadOnlyDictionary<string, T> Legacy => new Dictionary<string, T>();

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString() ?? string.Empty;
            if (this.Legacy.TryGetValue(text, out var mapped))
            {
                return mapped;
            }

            return Enum.TryParse<T>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
        {
            var value = (T)Enum.ToObject(typeof(T), number);
            return Enum.IsDefined(value) ? value : fallback;
        }

        reader.Skip();
        return fallback;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

public sealed class TrayClickBehaviorConverter() : LenientEnumConverter<TrayClickBehavior>(TrayClickBehavior.DoubleClickOpensWorkspace)
{
    protected override IReadOnlyDictionary<string, TrayClickBehavior> Legacy { get; } = new Dictionary<string, TrayClickBehavior>
    {
        ["SingleClickOpensSettings"] = TrayClickBehavior.SingleClickOpensWorkspace,
        ["DoubleClickOpensSettings"] = TrayClickBehavior.DoubleClickOpensWorkspace
    };
}

public sealed class AppThemeConverter() : LenientEnumConverter<AppTheme>(AppTheme.Dark);

/// <summary>
/// Decides whether a tray click opens the workspace. The tray only reports clicks, so a double click is two clicks close together.
/// A double click is consumed (a third click starts over), so triple-clicking does not open twice.
/// </summary>
public sealed class TrayClickDecider(Func<TrayClickBehavior> behavior, TimeSpan? doubleClickTime = null)
{
    private readonly TimeSpan window = doubleClickTime ?? TimeSpan.FromMilliseconds(500);
    private DateTime? lastClickUtc;

    /// <summary>True when this click should open the workspace.</summary>
    public bool OnClick(DateTime nowUtc)
    {
        switch (behavior())
        {
            case TrayClickBehavior.SingleClickOpensWorkspace:
                return true;
            case TrayClickBehavior.DoubleClickOpensWorkspace:
                if (this.lastClickUtc is { } last && nowUtc - last <= this.window)
                {
                    this.lastClickUtc = null;
                    return true;
                }

                this.lastClickUtc = nowUtc;
                return false;
            default:
                return false;
        }
    }
}
