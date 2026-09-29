namespace PrimeDictate.Core.Audio;

/// <summary>A span of continuously captured audio and the wall-clock time it was captured.</summary>
public sealed record CaptureInterval(
    TimeSpan RecordedStart,
    TimeSpan RecordedDuration,
    DateTimeOffset WallClockStart,
    CaptureIntervalEnd EndReason);

public enum CaptureIntervalEnd
{
    Open = 0,
    Paused = 1,
    Stopped = 2,
    /// <summary>Capture failed or samples were lost; the next interval does not continue this one.</summary>
    Gap = 3
}

/// <summary>
/// Maps the continuous recorded-audio timeline (what playback and transcripts use) to the
/// wall-clock intervals when capture actually ran. Pausing records nothing and adds no
/// recorded time. Recorded offsets come from sample counts, never from a UI clock.
/// </summary>
public sealed class RecordedAudioTimeline
{
    private readonly List<CaptureInterval> intervals = [];
    private readonly AudioFormat format;
    private long recordedSamples;
    private long openIntervalStartSample = -1;
    private DateTimeOffset openIntervalWallClock;

    public RecordedAudioTimeline(AudioFormat format)
    {
        format.Validate();
        this.format = format;
    }

    public bool IsCapturing => this.openIntervalStartSample >= 0;

    public TimeSpan RecordedDuration => this.format.ToTime(this.recordedSamples);

    public long RecordedSamples => this.recordedSamples;

    public IReadOnlyList<CaptureInterval> Intervals => this.intervals;

    public void BeginInterval(DateTimeOffset wallClockStart)
    {
        if (this.IsCapturing)
        {
            throw new InvalidOperationException("A capture interval is already open.");
        }

        this.openIntervalStartSample = this.recordedSamples;
        this.openIntervalWallClock = wallClockStart;
    }

    /// <summary>Appends captured samples; returns the recorded-timeline offset of the first one.</summary>
    public long Append(int samplesPerChannel)
    {
        if (!this.IsCapturing)
        {
            throw new InvalidOperationException("Audio arrived while no capture interval is open (paused or stopped).");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(samplesPerChannel);
        var offset = this.recordedSamples;
        this.recordedSamples += samplesPerChannel;
        return offset;
    }

    public CaptureInterval EndInterval(CaptureIntervalEnd reason)
    {
        if (!this.IsCapturing)
        {
            throw new InvalidOperationException("No capture interval is open.");
        }

        if (reason == CaptureIntervalEnd.Open)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        var interval = new CaptureInterval(
            this.format.ToTime(this.openIntervalStartSample),
            this.format.ToTime(this.recordedSamples - this.openIntervalStartSample),
            this.openIntervalWallClock,
            reason);
        this.intervals.Add(interval);
        this.openIntervalStartSample = -1;
        return interval;
    }

    /// <summary>Returns the wall-clock time a recorded offset was captured, or null if it is past the end.</summary>
    public DateTimeOffset? ToWallClock(TimeSpan recordedOffset)
    {
        foreach (var interval in this.intervals)
        {
            if (recordedOffset >= interval.RecordedStart &&
                recordedOffset < interval.RecordedStart + interval.RecordedDuration)
            {
                return interval.WallClockStart + (recordedOffset - interval.RecordedStart);
            }
        }

        if (this.IsCapturing)
        {
            var openStart = this.format.ToTime(this.openIntervalStartSample);
            if (recordedOffset >= openStart && recordedOffset < this.RecordedDuration)
            {
                return this.openIntervalWallClock + (recordedOffset - openStart);
            }
        }

        return null;
    }
}
