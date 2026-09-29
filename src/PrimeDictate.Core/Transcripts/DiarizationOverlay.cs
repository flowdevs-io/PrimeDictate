using System.Globalization;
using System.Text.Json;

namespace PrimeDictate.Core.Transcripts;

/// <summary>One stretch of speech by one diarizer speaker. Segments of different speakers may overlap.</summary>
public sealed record DiarizationSegment(string Speaker, double Start, double End);

/// <summary>A timeline bar: a stretch where a transcript speaker was talking, possibly at the same time as another.</summary>
public sealed record OverlayBar(string SpeakerId, double Start, double End);

/// <summary>
/// The diarizer's own view of the system audio, run over the whole recording after Stop. Live rows carry one
/// speaker per word, so overlapping speech is lost there; this keeps it and is used only to draw the timeline.
/// </summary>
public sealed record DiarizationOverlay(IReadOnlyList<DiarizationSegment> Segments)
{
    public const string FileName = "system-diarization.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Total seconds during which two or more diarizer speakers are active at once.</summary>
    public double OverlapSeconds
    {
        get
        {
            var edges = this.Segments.SelectMany(s => new[] { (Time: s.Start, Delta: 1), (Time: s.End, Delta: -1) })
                .OrderBy(e => e.Time).ThenBy(e => e.Delta).ToList();
            double total = 0, last = 0;
            var active = 0;
            foreach (var edge in edges)
            {
                if (active >= 2)
                {
                    total += edge.Time - last;
                }

                active += edge.Delta;
                last = edge.Time;
            }

            return total;
        }
    }

    public int SpeakerCount => this.Segments.Select(s => s.Speaker).Distinct().Count();

    public static DiarizationOverlay? TryLoad(string mediaDirectory)
    {
        var path = Path.Combine(mediaDirectory, FileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<DiarizationOverlay>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string mediaDirectory) =>
        File.WriteAllText(Path.Combine(mediaDirectory, FileName), JsonSerializer.Serialize(this, JsonOptions));

    /// <summary>Parses RTTM: <c>SPEAKER file 1 start duration &lt;NA&gt; &lt;NA&gt; speaker &lt;NA&gt; &lt;NA&gt;</c>. Other lines are ignored.</summary>
    public static IReadOnlyList<DiarizationSegment> ParseRttm(string text)
    {
        var result = new List<DiarizationSegment>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 8 || parts[0] != "SPEAKER"
                || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
                || !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
                || duration <= 0)
            {
                continue;
            }

            result.Add(new DiarizationSegment(parts[7], start, start + duration));
        }

        return result.OrderBy(s => s.Start).ToList();
    }

    /// <summary>
    /// Assigns each diarizer speaker to the transcript speaker whose lines overlap it most in time, and returns
    /// bars in transcript speaker terms. Speakers that match no line are dropped. The microphone side is excluded
    /// because the diarizer only saw the system audio.
    /// </summary>
    public IReadOnlyList<OverlayBar> MapTo(TranscriptDocument document)
    {
        var remote = document.ActiveSegments
            .SelectMany(s => s.Speakers)
            .Select(a => (Speaker: document.ResolveSpeakerId(a.SpeakerId), Start: a.Start.TotalSeconds, End: a.End.TotalSeconds))
            .Where(a => a.Speaker != "local")
            .ToList();

        var owner = new Dictionary<string, string>();
        foreach (var group in this.Segments.GroupBy(s => s.Speaker))
        {
            var best = remote
                .GroupBy(r => r.Speaker)
                .Select(g => (Speaker: g.Key, Overlap: g.Sum(r => group.Sum(d => Math.Max(0, Math.Min(r.End, d.End) - Math.Max(r.Start, d.Start))))))
                .OrderByDescending(x => x.Overlap)
                .FirstOrDefault();
            if (best.Speaker is not null && best.Overlap > 0)
            {
                owner[group.Key] = best.Speaker;
            }
        }

        return this.Segments
            .Where(s => owner.ContainsKey(s.Speaker))
            .Select(s => new OverlayBar(owner[s.Speaker], s.Start, s.End))
            .ToList();
    }
}
