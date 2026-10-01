using System.IO.Compression;

namespace PrimeDictate.Platforms.Speech.Qualcomm;

/// <summary>
/// Installs the AI Hub Whisper package: downloads the runnable <c>precompiled_qnn_onnx</c> zip, unpacks it in a scratch folder, adds the
/// Whisper tokenizer (a separate file in the Whisper repository) and moves the folder into place only when it holds every required file.
/// A cut connection or a wrong archive never leaves a half-installed model that looks installed.
/// </summary>
public static class QualcommAihubWhisperInstaller
{
    public static async Task<string> InstallAsync(
        HttpClient client,
        QualcommAihubWhisperOption option,
        string modelsRoot,
        Uri archiveUri,
        Uri tokenizerUri,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var folder = Path.Combine(modelsRoot, QualcommAihubWhisperCatalog.SubFolder);
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, option.InstallDirectoryName);
        if (QualcommAihubWhisperCatalog.TryResolveInstalledPath(modelsRoot, option, out var installed))
        {
            progress?.Report(new ModelDownloadProgress("ready", 1, 1));
            return installed;
        }

        var archive = Path.Combine(folder, option.RunnableArchiveFileName + ".download");
        var scratch = Path.Combine(folder, option.InstallDirectoryName + ".extract");
        var completed = false;
        try
        {
            DeleteQuietly(archive);
            DeleteQuietly(scratch);
            DeleteQuietly(destination);

            long downloaded = 0;
            long? total = null;
            using (var response = await client.GetAsync(archiveUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
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
            ZipFile.ExtractToDirectory(archive, scratch, overwriteFiles: true);

            var package = QualcommAihubWhisperCatalog.FindRunnablePackageDirectory(scratch)
                ?? throw new InvalidOperationException(
                    $"The extracted Qualcomm AI Hub Whisper package is incomplete. Expected {string.Join(", ", QualcommAihubWhisperCatalog.RequiredFiles)}.");
            Directory.Move(package, destination);

            progress?.Report(new ModelDownloadProgress("tokenizer", downloaded, total));
            await DownloadTokenizerAsync(client, tokenizerUri, destination, cancellationToken).ConfigureAwait(false);

            if (!QualcommAihubWhisperCatalog.IsValidModelDirectory(destination))
            {
                throw new InvalidOperationException(
                    $"The installed Qualcomm AI Hub Whisper package is incomplete. Expected {string.Join(", ", QualcommAihubWhisperCatalog.RequiredFiles)}.");
            }

            progress?.Report(new ModelDownloadProgress("ready", downloaded, downloaded));
            completed = true;
            return destination;
        }
        finally
        {
            DeleteQuietly(archive);
            DeleteQuietly(scratch);
            if (!completed && !QualcommAihubWhisperCatalog.IsValidModelDirectory(destination))
            {
                DeleteQuietly(destination);
            }
        }
    }

    private static async Task DownloadTokenizerAsync(HttpClient client, Uri tokenizerUri, string destination, CancellationToken cancellationToken)
    {
        var tokenizerPath = Path.Combine(destination, QualcommAihubWhisperCatalog.TokenizerFileName);
        if (File.Exists(tokenizerPath))
        {
            return;
        }

        using var response = await client.GetAsync(tokenizerUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(tokenizerPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
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
}
