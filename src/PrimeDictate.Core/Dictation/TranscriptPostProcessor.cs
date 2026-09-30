namespace PrimeDictate.Core.Dictation;

/// <summary>Cleans a final transcript before it is typed. Pure text logic, ported from the WPF controller.</summary>
public static class TranscriptPostProcessor
{
    /// <summary>
    /// Whisper often hallucinates a trailing "ok" or "okay" from the silence that triggered an auto-commit.
    /// Removed only for silence commits and only when other words remain.
    /// </summary>
    public static string RemoveTrailingSilenceArtifact(string transcript, bool committedBySilence)
    {
        if (!committedBySilence)
        {
            return transcript;
        }

        var text = transcript.TrimEnd();
        if (text.Length == 0 || (!EndsWithToken(text, "ok") && !EndsWithToken(text, "okay")))
        {
            return text;
        }

        var split = text.LastIndexOf(' ');
        if (split <= 0)
        {
            return text;
        }

        var without = text[..split].TrimEnd(' ', '\t', ',', '.', ';', ':', '!', '?', '-', '—');
        return without.Length == 0 ? text : without;
    }

    public static string Process(string transcript, bool committedBySilence, IReadOnlyList<ReplacementRule> replacements) =>
        TranscriptReplacements.Apply(RemoveTrailingSilenceArtifact(transcript, committedBySilence), replacements);

    private static bool EndsWithToken(string text, string token)
    {
        if (text.Length <= token.Length)
        {
            return string.Equals(text, token, StringComparison.OrdinalIgnoreCase);
        }

        var start = text.Length - token.Length;
        if (!text.AsSpan(start).Equals(token, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var boundary = text[start - 1];
        return char.IsWhiteSpace(boundary) || char.IsPunctuation(boundary);
    }
}
