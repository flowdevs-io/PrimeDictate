using System.Diagnostics;

namespace PrimeDictate.Platforms.Speech;

/// <summary>A downloadable sherpa-onnx Whisper model. Same ids, folders and archive names as the WPF catalog, so both apps share installs.</summary>
public sealed record WhisperModelOption(string Id, string DisplayName, string Description, long ApproximateBytes, bool Recommended = false)
{
    public string InstallDirectoryName => $"sherpa-onnx-whisper-{this.Id}";

    public string ArchiveFileName => $"{this.InstallDirectoryName}.tar.bz2";

    public Uri DownloadUri => new($"https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/{this.ArchiveFileName}");
}

public sealed record ModelDownloadProgress(string Stage, long BytesDownloaded, long? TotalBytes)
{
    public double? Fraction => this.Stage == "download" && this.TotalBytes is > 0 ? Math.Min(1.0, (double)this.BytesDownloaded / this.TotalBytes.Value) : null;
}

public static class WhisperModelCatalog
{
    public static IReadOnlyList<WhisperModelOption> Options { get; } =
    [
        new("tiny.en", "Tiny English", "The lightest English model. Fast, good for slower laptops and idle wake listening.", 118_071_777, true),
        new("base.en", "Base English", "A good English balance for everyday dictation on CPU.", 208_576_005, true),
        new("distil-small.en", "Distil Small English", "A faster distilled English model for longer sessions.", 453_710_017),
        new("small.en", "Small English", "Higher English accuracy, larger download, more compute.", 635_693_775),
        new("tiny", "Tiny Multilingual", "The lightest model for non-English dictation.", 116_204_861),
        new("base", "Base Multilingual", "A balanced multilingual model.", 207_557_382),
        new("small", "Small Multilingual", "Higher multilingual accuracy, more compute.", 639_387_718)
    ];

    public static string FormatSize(long bytes) => $"{bytes / (1024d * 1024d):N0} MB";
}

/// <summary>
/// Downloads a model archive over HTTPS and unpacks it into <c>{models}/whisper/{folder}</c>. The archive is unpacked in a
/// scratch folder first and only a folder that passes the same validation the recognizer uses is moved into place, so a cut
/// connection or a wrong archive never leaves a half-installed model that looks installed.
/// </summary>
public sealed class WhisperModelDownloader(HttpClient? http = null, Func<string, string, CancellationToken, Task>? extract = null)
{
    private static readonly HttpClient Shared = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient client = http ?? Shared;
    private readonly Func<string, string, CancellationToken, Task> extractArchive = extract ?? ExtractWithTarAsync;

    /// <summary>Overrides where archives come from (tests, mirrors). Null uses the sherpa-onnx release.</summary>
    public Func<WhisperModelOption, Uri>? UriFor { get; init; }

    public async Task<InstalledWhisperModel> DownloadAsync(
        WhisperModelOption option,
        string modelsRoot,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var whisperRoot = Path.Combine(modelsRoot, "whisper");
        Directory.CreateDirectory(whisperRoot);
        var destination = Path.Combine(whisperRoot, option.InstallDirectoryName);
        if (WhisperOnnxModelLocator.TryResolve(destination, out var existing))
        {
            progress?.Report(new ModelDownloadProgress("ready", 1, 1));
            return existing;
        }

        var archive = Path.Combine(whisperRoot, option.ArchiveFileName + ".download");
        var scratch = Path.Combine(whisperRoot, option.InstallDirectoryName + ".extract");
        try
        {
            DeleteQuietly(archive);
            DeleteQuietly(scratch);
            long downloaded = 0;
            long? total = null;
            using (var response = await this.client.GetAsync(this.UriFor?.Invoke(option) ?? option.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var file = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    downloaded += read;
                    progress?.Report(new ModelDownloadProgress("download", downloaded, total));
                }
            }

            if (total is > 0 && downloaded != total)
            {
                throw new IOException($"The download ended early ({downloaded:N0} of {total:N0} bytes).");
            }

            progress?.Report(new ModelDownloadProgress("extract", downloaded, total));
            Directory.CreateDirectory(scratch);
            await this.extractArchive(archive, scratch, cancellationToken).ConfigureAwait(false);

            var extracted = Path.Combine(scratch, option.InstallDirectoryName);
            if (!WhisperOnnxModelLocator.TryResolve(extracted, out _))
            {
                throw new InvalidOperationException("The downloaded archive did not contain a complete Whisper model (encoder, decoder and tokens).");
            }

            DeleteQuietly(destination);
            Directory.Move(extracted, destination);
            progress?.Report(new ModelDownloadProgress("ready", downloaded, downloaded));
            return WhisperOnnxModelLocator.TryResolve(destination, out var installed)
                ? installed
                : throw new InvalidOperationException("The model was unpacked but could not be read back.");
        }
        finally
        {
            DeleteQuietly(archive);
            DeleteQuietly(scratch);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>bsdtar ships with Windows 10+ and macOS, and GNU tar is on Linux. Arguments are passed as a list, never through a shell.</summary>
    private static async Task ExtractWithTarAsync(string archivePath, string targetDirectory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("tar") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-xjf");
        start.ArgumentList.Add(archivePath);
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(targetDirectory);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start tar to unpack the model. Install tar, or unpack the archive by hand into the models folder.");
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"tar could not unpack the model (exit {process.ExitCode}): {(await error.ConfigureAwait(false)).Trim()}");
        }
    }
}
