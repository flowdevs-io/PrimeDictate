namespace PrimeDictate.Core.Dictation;

/// <summary>
/// Decides when the user is speaking from the microphone level. Ports the WPF controller's adaptive
/// noise floor: the threshold is 3.5x the floor, clamped, and speech never trains the floor upward,
/// so a quiet talker is not mistaken for silence and auto-committed early.
/// </summary>
public sealed class SpeechActivityTracker
{
    public const double MinSpeechRms = 0.0018;
    public const double MaxSpeechRms = 0.02;
    public const int LevelBlockSamples = 1_600;
    public const int MinSpeechLevelEventsBeforeAutoCommit = 3;
    public static readonly TimeSpan MinAutoCommitRecordingDuration = TimeSpan.FromSeconds(1.5);

    private const double NoiseFloorRiseSmoothing = 0.02;
    private const double NoiseFloorFallSmoothing = 0.18;

    private readonly TimeProvider time;
    private readonly float[] pending = new float[LevelBlockSamples];
    private int pendingCount;
    private double noiseFloor = MinSpeechRms;
    private long lastSpeechTicks;
    private int speechEvents;

    public SpeechActivityTracker(TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Raised for each 100 ms level block (16 kHz), with its RMS.</summary>
    public event Action<double>? LevelUpdated;

    public double MaxObservedRms { get; private set; }

    public int SpeechEvents => Volatile.Read(ref this.speechEvents);

    public DateTime? LastSpeechUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref this.lastSpeechTicks);
            return ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
        }
    }

    public void Reset()
    {
        this.pendingCount = 0;
        this.noiseFloor = MinSpeechRms;
        this.MaxObservedRms = 0;
        Interlocked.Exchange(ref this.lastSpeechTicks, 0);
        Volatile.Write(ref this.speechEvents, 0);
    }

    /// <summary>Feeds 16 kHz mono samples. Call from one thread.</summary>
    public void AddSamples(ReadOnlySpan<float> samples)
    {
        while (!samples.IsEmpty)
        {
            var take = Math.Min(samples.Length, LevelBlockSamples - this.pendingCount);
            samples[..take].CopyTo(this.pending.AsSpan(this.pendingCount));
            this.pendingCount += take;
            samples = samples[take..];
            if (this.pendingCount == LevelBlockSamples)
            {
                this.OnLevel(Rms(this.pending));
                this.pendingCount = 0;
            }
        }
    }

    public void OnLevel(double rms)
    {
        rms = Math.Max(0, rms);
        if (rms > this.MaxObservedRms)
        {
            this.MaxObservedRms = rms;
        }

        var threshold = Math.Clamp(this.noiseFloor * 3.5, MinSpeechRms, MaxSpeechRms);
        if (rms >= threshold)
        {
            Interlocked.Exchange(ref this.lastSpeechTicks, this.time.GetUtcNow().UtcDateTime.Ticks);
            Interlocked.Increment(ref this.speechEvents);
        }
        else
        {
            var smoothing = rms < this.noiseFloor ? NoiseFloorFallSmoothing : NoiseFloorRiseSmoothing;
            this.noiseFloor = (this.noiseFloor * (1 - smoothing)) + (rms * smoothing);
        }

        this.LevelUpdated?.Invoke(rms);
    }

    public bool IsAutoCommitArmed(TimeSpan recordingElapsed) =>
        recordingElapsed >= MinAutoCommitRecordingDuration && this.SpeechEvents >= MinSpeechLevelEventsBeforeAutoCommit;

    /// <summary>True when the level path heard enough speech, or the buffer itself holds two loud 100 ms frames.</summary>
    public bool HasSpeechEvidence(ReadOnlySpan<float> recorded) =>
        this.SpeechEvents >= MinSpeechLevelEventsBeforeAutoCommit || ContainsLikelySpeech(recorded);

    public static bool ContainsLikelySpeech(ReadOnlySpan<float> samples)
    {
        const int requiredSpeechFrames = 2;
        var speechFrames = 0;
        for (var start = 0; start < samples.Length; start += LevelBlockSamples)
        {
            var frame = samples.Slice(start, Math.Min(LevelBlockSamples, samples.Length - start));
            if (Rms(frame) >= MinSpeechRms && ++speechFrames >= requiredSpeechFrames)
            {
                return true;
            }
        }

        return false;
    }

    public static double Rms(ReadOnlySpan<float> frame)
    {
        if (frame.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        foreach (var s in frame)
        {
            sum += (double)s * s;
        }

        return Math.Sqrt(sum / frame.Length);
    }
}
