using System.Diagnostics;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Platforms.Speech;
using Xunit.Abstractions;

namespace PrimeDictate.Core.Tests;

/// <summary>
/// Runs a real ggml model through <see cref="WhisperNetProvider"/>. Does nothing unless <c>PRIMEDICTATE_WHISPERNET_MODEL</c> (a .bin path)
/// and <c>PRIMEDICTATE_WHISPERNET_WAV</c> (16 kHz 16-bit mono WAV) are set, so the default test run never needs a model. Optional
/// <c>PRIMEDICTATE_WHISPERNET_DEVICE</c> is cpu, gpu or auto (default gpu). Reports timings and the runtime, never the recognized text.
/// </summary>
public sealed class WhisperNetRealRunTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Transcribes_a_wav_with_a_real_model_when_asked_to()
    {
        var modelPath = Environment.GetEnvironmentVariable("PRIMEDICTATE_WHISPERNET_MODEL");
        var wavPath = Environment.GetEnvironmentVariable("PRIMEDICTATE_WHISPERNET_WAV");
        if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(wavPath))
        {
            return;
        }

        var device = WhisperNetDevicePreferences.Parse(Environment.GetEnvironmentVariable(WhisperNetRuntime.EnvironmentVariable) ?? "gpu");
        var notices = new List<string>();
        WhisperNetRuntime.Configure(device, notices.Add);

        var option = SpeechModelLocator.FindWhisperNet(Path.GetFileNameWithoutExtension(modelPath).Replace("ggml-", string.Empty))
            ?? throw new InvalidOperationException("The model file name is not a catalog model.");
        var model = new InstalledSpeechModel(LegacyBackend.WhisperNet, option.Id, option.DisplayName, Path.GetFullPath(modelPath), option.Id.EndsWith(".en", StringComparison.Ordinal));
        var samples = ReadPcm16Mono16k(wavPath);
        output.WriteLine($"Audio: {samples.Length / 16000d:N1} s; requested device: {device}");

        await using var provider = new WhisperNetProvider(model);
        var first = Stopwatch.StartNew();
        var segments = await provider.RecognizeWindowAsync(samples, "en", CancellationToken.None);
        first.Stop();
        output.WriteLine($"First call (includes model load): {first.ElapsedMilliseconds} ms, {segments.Count} segment(s), {segments.Sum(s => s.Text.Length)} characters");
        output.WriteLine($"Runtime: {provider.Runtime.EffectiveBackend} (requested {provider.Runtime.RequestedBackend}); fallback: {provider.Runtime.FallbackReason ?? "none"}");

        // A second provider on the same file must reuse the loaded model, not load it again.
        await using var second = new WhisperNetProvider(model);
        Assert.Equal(1, WhisperNetProvider.SharedModels.LeaseCount(model.Directory));
        var warm = Stopwatch.StartNew();
        var again = await second.RecognizeWindowAsync(samples, "en", CancellationToken.None);
        warm.Stop();
        Assert.Equal(1, WhisperNetProvider.SharedModels.Count);
        Assert.Equal(2, WhisperNetProvider.SharedModels.LeaseCount(model.Directory));
        output.WriteLine($"Second provider, same model (shared load): {warm.ElapsedMilliseconds} ms, {again.Count} segment(s)");

        var timed = Stopwatch.StartNew();
        _ = await provider.RecognizeWindowAsync(samples, "en", CancellationToken.None);
        timed.Stop();
        output.WriteLine($"Warm transcribe: {timed.ElapsedMilliseconds} ms");
        foreach (var notice in notices)
        {
            output.WriteLine("Notice: " + notice);
        }

        Assert.NotEmpty(segments);
    }

    private static float[] ReadPcm16Mono16k(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            var size = BitConverter.ToInt32(bytes, offset + 4);
            if (id == "data")
            {
                var count = Math.Min(size, bytes.Length - offset - 8) / 2;
                var samples = new float[count];
                for (var i = 0; i < count; i++)
                {
                    samples[i] = BitConverter.ToInt16(bytes, offset + 8 + (i * 2)) / 32768f;
                }

                return samples;
            }

            offset += 8 + size + (size & 1);
        }

        throw new InvalidDataException("No data chunk in the WAV.");
    }
}
