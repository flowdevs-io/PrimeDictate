namespace PrimeDictate.Core.Dictation;

/// <summary>
/// Whisper's greedy decoder can get stuck repeating one word or short phrase ("like, like, like, ...") until the window runs out,
/// dropping the speech that followed. Finds such runs and collapses them to a single occurrence. Words compare without case or punctuation.
/// </summary>
public static class RepetitionLoops
{
    private const int MaxPhraseWords = 4;

    /// <summary>A loop is a word 8 times in a row, a two-word phrase 5 times, or a longer phrase 4 times: people repeat themselves ("bye bye bye bye"), Whisper loops ran 7 to 25 repeats.</summary>
    private static int MinRepeats(int phraseWords) => phraseWords switch { 1 => 8, 2 => 5, _ => 4 };

    /// <summary>Words inside loops beyond their first occurrence; 0 when the text has no loop.</summary>
    public static int LoopedWordCount(string text)
    {
        var total = 0;
        foreach (var (_, phrase, repeats) in Find(Words(text)))
        {
            total += phrase * (repeats - 1);
        }

        return total;
    }

    public static bool HasLoop(string text) => LoopedWordCount(text) > 0;

    /// <summary>Keeps the first occurrence of each loop and drops the repeats. Text without a loop comes back unchanged.</summary>
    public static string Collapse(string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var loops = Find(tokens);
        if (loops.Count == 0)
        {
            return text;
        }

        var kept = new List<string>(tokens.Length);
        var next = 0;
        foreach (var (start, phrase, repeats) in loops)
        {
            kept.AddRange(tokens[next..(start + phrase)]);
            next = start + (phrase * repeats);

            // Carry a sentence end from the last repeat ("like, like, like." becomes "like.").
            var last = tokens[next - 1];
            if (last.Length > 0 && last[^1] is '.' or '?' or '!')
            {
                kept[^1] = kept[^1].TrimEnd(',', ';', ':', '.', '?', '!') + last[^1];
            }
        }

        kept.AddRange(tokens[next..]);
        return string.Join(' ', kept);
    }

    /// <summary>Non-overlapping loops, left to right, each as (first token, phrase length in words, repeats).</summary>
    private static List<(int Start, int Phrase, int Repeats)> Find(IReadOnlyList<string> tokens)
    {
        var words = tokens.Select(Normalize).ToArray();
        var loops = new List<(int, int, int)>();
        var i = 0;
        while (i < words.Length)
        {
            var best = (Phrase: 0, Repeats: 0);
            for (var phrase = 1; phrase <= MaxPhraseWords && i + (phrase * 2) <= words.Length; phrase++)
            {
                if (words[i].Length == 0)
                {
                    break;
                }

                var repeats = 1;
                while (i + ((repeats + 1) * phrase) <= words.Length && SamePhrase(words, i, i + (repeats * phrase), phrase))
                {
                    repeats++;
                }

                if (repeats >= MinRepeats(phrase) && phrase * repeats > best.Phrase * best.Repeats)
                {
                    best = (phrase, repeats);
                }
            }

            if (best.Phrase > 0)
            {
                loops.Add((i, best.Phrase, best.Repeats));
                i += best.Phrase * best.Repeats;
            }
            else
            {
                i++;
            }
        }

        return loops;
    }

    private static bool SamePhrase(string[] words, int a, int b, int length)
    {
        for (var k = 0; k < length; k++)
        {
            if (words[a + k] != words[b + k])
            {
                return false;
            }
        }

        return true;
    }

    private static string[] Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string Normalize(string token) =>
        new string(token.Where(c => char.IsLetterOrDigit(c) || c == '\'').ToArray()).ToLowerInvariant();
}
