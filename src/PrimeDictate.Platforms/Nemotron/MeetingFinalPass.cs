using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Coordination;
using PrimeDictate.Core.Pipeline;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Platforms.Nemotron;

/// <summary>What the final pass produced, for the note on the session and the timeline.</summary>
public sealed record FinalPassResult(int ResultVersion, int MicrophoneLines, int SystemLines, int SpeakerCount, double OverlapSeconds, DiarizationOverlay? Overlay, string? DiarizerProblem, string? ChannelReport = null)
{
    /// <summary>One sentence for the status line. Zero speakers is explained: nobody spoke on the system side, or the diarizer failed.</summary>
    public string Describe()
    {
        if (this.DiarizerProblem is { } problem)
        {
            return $"Final transcript ready, but speakers could not be told apart: {problem}";
        }

        if (this.SystemLines == 0)
        {
            return this.Overlay is { SpeakerCount: > 0 } o
                ? $"Final transcript ready. The diarizer found only {o.SpeakerCount} speaker{(o.SpeakerCount == 1 ? string.Empty : "s")} and {o.SpeechSeconds:0.#} s of speech on the system audio, and no words were recognized there."
                : "Final transcript ready. Nothing was recognized on the system audio (no one spoke there, or it was silent).";
        }

        return $"Final transcript ready: {this.SpeakerCount} speakers on the system audio, {this.OverlapSeconds:0.#} s of overlapping speech.";
    }
}

/// <summary>
/// The second pass of a two-pass meeting. The live pass (any fast model) gave a draft while people talked. After Stop this
/// reads the saved stereo recording, one channel at a time: the microphone channel becomes "You", the system-audio channel
/// is transcribed and each word takes the speaker the whole-recording diarizer heard at that moment. Speaker numbers
/// therefore mean the same person across the whole meeting, which per-window diarization cannot promise. Nothing is
/// written until everything has succeeded, so a failure leaves the draft untouched; the draft stays stored as the
/// earlier result.
/// </summary>
public sealed class MeetingFinalPass(ModelLeaseScheduler scheduler)
{
    /// <param name="asr">Recognizes one window at a time; needs word times. Its own diarization is not used.</param>
    /// <param name="diarize">Whole-recording diarization of the system channel (or null and a reason).</param>
    public async Task<FinalPassResult> RunAsync(
        SessionDocumentHost host,
        string stereoWavPath,
        ITranscriptionProvider asr,
        Func<CancellationToken, Task<(DiarizationOverlay? Overlay, string? Error)>> diarize,
        string? language,
        string mediaDirectory,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var sessionId = host.Document.SessionId;
        var resultVersion = host.Document.Runs.Count == 0 ? 1 : host.Document.Runs.Max(r => r.ResultVersion) + 1;
        var (overlay, diarizerProblem) = await diarize(cancellationToken).ConfigureAwait(false);
        var diar = overlay?.Segments ?? [];

        var max = asr.Capabilities.MaxWindow ?? TimeSpan.FromSeconds(28);
        var chunkOptions = new SpeechChunkerOptions { MaxChunk = max - TimeSpan.FromSeconds(2) < TimeSpan.FromSeconds(5) ? max : max - TimeSpan.FromSeconds(2) };
        var chunkers = new[] { new SpeechChunker(chunkOptions), new SpeechChunker(chunkOptions) };
        var counters = new[] { 0, 0 };
        var mic = new List<TranscriptSegment>();
        var system = new List<TranscriptSegment>();
        var total = host.Document.Duration ?? TimeSpan.Zero;
        // Per channel: samples, sum of squares, peak, windows cut, windows sent to the recognizer, words back.
        var stats = new (long Samples, double Squares, float Peak, int Windows, int Sent, int Words)[2];

        async Task RecognizeAsync(int channel, IReadOnlyList<AudioChunk> chunks)
        {
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = counters[channel]++;
                var st = stats[channel];
                st.Windows++;
                foreach (var v in chunk.Samples)
                {
                    st.Squares += v * v;
                    st.Peak = Math.Max(st.Peak, Math.Abs(v));
                }

                st.Samples += chunk.Samples.Length;
                stats[channel] = st;
                if (!chunk.ContainsSpeech)
                {
                    continue;
                }

                IReadOnlyList<RecognizedSegment> recognized;
                using (await scheduler.AcquireAsync(asr.ModelId, ModelLeasePriority.Background, cancellationToken).ConfigureAwait(false))
                {
                    recognized = await asr.RecognizeWindowAsync(chunk.Samples, language, cancellationToken).ConfigureAwait(false);
                }

                stats[channel].Sent++;
                stats[channel].Words += recognized.Sum(r => r.Words?.Count ?? r.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
                if (channel == 0)
                {
                    var forced = recognized.Select(r => r with { SpeakerLabel = "local", Words = r.Words?.Select(w => w with { SpeakerId = "local" }).ToList() });
                    mic.AddRange(SegmentMapper.Map(forced, $"fm{index}", chunk.StartSample, chunk.Duration, resultVersion, 1, SegmentState.Final));
                }
                else
                {
                    var mapped = SegmentMapper.Map(recognized, $"fs{index}", chunk.StartSample, chunk.Duration, resultVersion, 1, SegmentState.Final).ToList();
                    for (var k = 0; k < mapped.Count; k++)
                    {
                        system.AddRange(SplitBySpeaker(mapped[k], $"fs{index}.{k}", diar, resultVersion));
                    }
                }

                if (total > TimeSpan.Zero)
                {
                    progress?.Report(Math.Clamp((chunk.Start + chunk.Duration) / total, 0, 1));
                }
            }
        }

        await foreach (var frame in new WavAudioDecoder().DecodeAsync(stereoWavPath, 0, cancellationToken).ConfigureAwait(false))
        {
            if (frame.Format.Channels < 2)
            {
                throw new MediaDecodeException("not-stereo", "The recording has no separate microphone and system channels.");
            }

            for (var channel = 0; channel < 2; channel++)
            {
                var samples = AudioConversion.SelectChannel(frame.Samples.Span, frame.Format.Channels, channel);
                await RecognizeAsync(channel, chunkers[channel].Add(samples)).ConfigureAwait(false);
            }

            total = frame.End > total ? frame.End : total;
        }

        for (var channel = 0; channel < 2; channel++)
        {
            await RecognizeAsync(channel, chunkers[channel].Flush()).ConfigureAwait(false);
        }

        // Everything worked: place the new result in one go, so the draft stays on screen until the final rows replace it.
        var run = new RecognitionRunInfo(
            resultVersion,
            asr.ModelId,
            asr.ModelInfo.AsrRevision,
            overlay is null ? null : NemotronPins.Diarizer.Id,
            overlay is null ? null : $"{NemotronPins.Diarizer.FileName} sha256:{NemotronPins.Diarizer.Sha256Prefix}",
            language,
            asr.Runtime.RuntimeName,
            asr.Runtime.RuntimeVersion,
            asr.Runtime.RequestedBackend,
            asr.Runtime.EffectiveBackend,
            DateTimeOffset.UtcNow);
        host.Apply(new SessionStarted(sessionId, run));
        var all = mic.Concat(system).ToList();
        host.EnsureSpeakers(all);
        foreach (var segment in all)
        {
            host.Apply(new SegmentFinalized(sessionId, segment));
        }

        // The microphone hears the speakers, so the same clean-up as in the live view applies to the You lines.
        foreach (var line in mic)
        {
            if (EchoMatcher.Judge(line, system) is { } verdict)
            {
                if (verdict.HideAll)
                {
                    host.Edit(line.Id, string.Empty);
                }
                else
                {
                    host.Apply(new SegmentFinalized(sessionId, verdict.Trimmed!));
                }
            }
        }

        host.SetStatus(TranscriptSessionStatus.Finalizing);
        host.Apply(new SessionCompleted(sessionId, total));
        if (overlay is not null)
        {
            overlay.Save(mediaDirectory);
        }

        var speakers = system.SelectMany(s => s.Speakers).Select(a => a.SpeakerId).Distinct().Count();
        string Report(string name, int c) => stats[c].Samples == 0
            ? $"{name}: no audio"
            : $"{name}: rms {Math.Sqrt(stats[c].Squares / stats[c].Samples) * 32768:0}, peak {stats[c].Peak * 32768:0}, {stats[c].Sent} of {stats[c].Windows} windows sent, {stats[c].Words} words";
        return new FinalPassResult(resultVersion, mic.Count, system.Count, speakers, overlay?.OverlapSeconds ?? 0, overlay, diarizerProblem, $"{Report("microphone", 0)}; {Report("system", 1)}");
    }

    /// <summary>
    /// Gives each word the diarizer speaker that covers most of it, repairs one-word flickers, and cuts the segment where
    /// the speaker changes. Without any diarizer segments everything is "remote".
    /// </summary>
    internal static IEnumerable<TranscriptSegment> SplitBySpeaker(TranscriptSegment segment, string idPrefix, IReadOnlyList<DiarizationSegment> diar, int resultVersion)
    {
        if (segment.Words is not { Count: > 0 } words)
        {
            var only = SpeakerFor(segment.Start, segment.End, diar);
            yield return segment with
            {
                Id = idPrefix,
                Speakers = [new SpeakerAttribution(only is null ? "remote" : $"speaker-{only}", segment.Start, segment.End, null)]
            };
            yield break;
        }

        var tuples = words.Select(w => (w.Text, w.Start, w.End, w.Confidence, Speaker: SpeakerFor(w.Start, w.End, diar))).ToList();
        NemotronResponseParser.SmoothSpeakers(tuples);
        // Words nobody claimed take the speaker before them, or the one after.
        for (var i = 0; i < tuples.Count; i++)
        {
            if (tuples[i].Speaker is null)
            {
                tuples[i] = (tuples[i].Text, tuples[i].Start, tuples[i].End, tuples[i].Confidence, i > 0 ? tuples[i - 1].Speaker : null);
            }
        }

        for (var i = tuples.Count - 1; i >= 0; i--)
        {
            if (tuples[i].Speaker is null && i + 1 < tuples.Count)
            {
                tuples[i] = (tuples[i].Text, tuples[i].Start, tuples[i].End, tuples[i].Confidence, tuples[i + 1].Speaker);
            }
        }

        var run = new List<int>();
        var k = 0;
        IEnumerable<TranscriptSegment> Flush()
        {
            if (run.Count == 0)
            {
                yield break;
            }

            var speaker = tuples[run[0]].Speaker;
            var label = speaker is null ? "remote" : $"speaker-{speaker}";
            var runWords = run.Select(i => words[i] with { SpeakerId = label }).ToList();
            var start = runWords[0].Start;
            var end = runWords[^1].End < start ? start : runWords[^1].End;
            yield return segment with
            {
                Id = $"{idPrefix}.{k++}",
                Start = start,
                End = end,
                RawText = string.Join(' ', runWords.Select(w => w.Text)),
                Words = runWords,
                Speakers = [new SpeakerAttribution(label, start, end, null)]
            };
            run.Clear();
        }

        for (var i = 0; i < tuples.Count; i++)
        {
            if (run.Count > 0 && tuples[run[0]].Speaker != tuples[i].Speaker)
            {
                foreach (var done in Flush())
                {
                    yield return done;
                }
            }

            run.Add(i);
        }

        foreach (var done in Flush())
        {
            yield return done;
        }
    }

    /// <summary>The diarizer speaker (1-based) covering most of the interval, or the nearest one within a second.</summary>
    private static int? SpeakerFor(TimeSpan start, TimeSpan end, IReadOnlyList<DiarizationSegment> diar)
    {
        if (diar.Count == 0)
        {
            return null;
        }

        var s = start.TotalSeconds;
        var e = Math.Max(end.TotalSeconds, s + 0.02);
        var best = diar
            .Select(d => (d.Speaker, Overlap: Math.Max(0, Math.Min(e, d.End) - Math.Max(s, d.Start))))
            .GroupBy(x => x.Speaker)
            .Select(g => (Speaker: g.Key, Overlap: g.Sum(x => x.Overlap)))
            .OrderByDescending(x => x.Overlap)
            .First();
        if (best.Overlap <= 0)
        {
            var nearest = diar.OrderBy(d => Math.Max(0, Math.Max(d.Start - e, s - d.End))).First();
            if (Math.Max(0, Math.Max(nearest.Start - e, s - nearest.End)) > 1.0)
            {
                return null;
            }

            best = (nearest.Speaker, 0);
        }

        return int.TryParse(best.Speaker.AsSpan(best.Speaker.LastIndexOf('_') + 1), out var number) ? number : null;
    }
}
