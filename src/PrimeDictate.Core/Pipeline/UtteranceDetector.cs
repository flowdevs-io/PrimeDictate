using PrimeDictate.Core.Audio;

namespace PrimeDictate.Core.Pipeline;

public sealed record UtteranceDetectorOptions
{
    public TimeSpan Frame { get; init; } = TimeSpan.FromMilliseconds(30);

    public float SpeechRms { get; init; } = 0.012f;

    /// <summary>Silence that ends an utterance. It finalizes the utterance, never the session.</summary>
    public TimeSpan EndSilence { get; init; } = TimeSpan.FromMilliseconds(700);

    /// <summary>Hard limit so a continuous talker still gets finalized segments.</summary>
    public TimeSpan MaxUtterance { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Audio kept before speech starts so first phonemes are not clipped.</summary>
    public TimeSpan PreRoll { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Utterances shorter than this are discarded as noise.</summary>
    public TimeSpan MinSpeech { get; init; } = TimeSpan.FromMilliseconds(250);
}

public enum UtteranceEventKind
{
    /// <summary>Speech is ongoing; <see cref="UtteranceEvent.Samples"/> is the audio so far.</summary>
    Growing = 0,

    /// <summary>The utterance ended. Samples hold all of it.</summary>
    Ended = 1
}

public sealed record UtteranceEvent(UtteranceEventKind Kind, int UtteranceIndex, long StartSample, float[] Samples);

/// <summary>
/// Energy-based endpointing on the 16 kHz mono timeline. Utterance audio is bounded by
/// <see cref="UtteranceDetectorOptions.MaxUtterance"/>, so live recognition never re-processes the
/// whole session. Offsets are absolute sample positions.
/// </summary>
public sealed class UtteranceDetector
{
    private readonly UtteranceDetectorOptions options;
    private readonly int frameSamples;
    private readonly int endSilenceFrames;
    private readonly int maxSamples;
    private readonly int preRollSamples;
    private readonly int minSpeechSamples;
    private readonly List<float> pending = [];
    private readonly List<float> utterance = [];
    private readonly Queue<float[]> preRoll = new();
    private int preRollCount;
    private long consumed;
    private long utteranceStart;
    private int silentFrames;
    private int speechSamples;
    private int index;
    private bool inUtterance;

    public UtteranceDetector(UtteranceDetectorOptions? options = null)
    {
        this.options = options ?? new UtteranceDetectorOptions();
        var format = AudioFormat.SpeechTimeline;
        this.frameSamples = (int)format.ToSampleOffset(this.options.Frame);
        this.endSilenceFrames = (int)Math.Ceiling(this.options.EndSilence / this.options.Frame);
        this.maxSamples = (int)format.ToSampleOffset(this.options.MaxUtterance);
        this.preRollSamples = (int)format.ToSampleOffset(this.options.PreRoll);
        this.minSpeechSamples = (int)format.ToSampleOffset(this.options.MinSpeech);
    }

    public bool InUtterance => this.inUtterance;

    /// <param name="samples">New audio.</param>
    /// <param name="emitGrowing">
    /// False skips the <see cref="UtteranceEventKind.Growing"/> snapshot, which copies the whole utterance so far;
    /// callers that are not going to preview should not pay for it.
    /// </param>
    public IReadOnlyList<UtteranceEvent> Add(ReadOnlySpan<float> samples, bool emitGrowing = true)
    {
        this.pending.AddRange(samples.ToArray());
        var events = new List<UtteranceEvent>();
        var grew = false;
        while (this.pending.Count >= this.frameSamples)
        {
            var frame = this.pending.GetRange(0, this.frameSamples).ToArray();
            this.pending.RemoveRange(0, this.frameSamples);
            var voiced = Rms(frame) >= this.options.SpeechRms;
            if (this.inUtterance)
            {
                this.utterance.AddRange(frame);
                this.silentFrames = voiced ? 0 : this.silentFrames + 1;
                if (voiced)
                {
                    this.speechSamples += this.frameSamples;
                }

                grew = true;
                if (this.silentFrames >= this.endSilenceFrames || this.utterance.Count >= this.maxSamples)
                {
                    this.End(events);
                    grew = false;
                }
            }
            else if (voiced)
            {
                this.Begin(frame);
                grew = true;
            }
            else
            {
                this.PushPreRoll(frame);
            }

            this.consumed += this.frameSamples;
        }

        if (emitGrowing && grew && this.inUtterance)
        {
            events.Add(new UtteranceEvent(UtteranceEventKind.Growing, this.index, this.utteranceStart, this.utterance.ToArray()));
        }

        return events;
    }

    /// <summary>Ends any open utterance at end of stream, including a partial trailing frame.</summary>
    public IReadOnlyList<UtteranceEvent> Flush()
    {
        var events = new List<UtteranceEvent>();
        if (this.inUtterance)
        {
            this.utterance.AddRange(this.pending);
            this.consumed += this.pending.Count;
            this.pending.Clear();
            this.End(events);
        }

        return events;
    }

    private void Begin(float[] frame)
    {
        this.inUtterance = true;
        this.silentFrames = 0;
        this.speechSamples = this.frameSamples;
        this.utterance.Clear();
        var lead = this.preRoll.SelectMany(f => f).ToArray();
        this.utterance.AddRange(lead);
        this.utterance.AddRange(frame);
        this.utteranceStart = this.consumed - lead.Length;
        this.preRoll.Clear();
        this.preRollCount = 0;
    }

    private void End(List<UtteranceEvent> events)
    {
        this.inUtterance = false;
        if (this.speechSamples >= this.minSpeechSamples)
        {
            events.Add(new UtteranceEvent(UtteranceEventKind.Ended, this.index, this.utteranceStart, this.utterance.ToArray()));
            this.index++;
        }

        this.utterance.Clear();
        this.silentFrames = 0;
        this.speechSamples = 0;
    }

    private void PushPreRoll(float[] frame)
    {
        this.preRoll.Enqueue(frame);
        this.preRollCount += frame.Length;
        while (this.preRollCount - this.preRoll.Peek().Length >= this.preRollSamples)
        {
            this.preRollCount -= this.preRoll.Dequeue().Length;
        }
    }

    private static float Rms(float[] frame)
    {
        double sum = 0;
        foreach (var s in frame)
        {
            sum += s * s;
        }

        return (float)Math.Sqrt(sum / frame.Length);
    }
}
