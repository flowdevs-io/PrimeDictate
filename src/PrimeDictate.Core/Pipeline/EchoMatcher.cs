namespace PrimeDictate.Core.Pipeline;

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

    private static List<string> Words(string text) =>
        text.ToLowerInvariant()
            .Split([' ', '\t', '\n', ',', '.', '?', '!', ';', ':', '"', '(', ')', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('\''))
            .Where(w => w.Length > 0)
            .ToList();
}
