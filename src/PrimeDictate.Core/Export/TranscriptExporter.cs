using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Core.Export;

public enum ExportFormat
{
    Text = 0,
    Markdown = 1,
    Json = 2,
    Srt = 3,
    WebVtt = 4
}

public enum ExportTextSource
{
    /// <summary>User-edited text where present, otherwise recognizer output.</summary>
    Edited = 0,

    /// <summary>Recognizer output only.</summary>
    Raw = 1
}

public sealed record ExportOptions(ExportFormat Format, ExportTextSource Text = ExportTextSource.Edited, bool IncludeTimestamps = true);

/// <summary>
/// Exports the active result of a transcript. Cue timing comes from segment timing (or word timing
/// when a segment is split); nothing is spread evenly across words.
/// </summary>
public static class TranscriptExporter
{
    public static string FileExtension(ExportFormat format) => format switch
    {
        ExportFormat.Text => ".txt",
        ExportFormat.Markdown => ".md",
        ExportFormat.Json => ".json",
        ExportFormat.Srt => ".srt",
        ExportFormat.WebVtt => ".vtt",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public static string Export(TranscriptDocument document, ExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        // A line whose text was edited to nothing (for example when a merged turn was rewritten as one block) is not a cue.
        var segments = document.ActiveSegments
            .Where(s => s.State == SegmentState.Final || document.Status != TranscriptSessionStatus.Completed)
            .Where(s => options.Format == ExportFormat.Json || Text(s, options.Text).Trim().Length > 0)
            .ToList();
        return options.Format switch
        {
            ExportFormat.Text => ToText(document, segments, options),
            ExportFormat.Markdown => ToMarkdown(document, segments, options),
            ExportFormat.Json => ToJson(document),
            ExportFormat.Srt => ToCues(document, segments, options, srt: true),
            ExportFormat.WebVtt => ToCues(document, segments, options, srt: false),
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
    }

    public static string FormatTimecode(TimeSpan time, bool srt)
    {
        var total = (long)Math.Max(0, Math.Round(time.TotalMilliseconds));
        var ms = total % 1000;
        var seconds = total / 1000 % 60;
        var minutes = total / 60_000 % 60;
        var hours = total / 3_600_000;
        return string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{minutes:00}:{seconds:00}{(srt ? ',' : '.')}{ms:000}");
    }

    private static string Text(TranscriptSegment segment, ExportTextSource source) =>
        source == ExportTextSource.Raw ? segment.RawText : segment.DisplayText;

    private static string SpeakerName(TranscriptDocument document, TranscriptSegment segment)
    {
        var id = segment.Speakers.Count > 0 ? document.ResolveSpeakerId(segment.Speakers[0].SpeakerId) : null;
        return id is null ? string.Empty : document.Speakers.FirstOrDefault(s => s.Id == id)?.Name ?? id;
    }

    private static string ToText(TranscriptDocument document, List<TranscriptSegment> segments, ExportOptions options)
    {
        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            var speaker = SpeakerName(document, segment);
            if (options.IncludeTimestamps)
            {
                sb.Append('[').Append(FormatTimecode(segment.DisplayStart, srt: false)).Append("] ");
            }

            if (speaker.Length > 0)
            {
                sb.Append(speaker).Append(": ");
            }

            sb.Append(Text(segment, options.Text).Trim()).Append('\n');
        }

        return sb.ToString();
    }

    private static string ToMarkdown(TranscriptDocument document, List<TranscriptSegment> segments, ExportOptions options)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(EscapeMarkdown(document.Title)).Append("\n\n");
        var run = document.Runs.FirstOrDefault(r => r.ResultVersion == document.ActiveResultVersion);
        if (run is not null)
        {
            sb.Append("_Model: ").Append(EscapeMarkdown(run.AsrModelId)).Append(" · ")
                .Append(document.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("_\n\n");
        }

        foreach (var segment in segments)
        {
            var speaker = SpeakerName(document, segment);
            if (speaker.Length > 0)
            {
                sb.Append("**").Append(EscapeMarkdown(speaker)).Append(":** ");
            }

            if (options.IncludeTimestamps)
            {
                sb.Append('`').Append(FormatTimecode(segment.DisplayStart, srt: false)).Append("` ");
            }

            sb.Append(EscapeMarkdown(Text(segment, options.Text).Trim())).Append("\n\n");
        }

        return sb.ToString();
    }

    private static string EscapeMarkdown(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ReplaceLineEndings(" "))
        {
            if ("\\`*_{}[]<>()#+-.!|~".Contains(c, StringComparison.Ordinal))
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string ToCues(TranscriptDocument document, List<TranscriptSegment> segments, ExportOptions options, bool srt)
    {
        var sb = new StringBuilder();
        if (!srt)
        {
            sb.Append("WEBVTT\n\n");
        }

        var index = 1;
        foreach (var segment in segments)
        {
            var text = SanitizeCueText(Text(segment, options.Text), srt);
            if (text.Length == 0)
            {
                continue;
            }

            var start = segment.DisplayStart;
            var end = segment.DisplayEnd > start ? segment.DisplayEnd : start + TimeSpan.FromMilliseconds(500);
            var speaker = SpeakerName(document, segment);
            if (speaker.Length > 0)
            {
                text = srt ? $"{speaker}: {text}" : $"<v {SanitizeCueText(speaker, srt: false)}>{text}";
            }

            if (srt)
            {
                sb.Append(index.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            sb.Append(FormatTimecode(start, srt)).Append(" --> ").Append(FormatTimecode(end, srt)).Append('\n')
                .Append(text).Append("\n\n");
            index++;
        }

        return sb.ToString();
    }

    /// <summary>Cue text cannot contain blank lines, and WebVTT must not contain "--&gt;" or markup.</summary>
    private static string SanitizeCueText(string text, bool srt)
    {
        var lines = text.Trim().Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0);
        var joined = string.Join("\n", lines);
        joined = joined.Replace("-->", "->", StringComparison.Ordinal);
        if (!srt)
        {
            joined = joined.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);
        }

        return joined;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string ToJson(TranscriptDocument document)
    {
        var export = new
        {
            format = "primedictate.transcript",
            formatVersion = 1,
            document.SchemaVersion,
            document.SessionId,
            document.Title,
            document.SourceType,
            document.CreatedAt,
            document.UpdatedAt,
            document.Status,
            durationSeconds = document.Duration?.TotalSeconds,
            document.Language,
            document.Media,
            document.Runs,
            document.ActiveResultVersion,
            document.Speakers,
            segments = document.Segments.OrderBy(s => s.ResultVersion).ThenBy(s => s.Start).Select(s => new
            {
                s.Id,
                s.ResultVersion,
                startSeconds = s.Start.TotalSeconds,
                endSeconds = s.End.TotalSeconds,
                s.RawText,
                s.EditedText,
                s.State,
                s.Revision,
                s.Confidence,
                s.TimingProvenance,
                words = s.Words?.Select(w => new { w.Text, startSeconds = w.Start.TotalSeconds, endSeconds = w.End.TotalSeconds, w.Confidence, w.Provenance }),
                s.Speakers
            })
        };
        return JsonSerializer.Serialize(export, JsonOptions);
    }
}
