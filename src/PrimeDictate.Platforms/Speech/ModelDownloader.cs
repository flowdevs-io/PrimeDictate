using System.Diagnostics;

namespace PrimeDictate.Platforms.Speech;

public sealed record ModelDownloadProgress(string Stage, long BytesDownloaded, long? TotalBytes)
{
    public double? Fraction => this.Stage == "download" && this.TotalBytes is > 0 ? Math.Min(1.0, (double)this.BytesDownloaded / this.TotalBytes.Value) : null;
}

/// <summary>
/// Downloads a model archive over HTTPS and unpacks it into <c>{models}/{whisper|parakeet|moonshine}/{folder}</c>. The archive is unpacked in a
/// scratch folder first and only a folder that passes the same validation the recognizer uses is moved into place, so a cut
/// connection or a wrong archive never leaves a half-installed model that looks installed.
/// </summary>
public sealed class ModelDownloader(HttpClient? http = null, Func<string, string, CancellationToken, Task>? extract = null)
{
    private static readonly HttpClient Shared = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient client = http ?? Shared;
    private readonly Func<string, string, CancellationToken, Task> extractArchive = extract ?? ExtractWithTarAsync;

    /// <summary>Overrides where archives come from (tests, mirrors). Null uses the sherpa-onnx release.</summary>
    public Func<ModelDownloadOption, Uri>? UriFor { get; init; }

    public async Task<string> DownloadAsync(
        ModelDownloadOption option,
        string modelsRoot,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var whisperRoot = Path.Combine(modelsRoot, option.SubFolder);
        Directory.CreateDirectory(whisperRoot);
        var destination = Path.Combine(whisperRoot, option.InstallDirectoryName);
        if (SpeechModelLocator.IsValid(option, destination))
        {
            progress?.Report(new ModelDownloadProgress("ready", 1, 1));
            return destination;
        }

        if (option.IsSingleFile)
        {
            return await this.DownloadFileAsync(option, destination, progress, cancellationToken).ConfigureAwait(false);
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
            if (!Directory.Exists(extracted) && Directory.GetDirectories(scratch) is [var only])
            {
                extracted = only;
            }

            if (!SpeechModelLocator.IsValid(option, extracted))
            {
                throw new InvalidOperationException($"The downloaded archive did not contain a complete {option.Backend} model.");
            }

            DeleteQuietly(destination);
            Directory.Move(extracted, destination);
            progress?.Report(new ModelDownloadProgress("ready", downloaded, downloaded));
            return SpeechModelLocator.IsValid(option, destination)
                ? destination
                : throw new InvalidOperationException("The model was unpacked but could not be read back.");
        }
        finally
        {
            DeleteQuietly(archive);
            DeleteQuietly(scratch);
        }
    }

    /// <summary>A single-file model (Whisper.net ggml): written to <c>{file}.download</c>, checked, then moved into place.</summary>
    private async Task<string> DownloadFileAsync(ModelDownloadOption option, string destination, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var temp = destination + ".download";
        try
        {
            DeleteQuietly(temp);
            long downloaded = 0;
            long? total = null;
            using (var response = await this.client.GetAsync(this.UriFor?.Invoke(option) ?? option.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
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

            if (!SpeechModelLocator.IsValidGgml(option, temp))
            {
                throw new InvalidOperationException($"The downloaded file is too small to be the {option.DisplayName} model ({downloaded:N0} bytes).");
            }

            File.Move(temp, destination, overwrite: true);
            progress?.Report(new ModelDownloadProgress("ready", downloaded, downloaded));
            return destination;
        }
        finally
        {
            DeleteQuietly(temp);
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
