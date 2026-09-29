using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PrimeDictate.Platforms.Nemotron;

/// <summary>
/// An app-managed <c>nemo-speech serve</c> child process: loopback only, no web UI, a random
/// bearer key passed through the environment (never the command line), local verified model paths
/// only, CPU device. Disposing kills the process tree.
/// </summary>
/// <summary>What the provider needs from a worker; lets tests stand in a fake local server.</summary>
public interface INemotronEndpoint
{
    HttpClient Client { get; }

    bool HasDiarizer { get; }
}

public sealed class NemotronWorker : IAsyncDisposable, INemotronEndpoint
{
    private readonly Process process;
    private readonly HttpClient http;

    private NemotronWorker(Process process, int port, string apiKey, bool hasDiarizer)
    {
        this.process = process;
        this.Port = port;
        this.ApiKey = apiKey;
        this.HasDiarizer = hasDiarizer;
        this.http = new HttpClient { BaseAddress = this.BaseAddress, Timeout = TimeSpan.FromMinutes(10) };
        this.http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
    }

    public int Port { get; }

    public string ApiKey { get; }

    public bool HasDiarizer { get; }

    public Uri BaseAddress => new($"http://127.0.0.1:{this.Port}/");

    public HttpClient Client => this.http;

    /// <summary>Builds the exact argument list; exposed so tests can check nothing risky is passed.</summary>
    public static IReadOnlyList<string> BuildArguments(int port, NemotronModelFiles files)
    {
        var args = new List<string>
        {
            "serve", "--no-ui", "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // CPU is the only validated backend at the pinned commit; Vulkan aborts while loading.
            "--device", "cpu", "--asr-model", files.AsrPath
        };
        if (files.DiarizerPath is not null)
        {
            args.Add("--diar-model");
            args.Add(files.DiarizerPath);
        }

        return args;
    }

    public static async Task<NemotronWorker> StartAsync(string executablePath, NemotronModelFiles files, TimeSpan readyTimeout, CancellationToken cancellationToken)
    {
        if (!File.Exists(executablePath))
        {
            throw new NemotronException("worker-missing", "The Nemotron worker (nemo-speech) was not found.");
        }

        var port = FreeLoopbackPort();
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var info = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in BuildArguments(port, files))
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["NEMO_SPEECH_HTTP_API_KEY"] = key;
        var process = Process.Start(info) ?? throw new NemotronException("worker-missing", "The Nemotron worker could not be started.");
        // Drain output so a full pipe can never stall the worker; it is not logged because it may echo audio metadata.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var worker = new NemotronWorker(process, port, key, files.DiarizerPath is not null);
        try
        {
            await worker.WaitUntilReadyAsync(readyTimeout, cancellationToken).ConfigureAwait(false);
            return worker;
        }
        catch
        {
            await worker.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        using var probe = new HttpClient { BaseAddress = this.BaseAddress, Timeout = TimeSpan.FromSeconds(2) };
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (this.process.HasExited)
            {
                throw new NemotronException("worker-exited", DescribeExit(this.process.ExitCode));
            }

            try
            {
                using var response = await probe.GetAsync("ready", cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new NemotronException("worker-timeout", "The Nemotron worker did not become ready in time.");
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Exit codes seen from the pinned runtime: 1 bad or corrupt model, 2 download failure, 3 missing file.</summary>
    public static string DescribeExit(int code) => code switch
    {
        1 => "The Nemotron worker rejected a model file (invalid or corrupt).",
        2 => "The Nemotron worker tried to download a model. This should never happen; report it.",
        3 => "The Nemotron worker could not find a model file.",
        _ => $"The Nemotron worker stopped unexpectedly (exit code {code})."
    };

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public ValueTask DisposeAsync()
    {
        this.http.Dispose();
        try
        {
            if (!this.process.HasExited)
            {
                this.process.Kill(entireProcessTree: true);
                this.process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }

        this.process.Dispose();
        return ValueTask.CompletedTask;
    }
}
