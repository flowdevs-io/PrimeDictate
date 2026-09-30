using System.Diagnostics;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Platforms.Nemotron;

/// <summary>
/// Runs <c>nemo-speech diarize</c> over the system-audio channel of a finished meeting. The live socket only reports
/// one speaker per word; this whole-file pass keeps the diarizer's per-speaker segments, which may overlap.
/// The command line was checked against the real CUDA worker at the pinned commit
/// (<c>diarize INPUT --model GGUF --device cuda:0 --format json</c>, 16 kHz mono WAV in). Any failure returns null and
/// the timeline keeps the live view.
/// </summary>
public static class NemotronDiarizer
{
    /// <summary>
    /// Offline (full attention over the whole recording) is the more accurate mode but the model can only hold about
    /// 6.6 minutes; longer recordings use the default streaming mode, which has no length limit.
    /// </summary>
    public static readonly TimeSpan OfflineLimit = TimeSpan.FromMinutes(6);

    public static IReadOnlyList<string> BuildArguments(string wavPath, string diarizerModelPath, string device, bool offline = false)
    {
        var args = new List<string> { "diarize", wavPath, "--model", diarizerModelPath, "--device", device, "--format", "json" };
        if (offline)
        {
            args.Add("--offline");
        }

        return args;
    }

    /// <summary>Copies one channel of a multi-channel WAV to a mono 16 kHz WAV.</summary>
    public static async Task ExtractChannelAsync(string stereoPath, int channel, string monoPath, CancellationToken cancellationToken)
    {
        var decoder = new WavAudioDecoder();
        using var writer = new WavFileWriter(monoPath, 16_000, 1);
        await foreach (var frame in decoder.DecodeAsync(stereoPath, 0, cancellationToken).ConfigureAwait(false))
        {
            var channels = frame.Format.Channels;
            var samples = frame.Samples.Span;
            var mono = new float[samples.Length / channels];
            for (var i = 0; i < mono.Length; i++)
            {
                mono[i] = samples[(i * channels) + channel];
            }

            writer.Write(mono);
        }
    }

    /// <returns>The overlay, or null when the worker could not produce one (the reason is in <paramref name="error"/>).</returns>
    public static async Task<(DiarizationOverlay? Overlay, string? Error)> RunAsync(
        string executablePath,
        string diarizerModelPath,
        string device,
        string stereoWavPath,
        bool offline,
        string workDirectory,
        TimeSpan timeout,
        IEnumerable<string>? extraPathDirectories,
        CancellationToken cancellationToken)
    {
        var monoPath = Path.Combine(workDirectory, "system-channel-16k-mono.wav");
        try
        {
            await ExtractChannelAsync(stereoWavPath, 1, monoPath, cancellationToken).ConfigureAwait(false);
            var info = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in BuildArguments(monoPath, diarizerModelPath, device, offline))
            {
                info.ArgumentList.Add(arg);
            }

            var extra = (extraPathDirectories ?? []).Where(Directory.Exists).ToList();
            if (extra.Count > 0)
            {
                var current = info.Environment.TryGetValue("PATH", out var path) ? path : Environment.GetEnvironmentVariable("PATH");
                info.Environment["PATH"] = string.Join(Path.PathSeparator, extra.Append(current ?? string.Empty));
            }

            NemotronLog.ScrubEnvironment(info);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("The diarizer did not start.");
            ChildProcessJob.TryAdd(process);
            var source = $"[diarizer pid={process.Id} {device}{(offline ? " offline" : string.Empty)}]";
            NemotronLog.Event(source, "started");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var errors = process.StandardError.ReadToEndAsync(limit.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            var text = await output.ConfigureAwait(false);
            foreach (var line in (await errors.ConfigureAwait(false)).Split('\n'))
            {
                NemotronLog.WorkerLine(source, line);
            }

            NemotronLog.Event(source, $"exited with code {process.ExitCode}");
            var segments = DiarizationOverlay.ParseJson(text);
            if (process.ExitCode != 0 || segments.Count == 0)
            {
                var reason = (await errors.ConfigureAwait(false)).Trim();
                return (null, process.ExitCode != 0
                    ? $"The diarizer exited with code {process.ExitCode}. {Truncate(reason)}".Trim()
                    : "The diarizer found no speech in the system audio.");
            }

            return (new DiarizationOverlay(segments, offline ? "offline" : "streaming"), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "The diarizer took too long and was stopped.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or MediaDecodeException)
        {
            return (null, ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(monoPath);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "…";
}
