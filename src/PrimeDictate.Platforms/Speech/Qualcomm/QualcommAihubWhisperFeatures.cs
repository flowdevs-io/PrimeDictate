using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PrimeDictate.Platforms.Speech.Qualcomm;

/// <summary>The log-mel front end the AI Hub Whisper encoder expects: a 30 s window, 80 mel bins, 3000 frames, half precision.</summary>
internal sealed class QualcommAihubWhisperFeatureExtractor
{
    private const int SampleRate = 16_000;
    private const int ChunkSamples = SampleRate * 30;
    private const int Nfft = 400;
    private const int HopLength = 160;
    private const int MelBins = 80;
    private const int Frames = 3000;
    private const int FrequencyBins = Nfft / 2 + 1;
    private const float LogFloor = 1e-10f;

    private static readonly Lazy<float[]> HannWindow = new(CreateHannWindow);
    private static readonly Lazy<float[,]> MelFilters = new(CreateMelFilters);
    private static readonly Lazy<float[,]> CosTable = new(() => CreateTrigTable(MathF.Cos));
    private static readonly Lazy<float[,]> SinTable = new(() => CreateTrigTable(MathF.Sin));

    public DenseTensor<Float16> Extract(float[] samples, CancellationToken cancellationToken)
    {
        var padded = new float[ChunkSamples];
        Array.Copy(samples, padded, Math.Min(samples.Length, padded.Length));

        var power = new float[FrequencyBins];
        var mel = new float[MelBins * Frames];
        var silenceLog = MathF.Log10(LogFloor);
        Array.Fill(mel, silenceLog);
        var maxLog = silenceLog;
        var framesToCompute = GetFrameCountToCompute(samples.Length);
        var window = HannWindow.Value;
        var filters = MelFilters.Value;
        var cos = CosTable.Value;
        var sin = SinTable.Value;

        for (var frame = 0; frame < framesToCompute; frame++)
        {
            if ((frame & 0x1f) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var frameStart = frame * HopLength - Nfft / 2;
            for (var frequency = 0; frequency < FrequencyBins; frequency++)
            {
                var real = 0f;
                var imaginary = 0f;
                for (var n = 0; n < Nfft; n++)
                {
                    var sample = GetReflectedSample(padded, frameStart + n) * window[n];
                    real += sample * cos[frequency, n];
                    imaginary -= sample * sin[frequency, n];
                }

                power[frequency] = real * real + imaginary * imaginary;
            }

            for (var melBin = 0; melBin < MelBins; melBin++)
            {
                var sum = 0f;
                for (var frequency = 0; frequency < FrequencyBins; frequency++)
                {
                    sum += filters[melBin, frequency] * power[frequency];
                }

                var logValue = MathF.Log10(MathF.Max(sum, LogFloor));
                mel[melBin * Frames + frame] = logValue;
                if (logValue > maxLog)
                {
                    maxLog = logValue;
                }
            }
        }

        var clampFloor = maxLog - 8f;
        var features = new Float16[MelBins * Frames];
        for (var i = 0; i < mel.Length; i++)
        {
            var normalized = (MathF.Max(mel[i], clampFloor) + 4f) / 4f;
            features[i] = (Float16)normalized;
        }

        return new DenseTensor<Float16>(features, [1, MelBins, Frames]);
    }

    private static int GetFrameCountToCompute(int sampleCount)
    {
        if (sampleCount <= 0)
        {
            return 1;
        }

        return Math.Clamp((sampleCount + Nfft) / HopLength + 1, 1, Frames);
    }

    private static float GetReflectedSample(float[] samples, int index)
    {
        if (index < 0)
        {
            index = -index;
        }
        else if (index >= samples.Length)
        {
            index = 2 * samples.Length - index - 2;
        }

        if (index < 0 || index >= samples.Length)
        {
            return 0f;
        }

        return samples[index];
    }

    private static float[] CreateHannWindow()
    {
        var window = new float[Nfft];
        for (var i = 0; i < window.Length; i++)
        {
            window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / Nfft));
        }

        return window;
    }

    private static float[,] CreateTrigTable(Func<float, float> trig)
    {
        var table = new float[FrequencyBins, Nfft];
        for (var frequency = 0; frequency < FrequencyBins; frequency++)
        {
            for (var n = 0; n < Nfft; n++)
            {
                table[frequency, n] = trig(2f * MathF.PI * frequency * n / Nfft);
            }
        }

        return table;
    }

    private static float[,] CreateMelFilters()
    {
        var filters = new float[MelBins, FrequencyBins];
        var fftFrequencies = new float[FrequencyBins];
        for (var i = 0; i < FrequencyBins; i++)
        {
            fftFrequencies[i] = i * SampleRate / (float)Nfft;
        }

        var minMel = HertzToMel(0f);
        var maxMel = HertzToMel(SampleRate / 2f);
        var melPoints = new float[MelBins + 2];
        for (var i = 0; i < melPoints.Length; i++)
        {
            melPoints[i] = MelToHertz(minMel + (maxMel - minMel) * i / (melPoints.Length - 1));
        }

        for (var melBin = 0; melBin < MelBins; melBin++)
        {
            var lower = melPoints[melBin];
            var center = melPoints[melBin + 1];
            var upper = melPoints[melBin + 2];
            var enorm = 2f / (upper - lower);

            for (var frequency = 0; frequency < FrequencyBins; frequency++)
            {
                var hz = fftFrequencies[frequency];
                var lowerSlope = (hz - lower) / (center - lower);
                var upperSlope = (upper - hz) / (upper - center);
                filters[melBin, frequency] = MathF.Max(0f, MathF.Min(lowerSlope, upperSlope)) * enorm;
            }
        }

        return filters;
    }

    private static float HertzToMel(float hertz)
    {
        const float minLogHertz = 1000f;
        const float minLogMel = 15f;
        var linearMel = 3f * hertz / 200f;
        if (hertz < minLogHertz)
        {
            return linearMel;
        }

        return minLogMel + MathF.Log(hertz / minLogHertz) / (MathF.Log(6.4f) / 27f);
    }

    private static float MelToHertz(float mel)
    {
        const float minLogHertz = 1000f;
        const float minLogMel = 15f;
        if (mel < minLogMel)
        {
            return 200f * mel / 3f;
        }

        return minLogHertz * MathF.Exp((mel - minLogMel) * (MathF.Log(6.4f) / 27f));
    }
}

/// <summary>Turns Whisper token ids back into text with the multilingual tiktoken table. Special tokens are dropped.</summary>
internal sealed class WhisperTiktokenDecoder
{
    private const int SpecialTokenStart = 50257;
    private readonly Dictionary<int, byte[]> tokenBytesById;

    private WhisperTiktokenDecoder(Dictionary<int, byte[]> tokenBytesById)
    {
        this.tokenBytesById = tokenBytesById;
    }

    public static WhisperTiktokenDecoder Load(string tokenizerPath)
    {
        var tokenBytesById = new Dictionary<int, byte[]>();
        foreach (var line in File.ReadLines(tokenizerPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var tokenId))
            {
                continue;
            }

            tokenBytesById[tokenId] = DecodeTokenBytes(parts[0]);
        }

        if (tokenBytesById.Count == 0)
        {
            throw new InvalidOperationException("Whisper tokenizer file is empty or invalid.");
        }

        return new WhisperTiktokenDecoder(tokenBytesById);
    }

    private static byte[] DecodeTokenBytes(string encodedToken)
    {
        try
        {
            return Convert.FromBase64String(encodedToken);
        }
        catch (FormatException) when (string.Equals(encodedToken, "=", StringComparison.Ordinal))
        {
            return Encoding.UTF8.GetBytes(encodedToken);
        }
    }

    public string Decode(IEnumerable<int> tokenIds)
    {
        using var bytes = new MemoryStream();
        foreach (var tokenId in tokenIds)
        {
            if (tokenId >= SpecialTokenStart)
            {
                continue;
            }

            if (this.tokenBytesById.TryGetValue(tokenId, out var tokenBytes))
            {
                bytes.Write(tokenBytes, 0, tokenBytes.Length);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray()).Trim();
    }
}
