using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Providers;

namespace PrimeDictate.Platforms.Media;

/// <summary>Uses the built-in WAV reader for .wav files (no external tool needed) and ffmpeg for everything else.</summary>
public sealed class CompositeAudioDecoder(IAudioDecoder wav, IAudioDecoder? other) : IAudioDecoder
{
    public ValueTask<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
        this.Pick(path).ProbeAsync(path, cancellationToken);

    public IAsyncEnumerable<AudioFrame> DecodeAsync(string path, int streamIndex, CancellationToken cancellationToken) =>
        this.Pick(path).DecodeAsync(path, streamIndex, cancellationToken);

    private IAudioDecoder Pick(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            return wav;
        }

        return other ?? throw new MediaDecodeException(
            "decoder-missing",
            "Only WAV files can be opened because ffmpeg was not found. Install ffmpeg (or set PRIMEDICTATE_FFMPEG_DIR) to open other audio and video files.");
    }
}
