using System.Diagnostics;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Platforms.Speech.Qualcomm;

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

    /// <summary>Overrides where the Whisper tokenizer for the Qualcomm package comes from (tests). Null uses the Whisper repository.</summary>
    public Uri? QualcommTokenizerUri { get; init; }

    /// <summary>Overrides whether the OpenVINO bundle is used for models that have one. Null asks the machine (a Windows x64 process with the OpenVINO runtime).</summary>
    public bool? OpenVinoSupported { get; init; }

    /// <summary>Overrides where the Intel OpenVINO bundle comes from (tests). Null uses the option's own address.</summary>
    public Func<ModelDownloadOption, Uri>? OpenVinoBundleUriFor { get; init; }

    public async Task<string> DownloadAsync(
        ModelDownloadOption option,
        string modelsRoot,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var whisperRoot = Path.Combine(modelsRoot, option.SubFolder);
        Directory.CreateDirectory(whisperRoot);
        var destination = Path.Combine(whisperRoot, option.InstallDirectoryName);
        var wantsOpenVino = option.SupportsOpenVinoBundle && (this.OpenVinoSupported ?? MachineSupport.Current.WhisperNetOpenVino);
        if (SpeechModelLocator.IsValid(option, destination) && (!wantsOpenVino || SpeechModelLocator.WhisperNetOpenVinoEncoder(destination) is not null))
        {
            progress?.Report(new ModelDownloadProgress("ready", 1, 1));
            return destination;
        }

        if (option.Backend == LegacyBackend.QualcommQnn)
        {
            return await QualcommAihubWhisperInstaller.InstallAsync(
                this.client,
                QualcommAihubWhisperCatalog.Options.First(o => o.Id == option.Id),
                modelsRoot,
                this.UriFor?.Invoke(option) ?? option.DownloadUri,
                this.QualcommTokenizerUri ?? new Uri(QualcommAihubWhisperCatalog.TokenizerUri),
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        if (wantsOpenVino)
        {
            return await this.DownloadOpenVinoBundleAsync(option, destination, progress, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// A Whisper.net model that has an Intel OpenVINO bundle (the ggml model plus its encoder files for the NPU), on a machine that can use
    /// them: one zip is downloaded, the three files are taken out next to each other as <c>.download</c> files, checked, then moved into place
    /// together, so the model never ends up installed without the files the NPU needs. Same flow and file names as the WPF app.
    /// </summary>
    private async Task<string> DownloadOpenVinoBundleAsync(ModelDownloadOption option, string destination, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var stem = Path.GetFileNameWithoutExtension(option.FileName)!;
        var directory = Path.GetDirectoryName(destination)!;
        var xml = Path.Combine(directory, $"{stem}-encoder-openvino.xml");
        var bin = Path.Combine(directory, $"{stem}-encoder-openvino.bin");
        var bundle = destination + ".openvino.zip.download";
        var temps = new[] { destination + ".download", xml + ".download", bin + ".download", bundle };
        try
        {
            foreach (var temp in temps)
            {
                DeleteQuietly(temp);
            }

            var uri = this.OpenVinoBundleUriFor?.Invoke(option) ?? option.OpenVinoBundleUri!;
            var (downloaded, total) = await this.FetchAsync(uri, bundle, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report(new ModelDownloadProgress("extract", downloaded, total));
            using (var archive = System.IO.Compression.ZipFile.OpenRead(bundle))
            {
                await ExtractEntryAsync(archive, option.FileName!, temps[0], cancellationToken).ConfigureAwait(false);
                await ExtractEntryAsync(archive, Path.GetFileName(xml), temps[1], cancellationToken).ConfigureAwait(false);
                await ExtractEntryAsync(archive, Path.GetFileName(bin), temps[2], cancellationToken).ConfigureAwait(false);
            }

            if (!SpeechModelLocator.IsValidGgml(option, temps[0]))
            {
                throw new InvalidOperationException($"The OpenVINO bundle holds a model file that is too small to be {option.DisplayName}.");
            }

            File.Move(temps[0], destination, overwrite: true);
            File.Move(temps[1], xml, overwrite: true);
            File.Move(temps[2], bin, overwrite: true);
            progress?.Report(new ModelDownloadProgress("ready", downloaded, downloaded));
            return destination;
        }
        finally
        {
            foreach (var temp in temps)
            {
                DeleteQuietly(temp);
            }
        }
    }

    private async Task<(long Downloaded, long? Total)> FetchAsync(Uri uri, string path, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        long downloaded = 0;
        long? total;
        using (var response = await this.client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
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

        return (downloaded, total);
    }

    private static async Task ExtractEntryAsync(System.IO.Compression.ZipArchive archive, string fileName, string destinationPath, CancellationToken cancellationToken)
    {
        var entry = archive.Entries.FirstOrDefault(candidate => string.Equals(Path.GetFileName(candidate.FullName), fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"The OpenVINO bundle is missing the expected file '{fileName}'.");
        await using var source = entry.Open();
        await using var file = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        await source.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
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
