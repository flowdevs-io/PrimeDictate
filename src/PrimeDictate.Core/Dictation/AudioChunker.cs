namespace PrimeDictate.Core.Dictation;

/// <summary>
/// Splits a long recording into windows a batch recognizer can take (Whisper is limited to 30 s),
/// cutting at the quietest 100 ms near the end of each window so words are not sliced in half.
/// </summary>
public static class AudioChunker
{
    private const int Block = SpeechActivityTracker.LevelBlockSamples;

    public static IReadOnlyList<ReadOnlyMemory<float>> Split(ReadOnlyMemory<float> samples, TimeSpan maxWindow, int sampleRate = 16_000)
    {
        var max = Math.Max(Block * 2, (int)(maxWindow.TotalSeconds * sampleRate));
        if (samples.Length <= max)
        {
            return [samples];
        }

        var chunks = new List<ReadOnlyMemory<float>>();
        var start = 0;
        while (samples.Length - start > max)
        {
            var searchFrom = start + max - (sampleRate * 5);
            var cut = start + max;
            var best = double.MaxValue;
            for (var pos = Math.Max(start + Block, searchFrom); pos + Block <= start + max; pos += Block)
            {
                var rms = SpeechActivityTracker.Rms(samples.Span.Slice(pos, Block));
                if (rms < best)
                {
                    best = rms;
                    cut = pos + (Block / 2);
                }
            }

            chunks.Add(samples.Slice(start, cut - start));
            start = cut;
        }

        chunks.Add(samples[start..]);
        return chunks;
    }
}
