namespace PrimeDictate.Core.Dictation;

public sealed record ReplacementRule(string Find, string? Replace);

/// <summary>
/// User find/replace rules applied to dictated text before it is typed. Behavior matches the
/// WPF app: longest find-string first so multi-word phrases win, case-insensitive matching,
/// literal replacement text.
/// </summary>
public static class TranscriptReplacements
{
    public static string Apply(string text, IReadOnlyList<ReplacementRule> rules)
    {
        if (string.IsNullOrEmpty(text) || rules.Count == 0)
        {
            return text;
        }

        var ordered = rules
            .Select(r => (Find: r.Find.Trim(), Replace: r.Replace ?? string.Empty))
            .Where(pair => pair.Find.Length > 0)
            .OrderByDescending(pair => pair.Find.Length)
            .ToList();

        var result = text;
        foreach (var (find, replace) in ordered)
        {
            result = result.Replace(find, replace, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }
}
