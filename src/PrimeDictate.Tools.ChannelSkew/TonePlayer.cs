using System.Diagnostics;
using NAudio.Wave;

/// <summary>Plays a mono float signal on the default output device: WASAPI on Windows, paplay elsewhere.</summary>
internal static class TonePlayer
{
    public static async Task PlayAsync(float[] mono, int sourceRate, int playRate)
    {
        var up = new float[(long)mono.Length * playRate / sourceRate];
        for (var i = 0; i < up.Length; i++)
        {
            var pos = (double)i * sourceRate / playRate;
            var i0 = (int)pos;
            var i1 = Math.Min(i0 + 1, mono.Length - 1);
            var f = (float)(pos - i0);
            up[i] = (mono[i0] * (1 - f)) + (mono[i1] * f);
        }

        if (OperatingSystem.IsWindows())
        {
            var bytes = new byte[up.Length * 2 * sizeof(float)];
            for (var i = 0; i < up.Length; i++)
            {
                var b = BitConverter.GetBytes(up[i] * 0.9f);
                b.CopyTo(bytes, i * 8);
                b.CopyTo(bytes, (i * 8) + 4);
            }

            using var stream = new RawSourceWaveStream(new MemoryStream(bytes), WaveFormat.CreateIeeeFloatWaveFormat(playRate, 2));
            using var output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 50);
            output.Init(stream);
            output.Play();
            while (output.PlaybackState == PlaybackState.Playing)
            {
                await Task.Delay(20);
            }

            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"pd-probe-{Environment.ProcessId}.wav");
        try
        {
            using (var w = new WaveFileWriter(path, new WaveFormat(playRate, 16, 2)))
            {
                foreach (var v in up)
                {
                    var s = (short)(Math.Clamp(v * 0.9f, -1f, 1f) * short.MaxValue);
                    w.WriteByte((byte)(s & 0xFF));
                    w.WriteByte((byte)((s >> 8) & 0xFF));
                    w.WriteByte((byte)(s & 0xFF));
                    w.WriteByte((byte)((s >> 8) & 0xFF));
                }
            }

            using var p = Process.Start(new ProcessStartInfo("paplay", $"\"{path}\"") { UseShellExecute = false })
                          ?? throw new InvalidOperationException("Could not start paplay.");
            await p.WaitForExitAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
