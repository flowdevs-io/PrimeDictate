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
/// therefore mean the same person across the whole meeting, which per-window diarization cannot promise. The rows are cut
/// from each channel's words (see <see cref="BuildRows"/>) and shown as they are. Nothing is written until everything has
/// succeeded, so a failure leaves the draft untouched; the draft stays stored as the earlier result.
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
        // Long windows give the recognizer the most context; each word then takes the diarizer speaker at its time. Only a window
        // that comes back without word times is redone turn by turn (needs the audio, so it is kept when a diarizer ran).
        var keepPcm = diar.Count > 0;
        var systemPcm = keepPcm ? new List<short>() : null;
        var fallbackWindows = new List<(double Start, double End)>();
        var turnCounter = 0;
        var turnReport = new List<string>();
        var windowReport = new List<string>();

        var max = asr.Capabilities.MaxWindow ?? TimeSpan.FromSeconds(28);
        var chunkOptions = new SpeechChunkerOptions { MaxChunk = max - TimeSpan.FromSeconds(2) < TimeSpan.FromSeconds(5) ? max : max - TimeSpan.FromSeconds(2) };
        var chunkers = new[] { new SpeechChunker(chunkOptions), new SpeechChunker(chunkOptions) };
        var counters = new[] { 0, 0 };
        // What the recognizer returned, window by window; the rows are cut from these once both channels are done.
        var mic = new List<TranscriptSegment>();
        var system = new List<TranscriptSegment>();
        var turnLines = new List<TranscriptSegment>();
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
                if (channel == 1 && windowReport.Count < 30)
                {
                    windowReport.Add($"{chunk.Start.TotalSeconds:0.0}-{(chunk.Start + chunk.Duration).TotalSeconds:0.0}s window: {recognized.Sum(r => r.Words?.Count ?? r.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)} words");
                }

                stats[channel].Words += recognized.Sum(r => r.Words?.Count ?? r.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
                if (channel == 0)
                {
                    var forced = recognized.Select(r => r with { SpeakerLabel = "local", Words = r.Words?.Select(w => w with { SpeakerId = "local" }).ToList() });
                    mic.AddRange(SegmentMapper.Map(forced, $"fm{index}", chunk.StartSample, chunk.Duration, resultVersion, 1, SegmentState.Final));
                }
                else
                {
                    var mapped = SegmentMapper.Map(recognized, $"fs{index}", chunk.StartSample, chunk.Duration, resultVersion, 1, SegmentState.Final).ToList();
                    if (keepPcm && mapped.Any(m => m.Words is not { Count: > 0 }))
                    {
                        fallbackWindows.Add((chunk.Start.TotalSeconds, (chunk.Start + chunk.Duration).TotalSeconds));
                    }
                    else
                    {
                        system.AddRange(mapped);
                    }
                }

                if (total > TimeSpan.Zero)
                {
                    progress?.Report(Math.Clamp((chunk.Start + chunk.Duration) / total, 0, 1));
                }
            }
        }

        async Task RecognizeTurnsAsync(IReadOnlyList<Turn> turns)
        {
            foreach (var turn in turns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var startSample = (long)(turn.Start * 16_000);
                var length = (int)Math.Min(systemPcm!.Count - startSample, (long)((turn.End - turn.Start) * 16_000));
                var index = turnCounter++;
                var st = stats[1];
                st.Windows++;
                stats[1] = st;
                if (length < 4_800)
                {
                    continue;
                }

                var samples = new float[length];
                double squares = 0;
                float peak = 0;
                for (var i = 0; i < length; i++)
                {
                    samples[i] = systemPcm[(int)startSample + i] / 32768f;
                    squares += samples[i] * samples[i];
                    peak = Math.Max(peak, Math.Abs(samples[i]));
                }

                st = stats[1];
                st.Squares += squares;
                st.Peak = Math.Max(st.Peak, peak);
                st.Samples += length;
                st.Sent++;
                stats[1] = st;
                IReadOnlyList<RecognizedSegment> recognized;
                using (await scheduler.AcquireAsync(asr.ModelId, ModelLeasePriority.Background, cancellationToken).ConfigureAwait(false))
                {
                    recognized = await asr.RecognizeWindowAsync(samples, language, cancellationToken).ConfigureAwait(false);
                }

                var turnWords = recognized.Sum(r => r.Words?.Count ?? r.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
                if (turnReport.Count < 30)
                {
                    turnReport.Add($"{turn.Start:0.0}-{turn.End:0.0}s speaker {turn.Speaker}: {turnWords} words");
                }

                stats[1].Words += recognized.Sum(r => r.Words?.Count ?? r.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
                var label = $"speaker-{turn.Speaker}";
                var forced = recognized.Select(r => r with { SpeakerLabel = label, Words = r.Words?.Select(w => w with { SpeakerId = label }).ToList() });
                turnLines.AddRange(SegmentMapper.Map(forced, $"ft{index}", startSample, TimeSpan.FromSeconds(length / 16_000d), resultVersion, 1, SegmentState.Final));
                if (total > TimeSpan.Zero)
                {
                    progress?.Report(Math.Clamp(0.5 + (0.5 * turn.End / Math.Max(1, systemPcm.Count / 16_000d)), 0, 1));
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
                if (channel == 1 && keepPcm)
                {
                    foreach (var v in samples)
                    {
                        systemPcm!.Add((short)Math.Clamp(v * 32767f, -32768f, 32767f));
                    }
                }

                await RecognizeAsync(channel, chunkers[channel].Add(samples)).ConfigureAwait(false);
            }

            total = frame.End > total ? frame.End : total;
        }

        await RecognizeAsync(0, chunkers[0].Flush()).ConfigureAwait(false);
        await RecognizeAsync(1, chunkers[1].Flush()).ConfigureAwait(false);
        if (fallbackWindows.Count > 0)
        {
            var turns = fallbackWindows.SelectMany(w => Turns(
                diar.Select(d => new DiarizationSegment(d.Speaker, Math.Max(d.Start, w.Start), Math.Min(d.End, w.End))).Where(d => d.End - d.Start > 0.1).ToList(),
                systemPcm!.Count / 16_000d,
                asr.Capabilities.MaxWindow ?? TimeSpan.FromSeconds(28))).ToList();
            await RecognizeTurnsAsync(turns).ConfigureAwait(false);
            turnReport.Insert(0, $"{fallbackWindows.Count} window(s) had no word times and were redone by speaker turn");
        }

        var rows = BuildRows(mic, system, turnLines, diar);

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
            DateTimeOffset.UtcNow,
            SegmentsAreRows: true);
        host.Apply(new SessionStarted(sessionId, run));
        // Microphone rows first, so "You" keeps its place in the speaker list; the rows are shown in start order either way.
        var all = rows.Microphone.Concat(rows.System).ToList();
        host.EnsureSpeakers(all);
        foreach (var segment in all)
        {
            host.Apply(new SegmentFinalized(sessionId, segment));
        }

        // A microphone line without word times that only repeats the speakers is hidden whole, as in the live view.
        foreach (var id in rows.EchoLines)
        {
            host.Edit(id, string.Empty);
        }

        host.SetStatus(TranscriptSessionStatus.Finalizing);
        host.Apply(new SessionCompleted(sessionId, total));
        if (overlay is not null)
        {
            overlay.Save(mediaDirectory);
        }

        var speakers = rows.System.SelectMany(s => s.Speakers).Select(a => a.SpeakerId).Distinct().Count();
        int Shown(List<TranscriptSegment> list) => list.Count(s => s.DisplayText.Length > 0 && !rows.EchoLines.Contains(s.Id));
        string Report(string name, int c) => stats[c].Samples == 0
            ? $"{name}: no audio"
            : $"{name}: rms {Math.Sqrt(stats[c].Squares / stats[c].Samples) * 32768:0}, peak {stats[c].Peak * 32768:0}, {stats[c].Sent} of {stats[c].Windows} windows sent, {stats[c].Words} words";
        return new FinalPassResult(resultVersion, Shown(rows.Microphone), Shown(rows.System), speakers, overlay?.OverlapSeconds ?? 0, overlay, diarizerProblem, $"{Report("microphone", 0)}; {Report("system", 1)}" + (windowReport.Count > 0 ? $"; system windows: {string.Join(", ", windowReport)}" : string.Empty) + (turnReport.Count > 0 ? $"; system turns: {string.Join(", ", turnReport)}" : string.Empty));
    }

    /// <summary>
    /// A row ends where its speaker is quiet this long, between two words or between two of the diarizer's segments for that
    /// speaker. Shorter gaps are breaths inside a sentence: the diarizer leaves gaps of 0.3-0.9 s inside one person's talking.
    /// </summary>
    internal static readonly TimeSpan Pause = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Makes the rows the session shows, and exports, one by one. Each channel's words are cut the same way, whatever window
    /// they were recognized in: where the speaker changes, where the diarizer's turn for that speaker ends, at a
    /// <see cref="Pause"/>, and where the other channel starts talking in the middle of a row. Microphone echo is judged per
    /// recognized line, as in the live view, and the hidden words get rows of their own, so a shown row starts and ends with
    /// the user's own words. Each channel's rows come back in start order.
    /// </summary>
    /// <param name="microphone">Recognized microphone lines, speaker "local".</param>
    /// <param name="system">Recognized system-audio lines; each word takes the diarizer speaker covering most of it.</param>
    /// <param name="turns">System lines recognized one diarizer turn at a time, speaker already set.</param>
    /// <returns>The rows, and the microphone lines without word times that only repeat the speakers (to be hidden whole).</returns>
    internal static (List<TranscriptSegment> Microphone, List<TranscriptSegment> System, HashSet<string> EchoLines) BuildRows(
        IReadOnlyList<TranscriptSegment> microphone,
        IReadOnlyList<TranscriptSegment> system,
        IReadOnlyList<TranscriptSegment> turns,
        IReadOnlyList<DiarizationSegment> diar)
    {
        var systemRows = system.Where(s => s.Words is not { Count: > 0 }).SelectMany(s => SplitBySpeaker(s, s.Id, diar)).ToList();
        if (Join(system.Where(s => s.Words is { Count: > 0 })) is { } spoken)
        {
            systemRows.AddRange(SplitBySpeaker(spoken, "fs", diar));
        }

        foreach (var turn in turns)
        {
            systemRows.AddRange(turn.Words is { Count: > 0 } words ? Cut(turn, turn.Id, i => IsPause(words, i)) : [turn]);
        }

        if (turns.Count > 0)
        {
            HideDuplicatesAcrossSpeakers(systemRows, diar);
        }

        // The microphone hears the speakers, so the same clean-up as in the live view applies to the You lines.
        var echoLines = new HashSet<string>(StringComparer.Ordinal);
        var micRows = new List<TranscriptSegment>();
        var judged = new List<TranscriptSegment>();
        foreach (var line in microphone)
        {
            var verdict = EchoMatcher.Judge(line, systemRows);
            if (line.Words is not { Count: > 0 } words)
            {
                micRows.Add(line);
                if (verdict is { HideAll: true })
                {
                    echoLines.Add(line.Id);
                }

                continue;
            }

            judged.Add(verdict switch
            {
                { HideAll: true } => line with { Words = words.Select(w => w with { Hidden = true }).ToList() },
                { Trimmed: { } trimmed } => trimmed,
                _ => line
            });
        }

        if (Join(judged) is { Words: { } said } joined)
        {
            micRows.AddRange(Cut(joined, "fm", i => said[i].Hidden != said[i - 1].Hidden || IsPause(said, i)));
        }

        // Where one side starts talking inside the other side's row, the row is cut there, so rows read in the order things
        // were said. Both sides' starts are taken before these cuts, so the two sides do not cut each other word by word.
        var micStarts = Starts(micRows.Where(r => !echoLines.Contains(r.Id)));
        var systemStarts = Starts(systemRows);
        return (
            micRows.SelectMany(r => CutAt(r, systemStarts)).OrderBy(r => r.Start).ThenBy(r => r.Id, StringComparer.Ordinal).ToList(),
            systemRows.SelectMany(r => CutAt(r, micStarts)).OrderBy(r => r.Start).ThenBy(r => r.Id, StringComparer.Ordinal).ToList(),
            echoLines);
    }

    /// <summary>
    /// Gives each word the diarizer speaker that covers most of it, repairs one-word flickers, and cuts rows where the speaker
    /// changes, where the diarizer's turn for that speaker ends and at a <see cref="Pause"/>. Without any diarizer segments
    /// everything is "remote".
    /// </summary>
    internal static IEnumerable<TranscriptSegment> SplitBySpeaker(TranscriptSegment segment, string idPrefix, IReadOnlyList<DiarizationSegment> diar)
    {
        if (segment.Words is not { Count: > 0 } words)
        {
            var only = SpeakerFor(segment.Start, segment.End, diar);
            return [segment with { Id = idPrefix, Speakers = [new SpeakerAttribution(only is null ? "remote" : $"speaker-{only}", segment.Start, segment.End, null)] }];
        }

        var tuples = words.Select(w => (w.Text, w.Start, w.End, w.Confidence, Speaker: SpeakerFor(w.Start, w.End, diar))).ToList();
        // Flickers are repaired only among words said without a pause. Across one, a word from someone else is a real reply,
        // and these are the whole meeting's words, so a lone "yeah" would otherwise go to the speaker talking a minute away.
        for (var from = 0; from < tuples.Count;)
        {
            var to = from + 1;
            while (to < tuples.Count && !IsPause(words, to))
            {
                to++;
            }

            var stretch = tuples.GetRange(from, to - from);
            NemotronResponseParser.SmoothSpeakers(stretch);
            for (var i = 0; i < stretch.Count; i++)
            {
                tuples[from + i] = stretch[i];
            }

            from = to;
        }
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

        var turns = SpeakerTurns(diar);
        var turn = tuples.Select(t => TurnOf(t.Start, t.End, t.Speaker, turns)).ToList();
        return Cut(
            segment,
            idPrefix,
            i => tuples[i].Speaker != tuples[i - 1].Speaker || turn[i] != turn[i - 1] || IsPause(words, i),
            i => tuples[i].Speaker is { } speaker ? $"speaker-{speaker}" : "remote");
    }

    /// <summary>
    /// Cuts a line's words into rows before each word <paramref name="cutBefore"/> picks (it is never asked about the first).
    /// A row's speaker is <paramref name="speakerOf"/> its first word, or the line's own.
    /// </summary>
    private static IEnumerable<TranscriptSegment> Cut(TranscriptSegment line, string idPrefix, Func<int, bool> cutBefore, Func<int, string>? speakerOf = null)
    {
        var words = line.Words!;
        var own = line.Speakers.Count > 0 ? line.Speakers[0].SpeakerId : "remote";
        var from = 0;
        var k = 0;
        for (var i = 1; i <= words.Count; i++)
        {
            if (i < words.Count && !cutBefore(i))
            {
                continue;
            }

            var label = speakerOf?.Invoke(from) ?? own;
            var part = Enumerable.Range(from, i - from).Select(j => words[j] with { SpeakerId = label }).ToList();
            var start = part[0].Start;
            var end = part[^1].End < start ? start : part[^1].End;
            var confidences = part.Where(w => w.Confidence is not null).Select(w => w.Confidence!.Value).ToList();
            yield return line with
            {
                Id = $"{idPrefix}.{k++}",
                Start = start,
                End = end,
                RawText = string.Join(' ', part.Select(w => w.Text)),
                Words = part,
                Confidence = confidences.Count > 0 ? confidences.Average() : line.Confidence,
                Speakers = [new SpeakerAttribution(label, start, end, null)]
            };
            from = i;
        }
    }

    /// <summary>One line holding the words of all these lines in time order, so a row does not end where a recognizer window did.</summary>
    private static TranscriptSegment? Join(IEnumerable<TranscriptSegment> lines)
    {
        var ordered = lines.OrderBy(l => l.Start).ToList();
        if (ordered.Count == 0)
        {
            return null;
        }

        var words = ordered.SelectMany(l => l.Words!).ToList();
        var confidences = ordered.Where(l => l.Confidence is not null).Select(l => l.Confidence!.Value).ToList();
        return ordered[0] with
        {
            Start = words[0].Start,
            End = words.Max(w => w.End),
            RawText = string.Join(' ', words.Select(w => w.Text)),
            Words = words,
            Confidence = confidences.Count > 0 ? confidences.Average() : null
        };
    }

    private static bool IsPause(IReadOnlyList<WordTiming> words, int i) => words[i].Start - words[i - 1].End >= Pause;

    /// <summary>Where each shown row starts, in time order.</summary>
    private static List<TimeSpan> Starts(IEnumerable<TranscriptSegment> rows) =>
        rows.Where(r => r.DisplayText.Length > 0).Select(r => r.DisplayStart).Order().ToList();

    /// <summary>
    /// Cuts a row before its first word at or after each of <paramref name="starts"/> that falls inside it, so the other side's
    /// line sits between the pieces. A cut that would leave a piece shorter than <see cref="TranscriptTurns.FragmentLength"/>
    /// is skipped: a word said just as the other side starts or stops is talking over them, not a turn in the middle.
    /// </summary>
    private static IEnumerable<TranscriptSegment> CutAt(TranscriptSegment row, IReadOnlyList<TimeSpan> starts)
    {
        if (row.Words is not { Count: > 1 } words || row.DisplayText.Length == 0)
        {
            return [row];
        }

        var cuts = new HashSet<int>();
        var from = 0;
        foreach (var start in starts.Where(t => t > row.DisplayStart && t < row.DisplayEnd))
        {
            var at = Enumerable.Range(from, words.Count - from).FirstOrDefault(i => words[i].Start >= start, -1);
            if (at > from && ShownLength(words, from, at) >= TranscriptTurns.FragmentLength && ShownLength(words, at, words.Count) >= TranscriptTurns.FragmentLength)
            {
                cuts.Add(at);
                from = at;
            }
        }

        return cuts.Count == 0 ? [row] : Cut(row, row.Id, cuts.Contains);
    }

    /// <summary>How long the shown words among <c>words[from..to)</c> take, first start to last end.</summary>
    private static TimeSpan ShownLength(IReadOnlyList<WordTiming> words, int from, int to)
    {
        var shown = Enumerable.Range(from, to - from).Select(i => words[i]).Where(w => !w.Hidden).ToList();
        return shown.Count == 0 ? TimeSpan.Zero : shown[^1].End - shown[0].Start;
    }

    /// <summary>
    /// Each speaker's stretches of talking as the diarizer heard them: that speaker's segments less than a <see cref="Pause"/>
    /// apart are one turn. A row never spans two turns, so rows follow the timeline's bars.
    /// </summary>
    private static List<Turn> SpeakerTurns(IReadOnlyList<DiarizationSegment> diar)
    {
        var turns = new List<Turn>();
        foreach (var group in diar.Select(d => (Speaker: SpeakerNumber(d.Speaker), d.Start, d.End)).Where(d => d.Speaker is not null).GroupBy(d => d.Speaker!.Value))
        {
            Turn? current = null;
            foreach (var segment in group.OrderBy(g => g.Start))
            {
                if (current is { } open && segment.Start - open.End < Pause.TotalSeconds)
                {
                    current = open with { End = Math.Max(open.End, segment.End) };
                    continue;
                }

                if (current is { } done)
                {
                    turns.Add(done);
                }

                current = new Turn(group.Key, segment.Start, segment.End);
            }

            if (current is { } last)
            {
                turns.Add(last);
            }
        }

        return turns;
    }

    /// <summary>Which of <paramref name="turns"/> the word belongs to: its speaker's turn covering most of it, or the nearest one.</summary>
    private static int? TurnOf(TimeSpan start, TimeSpan end, int? speaker, List<Turn> turns)
    {
        var s = start.TotalSeconds;
        var e = Math.Max(end.TotalSeconds, s + 0.02);
        int? best = null;
        var bestOverlap = double.NegativeInfinity;
        for (var i = 0; i < turns.Count; i++)
        {
            // Positive: how much of the word the turn covers; negative: how far apart they are.
            var overlap = Math.Min(e, turns[i].End) - Math.Max(s, turns[i].Start);
            if (turns[i].Speaker == speaker && overlap > bestOverlap)
            {
                best = i;
                bestOverlap = overlap;
            }
        }

        return best;
    }

    /// <summary>
    /// Where two speakers talk at once, both turns hear the same mixed audio and recognize the same words. A word that another
    /// speaker's turn also produced at the same moment stays with the speaker the diarizer says covers it more; the other copy is
    /// hidden (kept, not deleted), and a row left with only hidden words is dropped.
    /// </summary>
    internal static void HideDuplicatesAcrossSpeakers(List<TranscriptSegment> system, IReadOnlyList<DiarizationSegment> diar)
    {
        static string Key(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        double Coverage(int speaker, WordTiming w) => diar
            .Where(d => SpeakerNumber(d.Speaker) == speaker)
            .Sum(d => Math.Max(0, Math.Min(w.End.TotalSeconds, d.End) - Math.Max(w.Start.TotalSeconds, d.Start)));

        int Owner(TranscriptSegment segment) => SpeakerNumber(segment.Speakers[0].SpeakerId.Replace('-', '_')) ?? 0;

        var all = system.SelectMany((segment, si) => (segment.Words ?? []).Select((word, wi) => (si, wi, word, Speaker: Owner(segment)))).ToList();
        var hide = new HashSet<(int, int)>();
        foreach (var a in all)
        {
            foreach (var b in all)
            {
                if (a.Speaker >= b.Speaker || Key(a.word.Text).Length == 0 || Key(a.word.Text) != Key(b.word.Text)
                    || Math.Abs((a.word.Start - b.word.Start).TotalSeconds) > 0.6)
                {
                    continue;
                }

                hide.Add(Coverage(a.Speaker, a.word) >= Coverage(b.Speaker, b.word) ? (b.si, b.wi) : (a.si, a.wi));
            }
        }

        if (hide.Count == 0)
        {
            return;
        }

        for (var si = system.Count - 1; si >= 0; si--)
        {
            if (system[si].Words is not { } words || !hide.Any(h => h.Item1 == si))
            {
                continue;
            }

            var updated = words.Select((w, wi) => hide.Contains((si, wi)) ? w with { Hidden = true } : w).ToList();
            if (updated.All(w => w.Hidden))
            {
                system.RemoveAt(si);
            }
            else
            {
                system[si] = system[si] with { Words = updated };
            }
        }
    }

    internal readonly record struct Turn(int Speaker, double Start, double End);

    /// <summary>
    /// One span of audio per stretch of a speaker's talking: a speaker's segments with gaps under a second are joined, each span
    /// gets a little padding so words at the edges are not clipped, and spans are cut to the recognizer's window.
    /// </summary>
    internal static IReadOnlyList<Turn> Turns(IReadOnlyList<DiarizationSegment> diar, double durationSeconds, TimeSpan maxWindow)
    {
        const double MergeGap = 1.0;
        const double Pad = 0.25;
        var limit = Math.Max(5, maxWindow.TotalSeconds - 2);
        var turns = new List<Turn>();
        foreach (var group in diar.Select(d => (Speaker: SpeakerNumber(d.Speaker), d.Start, d.End)).Where(d => d.Speaker is not null).GroupBy(d => d.Speaker!.Value))
        {
            double? start = null, end = null;
            void Emit()
            {
                if (start is null)
                {
                    return;
                }

                for (var from = Math.Max(0, start.Value - Pad); from < end!.Value; from += limit)
                {
                    turns.Add(new Turn(group.Key, from, Math.Min(Math.Min(from + limit, end.Value + Pad), durationSeconds)));
                }
            }

            foreach (var segment in group.OrderBy(g => g.Start))
            {
                if (start is not null && segment.Start - end!.Value <= MergeGap)
                {
                    end = Math.Max(end.Value, segment.End);
                    continue;
                }

                Emit();
                start = segment.Start;
                end = segment.End;
            }

            Emit();
        }

        return turns.OrderBy(t => t.Start).ThenBy(t => t.Speaker).ToList();
    }

    private static int? SpeakerNumber(string speaker) =>
        int.TryParse(speaker.AsSpan(speaker.LastIndexOf('_') + 1), out var number) ? number : null;

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

        return SpeakerNumber(best.Speaker);
    }
}
