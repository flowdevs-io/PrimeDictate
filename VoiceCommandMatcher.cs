using System.Text;

namespace PrimeDictate;

internal readonly record struct VoiceCommandMatch(
    string CleanedText,
    bool CommitRequested,
    bool StopRequested,
    bool HistoryRequested,
    VoiceShellCommandInvocation? ShellCommandInvocation);

internal sealed record VoiceShellCommandInvocation(
    VoiceShellCommand Command,
    string TextToType);

internal sealed record VoiceCommandOptions(
    bool Enabled,
    string DictationPhrase,
    string StopPhrase,
    string HistoryPhrase,
    IReadOnlyList<VoiceShellCommand> ShellCommands);

internal static class VoiceCommandMatcher
{
    public static VoiceCommandMatch Apply(
        string transcript,
        VoiceCommandOptions options,
        bool includeShellCommands = false)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(transcript))
        {
            return new VoiceCommandMatch(
                transcript,
                CommitRequested: false,
                StopRequested: false,
                HistoryRequested: false,
                ShellCommandInvocation: null);
        }

        var cleaned = transcript;
        var commitRequested = TryRemovePhrase(cleaned, options.DictationPhrase, out cleaned);
        var stopRequested = TryRemovePhrase(cleaned, options.StopPhrase, out cleaned);
        var historyRequested = TryRemovePhrase(cleaned, options.HistoryPhrase, out cleaned);
        VoiceShellCommandInvocation? shellCommandInvocation = null;
        if (includeShellCommands &&
            !commitRequested &&
            !stopRequested &&
            TryFindShellCommand(cleaned, options.ShellCommands, out var matchedInvocation, out var shellCleaned))
        {
            shellCommandInvocation = matchedInvocation;
            cleaned = shellCleaned;
        }

        return new VoiceCommandMatch(
            CollapseWhitespace(cleaned).Trim(),
            commitRequested,
            stopRequested,
            historyRequested,
            shellCommandInvocation);
    }

    private static bool TryFindShellCommand(
        string transcript,
        IReadOnlyList<VoiceShellCommand> shellCommands,
        out VoiceShellCommandInvocation invocation,
        out string cleaned)
    {
        cleaned = transcript;
        var textTokens = Tokenize(transcript);
        if (textTokens.Count == 0)
        {
            invocation = null!;
            return false;
        }

        foreach (var candidate in shellCommands.OrderByDescending(command => Tokenize(command.Phrase).Count))
        {
            if (!candidate.Enabled ||
                string.IsNullOrWhiteSpace(candidate.Phrase) ||
                string.IsNullOrWhiteSpace(candidate.Command))
            {
                continue;
            }

            var phraseTokens = Tokenize(candidate.Phrase);
            if (phraseTokens.Count == 0 || textTokens.Count < phraseTokens.Count)
            {
                continue;
            }

            for (var startIndex = 0; startIndex <= textTokens.Count - phraseTokens.Count; startIndex++)
            {
                if (!TokensMatchAt(textTokens, phraseTokens, startIndex))
                {
                    continue;
                }

                var removeStart = textTokens[startIndex].Start;
                var phraseEnd = textTokens[startIndex + phraseTokens.Count - 1].End;
                var removeEnd = phraseEnd;
                var textToType = string.Empty;
                var remainder = transcript[phraseEnd..];
                if (TryGetChainedTypeText(remainder, out var chainedText, out var chainedRemoveEnd))
                {
                    textToType = chainedText;
                    removeEnd = phraseEnd + chainedRemoveEnd;
                }

                cleaned = RemoveSpan(transcript, removeStart, removeEnd);
                invocation = new VoiceShellCommandInvocation(candidate, textToType);
                return true;
            }
        }

        invocation = null!;
        return false;
    }

    private static bool TokensMatchAt(
        IReadOnlyList<TokenSpan> textTokens,
        IReadOnlyList<TokenSpan> phraseTokens,
        int startIndex)
    {
        for (var index = 0; index < phraseTokens.Count; index++)
        {
            if (!string.Equals(
                    textTokens[startIndex + index].Value,
                    phraseTokens[index].Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetChainedTypeText(string remainder, out string textToType, out int removeEnd)
    {
        removeEnd = 0;
        var tokens = Tokenize(remainder);
        if (tokens.Count < 2)
        {
            textToType = string.Empty;
            return false;
        }

        var typeTokenIndex = GetTypeTokenIndex(tokens);

        if (typeTokenIndex >= tokens.Count - 1 ||
            !string.Equals(tokens[typeTokenIndex].Value, "type", StringComparison.OrdinalIgnoreCase))
        {
            textToType = string.Empty;
            return false;
        }

        textToType = CollapseWhitespace(remainder[tokens[typeTokenIndex].End..]).Trim();
        if (textToType.Length == 0)
        {
            return false;
        }

        removeEnd = remainder.Length;
        return true;
    }

    private static int GetTypeTokenIndex(IReadOnlyList<TokenSpan> tokens)
    {
        if (tokens.Count == 0)
        {
            return 0;
        }

        if (string.Equals(tokens[0].Value, "and", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tokens[0].Value, "then", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Count > 2 &&
                string.Equals(tokens[0].Value, "and", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(tokens[1].Value, "then", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            return 1;
        }

        return 0;
    }

    private static bool TryRemovePhrase(string text, string phrase, out string cleaned)
    {
        cleaned = text;
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return false;
        }

        var variants = GetPhraseVariants(phrase);
        foreach (var variant in variants)
        {
            if (TryRemoveSinglePhrase(cleaned, variant, out cleaned))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryRemoveSinglePhrase(string text, string phrase, out string cleaned)
    {
        cleaned = text;
        var phraseTokens = Tokenize(phrase);
        if (phraseTokens.Count == 0)
        {
            return false;
        }

        var textTokens = Tokenize(text);
        if (textTokens.Count < phraseTokens.Count)
        {
            return false;
        }

        for (var startIndex = 0; startIndex <= textTokens.Count - phraseTokens.Count; startIndex++)
        {
            if (!TokensMatchAt(textTokens, phraseTokens, startIndex))
            {
                continue;
            }

            cleaned = RemoveSpan(
                text,
                textTokens[startIndex].Start,
                textTokens[startIndex + phraseTokens.Count - 1].End);
            return true;
        }

        return false;
    }

    private static List<string> GetPhraseVariants(string phrase)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return results;
        }

        var trimmed = phrase.Trim();
        results.Add(trimmed);

        var normalized = trimmed.ToLowerInvariant();

        if (normalized == "thank you" || normalized == "thanks" || normalized == "thankyou" || normalized == "thank u")
        {
            AddIfNotPresent(results, "thank you");
            AddIfNotPresent(results, "thanks");
            AddIfNotPresent(results, "thankyou");
            AddIfNotPresent(results, "thank u");
        }
        else
        {
            if (normalized.Contains("thank you"))
            {
                AddIfNotPresent(results, ReplaceIgnoreCase(trimmed, "thank you", "thanks"));
                AddIfNotPresent(results, ReplaceIgnoreCase(trimmed, "thank you", "thankyou"));
                AddIfNotPresent(results, ReplaceIgnoreCase(trimmed, "thank you", "thank u"));
            }
            else if (normalized.Contains("thanks"))
            {
                AddIfNotPresent(results, ReplaceIgnoreCase(trimmed, "thanks", "thank you"));
                AddIfNotPresent(results, ReplaceIgnoreCase(trimmed, "thanks", "thankyou"));
            }
        }

        var currentCount = results.Count;
        for (var i = 0; i < currentCount; i++)
        {
            var item = results[i];
            var itemNorm = item.ToLowerInvariant();
            if (itemNorm.Contains("okay"))
            {
                AddIfNotPresent(results, ReplaceIgnoreCase(item, "okay", "ok"));
            }
            else if (itemNorm.Contains("ok"))
            {
                AddIfNotPresent(results, ReplaceIgnoreCase(item, "ok", "okay"));
            }
        }

        return results.OrderByDescending(p => Tokenize(p).Count).ThenByDescending(p => p.Length).ToList();
    }

    private static void AddIfNotPresent(List<string> list, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return;
        if (!list.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(candidate);
        }
    }

    private static string ReplaceIgnoreCase(string input, string search, string replacement)
    {
        var index = input.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return input;
        return input[..index] + replacement + input[(index + search.Length)..];
    }

    private static string RemoveSpan(string text, int removeStart, int removeEnd)
    {
        while (removeStart > 0 && char.IsWhiteSpace(text[removeStart - 1]))
        {
            removeStart--;
        }

        while (removeEnd < text.Length && (char.IsWhiteSpace(text[removeEnd]) || char.IsPunctuation(text[removeEnd])))
        {
            removeEnd++;
        }

        return text[..removeStart] + " " + text[removeEnd..];
    }

    private static List<TokenSpan> Tokenize(string text)
    {
        var tokens = new List<TokenSpan>();
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && !char.IsLetterOrDigit(text[index]))
            {
                index++;
            }

            if (index >= text.Length)
            {
                break;
            }

            var start = index;
            while (index < text.Length && char.IsLetterOrDigit(text[index]))
            {
                index++;
            }

            tokens.Add(new TokenSpan(text[start..index], start, index));
        }

        return tokens;
    }

    private static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var lastWasWhitespace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!lastWasWhitespace)
                {
                    builder.Append(' ');
                    lastWasWhitespace = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasWhitespace = false;
        }

        return builder.ToString();
    }

    private readonly record struct TokenSpan(string Value, int Start, int End);
}
