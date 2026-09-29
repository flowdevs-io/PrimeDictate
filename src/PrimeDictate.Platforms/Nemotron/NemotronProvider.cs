using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Platforms.Nemotron;

/// <summary>
/// Nemotron ASR (and combined speaker detection when the worker loaded the diarizer) through the
/// worker's file endpoint, one bounded window per request. Word timings come from the model.
/// Speaker numbers are consistent inside one window only; the worker gives no cross-window identity.
/// </summary>
public sealed class NemotronProvider(INemotronEndpoint worker, string modelId, bool identifySpeakers = true) : ITranscriptionProvider
{
    /// <summary>The worker rejects uploads over 1 MB with a bare 413. 30 s of 16 kHz PCM16 is about 960 KB.</summary>
    public const int MaxWindowSamples = 30 * 16_000;

    public string ModelId => modelId;

    public TranscriptionProviderCapabilities Capabilities { get; } = new(
        SupportsFiles: true,
        LiveMode: LiveRecognitionMode.NativeStreaming,
        Timing: TimingCapabilities.SegmentTimestamps | TimingCapabilities.WordTimestamps | TimingCapabilities.Confidence,
        CombinedDiarization: worker.HasDiarizer && identifySpeakers,
        MaxSpeakers: worker.HasDiarizer ? 8 : null,
        MaxWindow: TimeSpan.FromSeconds(30),
        Languages: ["auto"],
        RequiredSampleRate: 16_000);

    public ProviderModelInfo ModelInfo
    {
        get
        {
            var asr = NemotronPins.All.FirstOrDefault(p => !p.IsDiarizer && $"nemotron:{p.Id}" == modelId);
            var diar = worker.HasDiarizer && identifySpeakers ? NemotronPins.Diarizer : null;
            return new ProviderModelInfo(
                asr is null ? null : $"{asr.FileName} sha256:{asr.Sha256Prefix}",
                diar?.Id,
                diar is null ? null : $"{diar.FileName} sha256:{diar.Sha256Prefix}");
        }
    }

    public EffectiveRuntime Runtime { get; } = new("nemo-speech", NemotronPins.RuntimeCommit[..8], worker.RequestedBackend, worker.EffectiveBackend, worker.FallbackReason);

    public async ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken)
    {
        if (samples.Length == 0)
        {
            return [];
        }

        if (samples.Length > MaxWindowSamples)
        {
            throw new ArgumentException("The window is longer than the worker accepts; split it first.", nameof(samples));
        }

        var wav = WavEncoder.EncodePcm16Mono(samples.Span);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "window.wav");
        form.Add(new StringContent("verbose_json"), "response_format");
        var diarize = worker.HasDiarizer && identifySpeakers;
        if (diarize)
        {
            // Asking without a loaded diarizer is an error (HTTP 400 here; on the socket it silently breaks the stream).
            form.Add(new StringContent("true"), "diarization");
        }

        if (!string.IsNullOrWhiteSpace(language) && language != "auto")
        {
            form.Add(new StringContent(language), "language");
        }

        using var response = await worker.Client.PostAsync("v1/audio/transcriptions", form, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            throw new NemotronException("worker-http", "The worker refused the audio window as too large.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new NemotronException("worker-http", $"The worker returned HTTP {(int)response.StatusCode}: {ErrorMessage(body)}");
        }

        return NemotronResponseParser.ParseVerboseJson(body, TimeSpan.FromSeconds(samples.Length / 16_000d), diarize);
    }

    public async ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken)
    {
        // The worker accepts a speaker request without a diarizer and then errors on every audio frame, so never send it.
        var withSpeakers = diarize && worker.HasDiarizer;
        return await NemotronRealtimeSession.ConnectAsync(worker.BaseAddress, worker.ApiKey, withSpeakers, cancellationToken, language: language).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "unknown error";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "unreadable error response";
        }
    }
}

public static class NemotronResponseParser
{
    /// <summary>
    /// Turns a <c>verbose_json</c> response into segments. With speakers, consecutive words by the same
    /// speaker form one segment. Without, the whole window is one segment. Word times are seconds from window start.
    /// </summary>
    public static IReadOnlyList<RecognizedSegment> ParseVerboseJson(string json, TimeSpan windowDuration, bool withSpeakers)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseVerboseJson(doc.RootElement, windowDuration, withSpeakers, null);
    }

    /// <summary>Same as the string overload for an already parsed object; <paramref name="fallbackText"/> is used when the object has no <c>text</c> and no words (realtime events call it <c>transcript</c>).</summary>
    public static IReadOnlyList<RecognizedSegment> ParseVerboseJson(JsonElement root, TimeSpan windowDuration, bool withSpeakers, string? fallbackText)
    {
        var words = new List<(string Text, TimeSpan Start, TimeSpan End, double? Confidence, int? Speaker)>();
        if (root.TryGetProperty("words", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in list.EnumerateArray())
            {
                var text = w.TryGetProperty("word", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                words.Add((
                    text.Trim(),
                    Seconds(w, "start"),
                    Seconds(w, "end"),
                    w.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var conf) ? conf : null,
                    withSpeakers && w.TryGetProperty("speaker", out var s) && s.TryGetInt32(out var spk) ? spk : null));
            }
        }

        if (words.Count == 0)
        {
            var text = (root.TryGetProperty("text", out var t) ? t.GetString() : fallbackText)?.Trim();
            return string.IsNullOrEmpty(text)
                ? []
                : [new RecognizedSegment(TimeSpan.Zero, windowDuration, text, null, null, null, TimingProvenance.ApproximateChunk)];
        }

        var segments = new List<RecognizedSegment>();
        var group = new List<(string Text, TimeSpan Start, TimeSpan End, double? Confidence, int? Speaker)>();

        void Flush()
        {
            if (group.Count == 0)
            {
                return;
            }

            var speaker = group[0].Speaker;
            segments.Add(new RecognizedSegment(
                group[0].Start,
                group[^1].End,
                string.Join(' ', group.Select(g => g.Text)),
                group.Select(g => new WordTiming(g.Text, g.Start, g.End, g.Confidence, TimingProvenance.Model, g.Speaker is null ? null : $"speaker-{g.Speaker}")).ToList(),
                group.Where(g => g.Confidence is not null).Select(g => g.Confidence!.Value).DefaultIfEmpty(double.NaN).Average() is var avg && !double.IsNaN(avg) ? avg : null,
                speaker is null ? null : $"speaker-{speaker}",
                TimingProvenance.Model));
            group.Clear();
        }

        foreach (var word in words)
        {
            if (group.Count > 0 && group[0].Speaker != word.Speaker)
            {
                Flush();
            }

            group.Add(word);
        }

        Flush();
        return segments;
    }

    private static TimeSpan Seconds(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetDouble(out var s) ? TimeSpan.FromSeconds(Math.Max(0, s)) : TimeSpan.Zero;
}
