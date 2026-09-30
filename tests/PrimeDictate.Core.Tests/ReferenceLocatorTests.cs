using PrimeDictate.Core.Audio;

namespace PrimeDictate.Core.Tests;

public sealed class ReferenceLocatorTests
{
    [Theory]
    [InlineData(0, 1.0f)]
    [InlineData(4321, 1.0f)]
    [InlineData(20000, 0.02f)]
    public void FindsTrainAtKnownOffsetEvenWhenFaintAndNoisy(int offset, float gain)
    {
        var train = ReferenceLocator.NoiseBurstTrain(16_000);
        var rng = new Random(7);
        var recording = new float[train.Length + 40_000];
        for (var i = 0; i < recording.Length; i++)
        {
            recording[i] = (float)(rng.NextDouble() - 0.5) * 0.02f; // room noise
        }

        for (var i = 0; i < train.Length; i++)
        {
            recording[offset + i] += train[i] * gain;
        }

        var match = ReferenceLocator.Locate(recording, train);
        Assert.Equal(offset, match.SampleIndex);
        Assert.True(match.IsReliable, $"corr {match.Correlation}, psr {match.PeakToSidelobe}");
    }

    [Fact]
    public void ReportsUnreliableWhenTrainIsAbsent()
    {
        var train = ReferenceLocator.NoiseBurstTrain(16_000);
        var rng = new Random(9);
        var recording = new float[train.Length + 10_000];
        for (var i = 0; i < recording.Length; i++)
        {
            recording[i] = (float)(rng.NextDouble() - 0.5);
        }

        Assert.False(ReferenceLocator.Locate(recording, train).IsReliable);
    }
}
