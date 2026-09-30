using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Core.Pipeline;

/// <summary>What to do with a microphone line that overlaps system audio: hide all of it, or keep it with the echoed words marked hidden, so the row shows only the user's own words.</summary>
public sealed record EchoVerdict(bool HideAll, TranscriptSegment? Trimmed);

/// <summary>
/// Decides whether a microphone line is only the microphone hearing the speakers, i.e. the same words as a
/// system-audio line at about the same time. Text-based on purpose: it works whatever the capture stack does, and
/// tolerates a clock skew between the two sides.
/// </summary>
public static class EchoMatcher
{
    /// <summary>How far apart in time the two lines may be and still count as the same speech.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(3);

    private const int MinWords = 3;
    private const double MinContainment = 0.7;

    public static bool Overlaps(TimeSpan startA, TimeSpan endA, TimeSpan startB, TimeSpan endB) =>
        startA < endB + Tolerance && startB < endA + Tolerance;

    /// <summary>True when most of the microphone words also appear in the system-audio text. Very short lines are never judged.</summary>
    public static bool Repeats(string microphoneText, string systemText)
    {
        var mic = Words(microphoneText);
        if (mic.Count < MinWords)
        {
            return false;
        }

        var pool = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var word in Words(systemText))
        {
            pool[word] = pool.GetValueOrDefault(word) + 1;
        }

        var matched = 0;
        foreach (var word in mic)
        {
            if (pool.TryGetValue(word, out var left) && left > 0)
            {
                pool[word] = left - 1;
                matched++;
            }
        }

        return matched >= MinContainment * mic.Count;
    }

    /// <summary>An echoed stretch must be at least this many words in a row; single matching words ("yeah", "the") are the user's own.</summary>
    private const int MinRun = 3;

    private const double AlmostAllEcho = 0.8;

    /// <summary>
    /// Word-level echo removal. The microphone often picks up the tail of what the speakers said and then the user's own
    /// words in the same line ("as far as your credits go, yeah now totally"). Words that continue a run of at least
    /// <see cref="MinRun"/> matching words in an overlapping system line are dropped; the rest stay as the microphone
    /// line, which then starts at its first kept word. Nothing is deleted. Returns null when the line should stay untouched: no overlap, no
    /// echo, or the user already edited it.
    /// </summary>
    public static EchoVerdict? Judge(TranscriptSegment mic, IEnumerable<TranscriptSegment> system)
    {
        if (mic.EditedText is not null || mic.HasHiddenWords)
        {
            return null;
        }

        var overlapping = system.Where(s => s.DisplayText.Length > 0 && Overlaps(mic.Start, mic.End, s.Start, s.End)).ToList();
        if (overlapping.Count == 0)
        {
            return null;
        }

        var words = mic.Words;
        if (words is null || words.Count == 0)
        {
            return overlapping.Any(s => Repeats(mic.RawText, s.RawText)) ? new EchoVerdict(true, null) : null;
        }

        var pool = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var word in overlapping.SelectMany(s => Words(s.RawText)))
        {
            pool[word] = pool.GetValueOrDefault(word) + 1;
        }

        var matched = new bool[words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            var normal = Words(words[i].Text).FirstOrDefault();
            if (normal is not null && pool.TryGetValue(normal, out var left) && left > 0)
            {
                pool[normal] = left - 1;
                matched[i] = true;
            }
        }

        var echo = new bool[words.Count];
        for (var i = 0; i < words.Count;)
        {
            if (!matched[i])
            {
                i++;
                continue;
            }

            var end = i;
            while (end < words.Count && matched[end])
            {
                end++;
            }

            if (end - i >= MinRun)
            {
                Array.Fill(echo, true, i, end - i);
            }

            i = end;
        }

        var kept = words.Where((_, i) => !echo[i]).ToList();
        if (kept.Count == words.Count)
        {
            // No run of matching words, but a line that is mostly the same words scattered is still an echo.
            return overlapping.Any(s => Repeats(mic.RawText, s.RawText)) ? new EchoVerdict(true, null) : null;
        }

        // Nothing of the user's own is left, or the line is so nearly all echo that the rest is misheard fragments.
        if (kept.Count == 0 || (words.Count - kept.Count) >= AlmostAllEcho * words.Count)
        {
            return new EchoVerdict(true, null);
        }

        // The recognized text and every word stay stored; the echo words are only marked hidden.
        return new EchoVerdict(false, mic with
        {
            Words = words.Select((w, i) => echo[i] ? w with { Hidden = true } : w).ToList(),
            Revision = mic.Revision + 1
        });
    }

    private static List<string> Words(string text) =>
        text.ToLowerInvariant()
            .Split([' ', '\t', '\n', ',', '.', '?', '!', ';', ':', '"', '(', ')', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('\''))
            .Where(w => w.Length > 0)
            .ToList();
}
