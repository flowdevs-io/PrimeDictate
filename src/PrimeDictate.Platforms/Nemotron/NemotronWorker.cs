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

    Uri BaseAddress { get; }

    string ApiKey { get; }

    /// <summary>The device asked for, for example "cpu" or "cuda:0".</summary>
    string RequestedBackend => "cpu";

    /// <summary>The backend the worker itself reported (from its startup output), or "cpu" for stand-ins.</summary>
    string EffectiveBackend => "cpu";

    /// <summary>Set when the requested device could not be used and the worker runs elsewhere.</summary>
    string? FallbackReason => null;
}

public sealed class NemotronWorker : IAsyncDisposable, INemotronEndpoint
{
    private readonly Process process;
    private readonly HttpClient http;

    private static readonly System.Text.RegularExpressions.Regex BackendLine = new(@"backend=([A-Za-z]+\d*)", System.Text.RegularExpressions.RegexOptions.Compiled);

    private volatile string? reportedBackend;
    private volatile bool diarizerReportedOn;

    /// <summary>True when the worker's startup line said <c>diarization=on</c>, confirming the diarizer loaded.</summary>
    public bool DiarizerConfirmed => this.diarizerReportedOn;

    private NemotronWorker(Process process, int port, string apiKey, bool hasDiarizer, string requestedDevice)
    {
        this.RequestedBackend = requestedDevice;
        this.process = process;
        this.Port = port;
        this.ApiKey = apiKey;
        this.HasDiarizer = hasDiarizer;
        this.http = new HttpClient { BaseAddress = this.BaseAddress, Timeout = TimeSpan.FromMinutes(10) };
        this.http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
    }

    public int Port { get; }

    public string RequestedBackend { get; set; }

    /// <summary>What the worker printed at startup (<c>backend=CUDA0</c>), normalised; "unknown" if it printed nothing.</summary>
    public string EffectiveBackend => NormalizeBackend(this.reportedBackend);

    public string? FallbackReason { get; set; }

    /// <summary>"CPU" becomes "cpu", "CUDA0" becomes "cuda:0"; anything else is kept as reported.</summary>
    public static string NormalizeBackend(string? reported)
    {
        if (string.IsNullOrEmpty(reported))
        {
            return "unknown";
        }

        var m = System.Text.RegularExpressions.Regex.Match(reported, @"^(CUDA|Vulkan)(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? $"{m.Groups[1].Value.ToLowerInvariant()}:{m.Groups[2].Value}" : reported.ToLowerInvariant();
    }

    /// <summary>Only devices that are validated at the pinned commit. Vulkan aborts (NeMo-Speech.cpp#48), so it is refused.</summary>
    public static bool IsSupportedDevice(string device) =>
        System.Text.RegularExpressions.Regex.IsMatch(device, @"^(cpu|cuda:\d)$");

    public string ApiKey { get; }

    public bool HasDiarizer { get; }

    public Uri BaseAddress => new($"http://127.0.0.1:{this.Port}/");

    public HttpClient Client => this.http;

    /// <summary>Builds the exact argument list; exposed so tests can check nothing risky is passed.</summary>
    public static IReadOnlyList<string> BuildArguments(int port, NemotronModelFiles files, string device = "cpu")
    {
        if (!IsSupportedDevice(device))
        {
            throw new ArgumentException($"Unsupported Nemotron device '{device}'. Use cpu or cuda:N.", nameof(device));
        }

        var args = new List<string>
        {
            "serve", "--no-ui", "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--device", device, "--asr-model", files.AsrPath
        };
        if (files.DiarizerPath is not null)
        {
            args.Add("--diar-model");
            args.Add(files.DiarizerPath);
        }

        return args;
    }

    /// <param name="device">"cpu" or "cuda:N".</param>
    /// <param name="extraPathDirectories">
    /// Directories put on the child's PATH, for the CUDA runtime DLLs. Without them a CUDA build exits at once
    /// with 0xC0000135 (DLL not found).
    /// </param>
    public static async Task<NemotronWorker> StartAsync(
        string executablePath,
        NemotronModelFiles files,
        string device,
        TimeSpan readyTimeout,
        CancellationToken cancellationToken,
        IEnumerable<string>? extraPathDirectories = null)
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
        foreach (var arg in BuildArguments(port, files, device))
        {
            info.ArgumentList.Add(arg);
        }

        var extra = (extraPathDirectories ?? []).Where(Directory.Exists).ToList();
        if (extra.Count > 0)
        {
            var current = info.Environment.TryGetValue("PATH", out var path) ? path : Environment.GetEnvironmentVariable("PATH");
            info.Environment["PATH"] = string.Join(Path.PathSeparator, extra.Append(current ?? string.Empty));
        }

        info.Environment["NEMO_SPEECH_HTTP_API_KEY"] = key;
        var process = Process.Start(info) ?? throw new NemotronException("worker-missing", "The Nemotron worker could not be started.");
        // A crash or force-kill of the app must not leave a worker holding the GPU.
        ChildProcessJob.TryAdd(process);
        var worker = new NemotronWorker(process, port, key, files.DiarizerPath is not null, device);
        // Drain output so a full pipe can never stall the worker. Only the backend it reports is kept; the rest
        // is dropped because it may echo audio metadata.
        void Inspect(string? line)
        {
            if (line is null)
            {
                return;
            }

            // Real line (stderr): "[asr] model=.x.gguf head=rnnt backend=CUDA0 diarization=on". It appears at model
            // load, before /ready, so it says what was chosen; /ready says it works.
            if (worker.reportedBackend is null && BackendLine.Match(line) is { Success: true } m)
            {
                worker.reportedBackend = m.Groups[1].Value;
                worker.diarizerReportedOn = line.Contains("diarization=on", StringComparison.Ordinal);
            }
        }

        process.OutputDataReceived += (_, e) => Inspect(e.Data);
        process.ErrorDataReceived += (_, e) => Inspect(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await worker.WaitUntilReadyAsync(readyTimeout, cancellationToken).ConfigureAwait(false);
            // The backend line is printed while the model loads, before /ready; give the output pipe a moment.
            for (var i = 0; i < 20 && worker.reportedBackend is null; i++)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            return worker;
        }
        catch
        {
            await worker.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Starts the worker on the preferred device (<c>auto</c>, <c>cpu</c> or <c>cuda:N</c>). If the GPU worker cannot
    /// start, the CPU worker is used and <see cref="FallbackReason"/> says why; the same text goes to
    /// <paramref name="notice"/>. Nothing falls back silently.
    /// </summary>
    public static async Task<NemotronWorker> StartPreferredAsync(
        string cpuWorkerPath,
        string? cudaWorkerPath,
        NemotronModelFiles files,
        string preference,
        Func<string, IEnumerable<string>> runtimeDirectories,
        Action<string> notice,
        CancellationToken cancellationToken,
        TimeSpan? readyTimeout = null)
    {
        var timeout = readyTimeout ?? TimeSpan.FromSeconds(90);
        var wantsGpu = preference != "cpu";
        var wantsCuda = preference.StartsWith("cuda", StringComparison.Ordinal);
        var gpuDevice = wantsCuda && IsSupportedDevice(preference) ? preference : "cuda:0";
        string? fallback = null;
        if (wantsGpu && preference != "auto" && !wantsCuda)
        {
            fallback = $"'{preference}' is not a supported Nemotron device (use auto, cpu or cuda:N). Using the CPU instead.";
        }
        else if (wantsGpu && cudaWorkerPath is not null)
        {
            try
            {
                var gpu = await StartAsync(cudaWorkerPath, files, gpuDevice, timeout, cancellationToken, runtimeDirectories(cudaWorkerPath)).ConfigureAwait(false);
                notice($"Nemotron is running on {gpu.EffectiveBackend}." + DiarizerWarning(gpu));
                return gpu;
            }
            catch (Exception ex) when (ex is NemotronException or System.ComponentModel.Win32Exception)
            {
                fallback = $"Nemotron could not start on the GPU ({ex.Message}) and is using the CPU instead.";
            }
        }
        else if (wantsCuda)
        {
            fallback = "No CUDA build of the Nemotron worker was found (expected models\\nemotron\\cuda\\nemo-speech). Using the CPU instead.";
        }

        var cpu = await StartAsync(cpuWorkerPath, files, "cpu", timeout, cancellationToken, runtimeDirectories(cpuWorkerPath)).ConfigureAwait(false);
        if (fallback is not null)
        {
            cpu.RequestedBackend = gpuDevice;
            cpu.FallbackReason = fallback;
            notice(fallback);
        }
        else
        {
            notice($"Nemotron is running on {cpu.EffectiveBackend}." + DiarizerWarning(cpu));
        }

        return cpu;
    }

    private static string DiarizerWarning(NemotronWorker worker) =>
        worker.HasDiarizer && !worker.DiarizerConfirmed ? " The speaker model was supplied but the worker did not confirm it loaded." : string.Empty;

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
        -1073741515 => "The Nemotron worker could not start because a required DLL was not found (for a CUDA build: the CUDA runtime folder is not on PATH).",
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
