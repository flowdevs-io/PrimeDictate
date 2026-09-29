using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using PrimeDictate.Core.Providers;

namespace PrimeDictate.Core.Audio;

/// <summary>
/// Streaming writer for the session's owned working audio: 16 kHz mono PCM16 WAV. The header is
/// rewritten on every <see cref="Flush"/> so a crash leaves a playable file up to the last flush.
/// </summary>
public sealed class WavFileWriter : IDisposable
{
    private const int HeaderSize = 44;
    private readonly FileStream stream;
    private readonly int sampleRate;
    private readonly int channels;
    private long dataBytes;
    private bool disposed;

    public WavFileWriter(string path, int sampleRate = 16_000, int channels = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        this.sampleRate = sampleRate;
        this.channels = channels;
        this.Path = path;
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Storage.AppDataPaths.EnsurePrivateDirectory(directory);
        }

        this.stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        this.WriteHeader();
        Storage.AppDataPaths.RestrictFile(path);
    }

    public string Path { get; }

    /// <summary>Samples per channel written so far.</summary>
    public long SamplesWritten => this.dataBytes / 2 / this.channels;

    /// <summary>Writes interleaved samples (a whole number of frames for multi-channel files).</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 2];
        AudioConversion.FloatToPcm16(samples, bytes);
        this.stream.Seek(HeaderSize + this.dataBytes, SeekOrigin.Begin);
        this.stream.Write(bytes);
        this.dataBytes += bytes.Length;
    }

    /// <summary>
    /// Repairs a working WAV whose writer never finished (crash or kill): sets the RIFF and data sizes
    /// from the file length. Returns false if the file is not a 44-byte-header PCM16 file written by
    /// this class, or is already correct.
    /// </summary>
    public static bool RepairHeader(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (file.Length < HeaderSize)
        {
            return false;
        }

        Span<byte> h = stackalloc byte[HeaderSize];
        file.ReadExactly(h);
        if (!h[..4].SequenceEqual("RIFF"u8) || !h[36..40].SequenceEqual("data"u8) || BinaryPrimitives.ReadUInt16LittleEndian(h[34..]) != 16)
        {
            return false;
        }

        var frameBytes = BinaryPrimitives.ReadUInt16LittleEndian(h[32..]);
        if (frameBytes == 0)
        {
            return false;
        }

        var data = (file.Length - HeaderSize) / frameBytes * frameBytes;
        if (BinaryPrimitives.ReadUInt32LittleEndian(h[40..]) == (uint)data)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)Math.Min(uint.MaxValue, 36 + data));
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], (uint)Math.Min(uint.MaxValue, data));
        file.Seek(0, SeekOrigin.Begin);
        file.Write(h);
        file.SetLength(HeaderSize + data);
        return true;
    }

    public void Flush()
    {
        this.WriteHeader();
        this.stream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.WriteHeader();
        this.stream.Dispose();
    }

    private void WriteHeader()
    {
        Span<byte> h = stackalloc byte[HeaderSize];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)Math.Min(uint.MaxValue, 36 + this.dataBytes));
        "WAVEfmt "u8.CopyTo(h[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], (ushort)this.channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)this.sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)(this.sampleRate * 2 * this.channels));
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)(2 * this.channels));
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 16);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], (uint)Math.Min(uint.MaxValue, this.dataBytes));
        this.stream.Seek(0, SeekOrigin.Begin);
        this.stream.Write(h);
    }
}

/// <summary>
/// Portable decoder for RIFF/WAVE (PCM 8/16/24/32-bit and 32-bit float). It needs no external
/// tools, so WAV import and reruns from the session's working audio always work.
/// </summary>
public sealed class WavAudioDecoder : IAudioDecoder
{
    private const int FramesPerRead = 4_096;

    public ValueTask<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var info = ReadHeader(path);
        var duration = info.Channels > 0 && info.SampleRate > 0
            ? TimeSpan.FromSeconds((double)info.DataLength / info.BytesPerFrame / info.SampleRate)
            : (TimeSpan?)null;
        return ValueTask.FromResult(new MediaProbeResult(
            "wav",
            duration,
            [new MediaStreamInfo(0, info.IsFloat ? "pcm_f32le" : $"pcm_s{info.BitsPerSample}le", info.SampleRate, info.Channels, null, null)]));
    }

    public async IAsyncEnumerable<AudioFrame> DecodeAsync(
        string path,
        int streamIndex,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (streamIndex != 0)
        {
            throw new MediaDecodeException("no-such-stream", "WAV files have a single audio stream.");
        }

        var info = ReadHeader(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        file.Seek(info.DataOffset, SeekOrigin.Begin);
        var remaining = Math.Min(info.DataLength, file.Length - info.DataOffset);
        var buffer = new byte[FramesPerRead * info.BytesPerFrame];
        long sequence = 0;
        long offset = 0;
        while (remaining >= info.BytesPerFrame)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var want = (int)Math.Min(buffer.Length, remaining) / info.BytesPerFrame * info.BytesPerFrame;
            var read = await file.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
            read -= read % info.BytesPerFrame;
            if (read <= 0)
            {
                yield break;
            }

            var floats = ToFloat(buffer.AsSpan(0, read), info);
            yield return AudioFrame.CopyFrom(floats, new AudioFormat(info.SampleRate, info.Channels, AudioSampleFormat.Float32), sequence++, offset);
            offset += floats.Length / info.Channels;
            remaining -= read;
        }
    }

    private static float[] ToFloat(ReadOnlySpan<byte> data, WavInfo info)
    {
        var bytesPer = info.BitsPerSample / 8;
        var count = data.Length / bytesPer;
        var result = new float[count];
        for (var i = 0; i < count; i++)
        {
            var s = data.Slice(i * bytesPer, bytesPer);
            result[i] = (info.IsFloat, info.BitsPerSample) switch
            {
                (true, 32) => BinaryPrimitives.ReadSingleLittleEndian(s),
                (false, 8) => (s[0] - 128) / 128f,
                (false, 16) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                (false, 24) => (((s[2] << 24) | (s[1] << 16) | (s[0] << 8)) >> 8) / 8388608f,
                (false, 32) => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                _ => throw new MediaDecodeException("unsupported-format", "Unsupported WAV sample format.")
            };
        }

        return result;
    }

    private readonly record struct WavInfo(int Channels, int SampleRate, int BitsPerSample, bool IsFloat, long DataOffset, long DataLength)
    {
        public int BytesPerFrame => this.Channels * (this.BitsPerSample / 8);
    }

    private static WavInfo ReadHeader(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> riff = stackalloc byte[12];
            if (file.Read(riff) < 12 || !riff[..4].SequenceEqual("RIFF"u8) || !riff[8..12].SequenceEqual("WAVE"u8))
            {
                throw new MediaDecodeException("unsupported-format", "The file is not a RIFF/WAVE file.");
            }

            int channels = 0, rate = 0, bits = 0;
            var isFloat = false;
            Span<byte> chunk = stackalloc byte[8];
            Span<byte> fmt = stackalloc byte[16];
            Span<byte> sub = stackalloc byte[2];
            while (file.Read(chunk) == 8)
            {
                var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                if (chunk[..4].SequenceEqual("fmt "u8))
                {
                    file.ReadExactly(fmt);
                    var tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                    rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                    isFloat = tag == 3;
                    if (tag == 0xFFFE)
                    {
                        // WAVE_FORMAT_EXTENSIBLE: sub-format GUID starts at offset 24 of the chunk body.
                        file.Seek(8, SeekOrigin.Current);
                        file.ReadExactly(sub);
                        isFloat = BinaryPrimitives.ReadUInt16LittleEndian(sub) == 3;
                        file.Seek((long)size - 16 - 8 - 2, SeekOrigin.Current);
                    }
                    else if (tag is not (1 or 3))
                    {
                        throw new MediaDecodeException("unsupported-format", "Compressed WAV files are not supported without FFmpeg.");
                    }
                    else
                    {
                        file.Seek((long)size - 16, SeekOrigin.Current);
                    }
                }
                else if (chunk[..4].SequenceEqual("data"u8))
                {
                    if (channels == 0 || rate == 0)
                    {
                        throw new MediaDecodeException("corrupt", "The WAV data chunk appears before its format chunk.");
                    }

                    // A crash-truncated or streamed file can have a bad size; trust the file length.
                    var length = size == uint.MaxValue || file.Position + size > file.Length ? file.Length - file.Position : size;
                    return new WavInfo(channels, rate, bits, isFloat, file.Position, length);
                }
                else
                {
                    file.Seek(size + (size & 1), SeekOrigin.Current);
                }
            }

            throw new MediaDecodeException("corrupt", "The WAV file has no audio data.");
        }
        catch (EndOfStreamException ex)
        {
            throw new MediaDecodeException("corrupt", "The WAV file is truncated.", ex);
        }
        catch (IOException ex)
        {
            throw new MediaDecodeException("unreadable", "The file could not be read.", ex);
        }
    }
}
