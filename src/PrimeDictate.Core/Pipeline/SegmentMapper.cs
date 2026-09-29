using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Core.Pipeline;

/// <summary>Turns provider output on a window into session segments on the session timeline.</summary>
public static class SegmentMapper
{
    public static IEnumerable<TranscriptSegment> Map(
        IEnumerable<RecognizedSegment> recognized,
        string idPrefix,
        long windowStartSample,
        TimeSpan windowDuration,
        int resultVersion,
        long revision,
        SegmentState state)
    {
        var offset = AudioFormat.SpeechTimeline.ToTime(windowStartSample);
        var n = 0;
        foreach (var r in recognized)
        {
            var text = r.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var start = offset + Clamp(r.Start, TimeSpan.Zero, windowDuration);
            var end = offset + Clamp(r.End, r.Start, windowDuration);
            yield return new TranscriptSegment
            {
                Id = $"{idPrefix}.{n++}",
                Start = start,
                End = end < start ? start : end,
                RawText = text,
                State = state,
                Revision = revision,
                Confidence = r.Confidence,
                TimingProvenance = r.Provenance,
                ResultVersion = resultVersion,
                Words = r.Words?.Select(w => w with { Start = offset + w.Start, End = offset + w.End }).ToList()
            };
        }
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}
