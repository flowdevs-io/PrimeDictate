using System.Diagnostics;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Providers;
using PrimeDictate.Platforms.Audio;

/// <summary>
/// Self-contained skew measurement: opens the same combined lease the app uses, plays a known noise-burst
/// train through the default output, and finds that train in each channel with a matched filter.
/// </summary>
internal static class Probe
{
    private const int PlayRate = 48_000;

    public static async Task<int> RunAsync(string? saveWavPath)
    {
        var system = SystemAudioSources.TryCreate(out var reason);
        if (system is null)
        {
            Console.Error.WriteLine($"System audio capture unavailable: {reason}");
            return 2;
        }

        MiniAudioCaptureSource mic;
        try
        {
            mic = new MiniAudioCaptureSource();
        }
        catch (AudioSourceException ex)
        {
            Console.Error.WriteLine($"Microphone unavailable: {ex.Message}");
            return 2;
        }

        var reference = ReferenceLocator.NoiseBurstTrain(16_000);
        var trainSeconds = reference.Length / 16_000.0;
        var left = new List<float>();
        var right = new List<float>();
        var lease = await new CombinedAudioSource(mic, system).OpenAsync(null, default);
        await using var _ = lease;
        Console.WriteLine($"Microphone: {lease.DeviceName}");
        Console.WriteLine($"Opened combined lease at {lease.Format.SampleRate} Hz, {lease.Format.Channels} ch. Playing a {trainSeconds:0.0} s noise train through the default output; keep the speakers on and turn them up.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5 + trainSeconds + 2.5));
        var clock = Stopwatch.StartNew();
        long playCallSample = -1;
        var playTask = Task.Run(async () =>
        {
            await Task.Delay(1500);
            playCallSample = left.Count;
            await TonePlayer.PlayAsync(reference, 16_000, PlayRate);
        });

        try
        {
            await foreach (var frame in lease.ReadFramesAsync(cts.Token))
            {
                var s = frame.Samples.Span;
                for (var i = 0; i + 1 < s.Length; i += 2)
                {
                    left.Add(s[i]);
                    right.Add(s[i + 1]);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        await playTask;
        var lc = lease as ICombinedAudioCaptureLease;
        var micArr = left.ToArray();
        var sysArr = right.ToArray();
        Console.WriteLine($"Captured {micArr.Length / 16_000.0:0.0} s. Padded silence: mic {lc?.MicrophonePaddedSamples ?? 0} samples, system {lc?.SystemAudioPaddedSamples ?? 0} samples. System opened {lc?.SystemOpenedAfterMicrophone.TotalMilliseconds:0} ms after mic.");
        if (saveWavPath is not null)
        {
            using var writer = new WavFileWriter(saveWavPath, 16_000, 2);
            var inter = new float[micArr.Length * 2];
            for (var i = 0; i < micArr.Length; i++)
            {
                inter[2 * i] = micArr[i];
                inter[(2 * i) + 1] = sysArr[i];
            }

            writer.Write(inter);
            Console.WriteLine($"Saved {saveWavPath}");
        }

        var micMatch = ReferenceLocator.Locate(micArr, reference);
        var sysMatch = ReferenceLocator.Locate(sysArr, reference);
        Report("system (loopback)", sysMatch, playCallSample);
        Report("microphone      ", micMatch, playCallSample);
        if (!sysMatch.IsReliable)
        {
            Console.WriteLine("RESULT: unusable. The loopback did not capture the test train; check that the default output device is the one playing sound.");
            return 1;
        }

        if (!micMatch.IsReliable)
        {
            Console.WriteLine("RESULT: unusable. The microphone did not hear the test train. Turn the speakers up, move the mic closer, or use a cable from output to line-in.");
            return 1;
        }

        var lagMs = (micMatch.SampleIndex - sysMatch.SampleIndex) * 1000.0 / 16_000;
        Console.WriteLine($"RESULT: microphone is {lagMs:+0.0;-0.0;0.0} ms after system audio (positive = mic later).");
        Console.WriteLine("This includes the speaker path: output device latency (Bluetooth can add 100 to 300 ms) plus ~3 ms per metre of air. Wired speakers and a mic within a metre should show under about 60 ms if capture is aligned.");
        return 0;
    }

    private static void Report(string name, ReferenceMatch m, long playCallSample) =>
        Console.WriteLine($"  {name}: train found at {m.SampleIndex / 16.0:0} ms (play was called at {playCallSample / 16.0:0} ms), correlation {m.Correlation:0.00}, peak/sidelobe {m.PeakToSidelobe:0.0}{(m.IsReliable ? "" : "  <- not reliable")}");
}
