using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Core.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 29, 18, 0, 0, TimeSpan.Zero);

    public static TranscriptDocument NewDocument(Guid? id = null) => new()
    {
        SessionId = id ?? Guid.NewGuid(),
        Title = "Weekly sync",
        SourceType = TranscriptSourceType.ImportedFile,
        CreatedAt = Now,
        UpdatedAt = Now,
        Status = TranscriptSessionStatus.Created
    };

    public static RecognitionRunInfo Run(int version = 1, string model = "whisper-onnx/base.en") => new(
        version, model, "rev1", null, null, "en", "sherpa-onnx", "1.13.0", "cpu", "cpu", Now);

    public static TranscriptSegment Segment(
        string id,
        string text,
        double start,
        double end,
        long revision = 1,
        SegmentState state = SegmentState.Final,
        int resultVersion = 1) => new()
    {
        Id = id,
        Start = TimeSpan.FromSeconds(start),
        End = TimeSpan.FromSeconds(end),
        RawText = text,
        Revision = revision,
        State = state,
        ResultVersion = resultVersion,
        TimingProvenance = TimingProvenance.ApproximateChunk
    };
}
