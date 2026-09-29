using System.Text;

namespace PrimeDictate.Core.Dictation;

public enum DictationAudioCue
{
    Start = 0,
    Stop = 1
}

public interface IAudioCuePlayer
{
    /// <summary>Plays without blocking and never throws: a missing sound device must not affect dictation.</summary>
    void Play(DictationAudioCue cue);
}

/// <summary>The start and stop chirps, synthesized rather than shipped as files. Same tones as the WPF app.</summary>
public static class AudioCues
{
    private const int SampleRate = 24_000;

    private static readonly Lazy<byte[]> Start = new(() => Build(
        Tone(415.30, 466.16, 0.05, 0.27), Silence(0.012), Tone(523.25, 587.33, 0.055, 0.24), Silence(0.01), Tone(659.25, 739.99, 0.085, 0.22)));

    private static readonly Lazy<byte[]> Stop = new(() => Build(
        Tone(698.46, 659.25, 0.05, 0.23), Silence(0.01), Tone(587.33, 523.25, 0.055, 0.20), Silence(0.012), Tone(466.16, 392.00, 0.09, 0.18)));

    /// <summary>A complete 16-bit mono 24 kHz WAV file.</summary>
    public static byte[] Wave(DictationAudioCue cue) => cue == DictationAudioCue.Start ? Start.Value : Stop.Value;

    private static byte[] Build(params (double From, double To, double Seconds, double Amplitude, bool Silent)[] segments)
    {
        var pcm = new List<byte>();
        foreach (var segment in segments)
        {
            var count = Math.Max(1, (int)Math.Round(segment.Seconds * SampleRate));
            if (segment.Silent)
            {
                pcm.AddRange(new byte[count * 2]);
                continue;
            }

            double attack = Math.Max(1.0, count * 0.07);
            double release = Math.Max(1.0, count * 0.22);
            double phase = 0;
            for (var i = 0; i < count; i++)
            {
                var progress = count == 1 ? 1.0 : (double)i / (count - 1);
                var frequency = (segment.From + ((segment.To - segment.From) * progress)) * (1.0 - (0.09 * Math.Exp(-progress * 18.0)));
                phase += 2 * Math.PI * frequency / SampleRate;
                var envelope = Math.Max(0, Math.Min(i < attack ? i / attack : 1.0, i >= count - release ? (count - i - 1) / release : 1.0));
                var shimmer = (0.90 * Math.Sin(phase)) + (0.16 * Math.Sin((phase * 0.5) + 0.2)) + (0.18 * Math.Sin((phase * 2.0) - 0.35)) + (0.07 * Math.Sin((phase * 3.0) + 1.1));
                var sample = (short)Math.Clamp(shimmer * segment.Amplitude * envelope * 0.78 * short.MaxValue, short.MinValue, short.MaxValue);
                pcm.Add((byte)(sample & 0xFF));
                pcm.Add((byte)((sample >> 8) & 0xFF));
            }
        }

        using var stream = new MemoryStream(44 + pcm.Count);
        using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + pcm.Count);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(SampleRate);
        w.Write(SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(pcm.Count);
        w.Write(pcm.ToArray());
        w.Flush();
        return stream.ToArray();
    }

    private static (double, double, double, double, bool) Tone(double from, double to, double seconds, double amplitude) => (from, to, seconds, amplitude, false);

    private static (double, double, double, double, bool) Silence(double seconds) => (0, 0, seconds, 0, true);
}
