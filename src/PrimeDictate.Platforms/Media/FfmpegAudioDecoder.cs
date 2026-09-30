using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Providers;

namespace PrimeDictate.Platforms.Media;

/// <summary>
/// Decodes audio and video containers by running ffprobe and ffmpeg as child processes. Arguments go
/// through <see cref="ProcessStartInfo.ArgumentList"/> (no shell, no string concatenation of paths),
/// the path is prefixed with <c>file:</c> so it is never read as a URL, and the protocol whitelist is
/// limited to local files and pipes.
/// </summary>
public sealed class FfmpegAudioDecoder : IAudioDecoder
{
    private const int FrameSamplesPerChannel = 4096;
    private readonly string ffmpegPath;
    private readonly string ffprobePath;

    public FfmpegAudioDecoder(string ffmpegPath, string ffprobePath)
    {
        this.ffmpegPath = ffmpegPath;
        this.ffprobePath = ffprobePath;
    }

    /// <summary>
    /// Finds ffmpeg and ffprobe: <c>PRIMEDICTATE_FFMPEG_DIR</c>, then next to the app (or an
    /// <c>ffmpeg</c> subfolder), then PATH. Returns null when either is missing.
    /// </summary>
    public static FfmpegAudioDecoder? TryCreate()
    {
        var dirs = new List<string>();
        var env = Environment.GetEnvironmentVariable("PRIMEDICTATE_FFMPEG_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            dirs.Add(env);
        }

        dirs.Add(AppContext.BaseDirectory);
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg"));
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        string? ffmpeg = null, ffprobe = null;
        foreach (var dir in dirs)
        {
            ffmpeg ??= Existing(dir, "ffmpeg" + suffix);
            ffprobe ??= Existing(dir, "ffprobe" + suffix);
        }

        return ffmpeg is null || ffprobe is null ? null : new FfmpegAudioDecoder(ffmpeg, ffprobe);
    }

    private static string? Existing(string dir, string file)
    {
        try
        {
            var path = Path.Combine(dir, file);
            return File.Exists(path) ? path : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public async ValueTask<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new MediaDecodeException("unreadable", "The file could not be found.");
        }

        var info = NewProcess(this.ffprobePath, "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-protocol_whitelist", "file,pipe", "-i", "file:" + path);
        using var process = Start(info);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var json = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new MediaDecodeException("unsupported-format", "This file could not be read as audio or video.", new InvalidOperationException(Trim(error)));
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var streams = new List<MediaStreamInfo>();
            if (root.TryGetProperty("streams", out var list))
            {
                foreach (var s in list.EnumerateArray())
                {
                    if (s.TryGetProperty("codec_type", out var type) && type.GetString() == "audio")
                    {
                        var tags = s.TryGetProperty("tags", out var t) ? t : default;
                        streams.Add(new MediaStreamInfo(
                            s.GetProperty("index").GetInt32(),
                            s.TryGetProperty("codec_name", out var codec) ? codec.GetString() ?? "unknown" : "unknown",
                            ParseInt(s, "sample_rate"),
                            s.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 0,
                            Tag(tags, "language"),
                            Tag(tags, "title")));
                    }
                }
            }

            TimeSpan? duration = null;
            var format = "unknown";
            if (root.TryGetProperty("format", out var fmt))
            {
                format = fmt.TryGetProperty("format_name", out var fn) ? fn.GetString() ?? "unknown" : "unknown";
                if (fmt.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                {
                    duration = TimeSpan.FromSeconds(seconds);
                }
            }

            return new MediaProbeResult(format, duration, streams);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new MediaDecodeException("corrupt", "The media information could not be read.", ex);
        }
    }

    public async IAsyncEnumerable<AudioFrame> DecodeAsync(string path, int streamIndex, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var probe = await this.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        var stream = probe.AudioStreams.FirstOrDefault(s => s.Index == streamIndex)
            ?? throw new MediaDecodeException("no-audio", "The selected audio track was not found.");
        if (stream.SampleRate <= 0 || stream.Channels <= 0)
        {
            throw new MediaDecodeException("corrupt", "The audio track has no usable format.");
        }

        // Native rate and channel layout: decode to raw float, no resampling here.
        var info = NewProcess(
            this.ffmpegPath, "-nostdin", "-v", "error", "-protocol_whitelist", "file,pipe",
            "-i", "file:" + path, "-map", $"0:{streamIndex}", "-vn", "-sn", "-dn",
            "-f", "f32le", "-acodec", "pcm_f32le", "-ar", stream.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ac", stream.Channels.ToString(System.Globalization.CultureInfo.InvariantCulture), "pipe:1");
        using var process = Start(info);
        using var cancel = cancellationToken.Register(() => Kill(process));
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var bytesPerFrame = stream.Channels * sizeof(float);
        var buffer = new byte[FrameSamplesPerChannel * bytesPerFrame];
        var floats = new float[FrameSamplesPerChannel * stream.Channels];
        var format = new AudioFormat(stream.SampleRate, stream.Channels, AudioSampleFormat.Float32);
        long sequence = 0, offset = 0;
        var baseStream = process.StandardOutput.BaseStream;
        while (true)
        {
            var filled = 0;
            while (filled < buffer.Length)
            {
                var read = await baseStream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                filled += read;
            }

            var whole = filled - (filled % bytesPerFrame);
            if (whole > 0)
            {
                var count = whole / sizeof(float);
                Buffer.BlockCopy(buffer, 0, floats, 0, whole);
                var frame = AudioFrame.CopyFrom(floats.AsSpan(0, count), format, sequence++, offset);
                offset += count / stream.Channels;
                yield return frame;
            }

            if (filled < buffer.Length)
            {
                break;
            }
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new MediaDecodeException("corrupt", "The file could not be decoded completely.", new InvalidOperationException(Trim(await stderr.ConfigureAwait(false))));
        }
    }

    private static ProcessStartInfo NewProcess(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        return info;
    }

    private static Process Start(ProcessStartInfo info)
    {
        try
        {
            return Process.Start(info) ?? throw new MediaDecodeException("decoder-missing", "The media decoder could not be started.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new MediaDecodeException("decoder-missing", "ffmpeg could not be started. Install ffmpeg or set PRIMEDICTATE_FFMPEG_DIR.", ex);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static int ParseInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && int.TryParse(v.GetString(), out var n) ? n : 0;

    private static string? Tag(JsonElement tags, string name) =>
        tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty(name, out var v) ? v.GetString() : null;

    private static string Trim(string text) => text.Length > 400 ? text[..400] : text;
}
