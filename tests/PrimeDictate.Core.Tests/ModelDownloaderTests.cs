using System.Diagnostics;
using System.Net;
using PrimeDictate.Platforms.Speech;

namespace PrimeDictate.Core.Tests;

public sealed class ModelDownloaderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-dl-" + Guid.NewGuid().ToString("N"));

    public ModelDownloaderTests() => Directory.CreateDirectory(this.root);

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private sealed class Handler(byte[] body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
    }

    private byte[]? BuildArchive(string folder, params string[] files)
    {
        var src = Path.Combine(this.root, "src");
        Directory.CreateDirectory(Path.Combine(src, folder));
        foreach (var f in files)
        {
            File.WriteAllText(Path.Combine(src, folder, f), "x");
        }

        var archive = Path.Combine(this.root, "a.tar.bz2");
        try
        {
            var p = Process.Start(new ProcessStartInfo("tar") { ArgumentList = { "-cjf", archive, "-C", src, folder }, RedirectStandardError = true });
            p!.WaitForExit();
            return p.ExitCode == 0 ? File.ReadAllBytes(archive) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static readonly WhisperModelOption Tiny = WhisperModelCatalog.Options.First(o => o.Id == "tiny.en");

    [Fact]
    public async Task A_valid_archive_is_installed_and_found_by_the_locator()
    {
        var archive = this.BuildArchive(Tiny.InstallDirectoryName, "tiny.en-encoder.int8.onnx", "tiny.en-decoder.int8.onnx", "tiny.en-tokens.txt");
        if (archive is null)
        {
            return;
        }

        var models = Path.Combine(this.root, "models");
        var reports = new List<string>();
        var installed = await new WhisperModelDownloader(new HttpClient(new Handler(archive))).DownloadAsync(
            Tiny, models, new Progress<ModelDownloadProgress>(p => reports.Add(p.Stage)));
        Assert.Equal("whisper-onnx:tiny.en", installed.ModelId);
        Assert.Contains(WhisperOnnxModelLocator.Discover(models), m => m.Id == "tiny.en");
        Assert.DoesNotContain(Directory.GetFileSystemEntries(Path.Combine(models, "whisper")), p => p.EndsWith(".download") || p.EndsWith(".extract"));

        // Already installed: no second download.
        var again = await new WhisperModelDownloader(new HttpClient(new Handler([]))).DownloadAsync(Tiny, models);
        Assert.Equal(installed.Directory, again.Directory);
    }

    [Fact]
    public async Task An_incomplete_archive_installs_nothing()
    {
        var archive = this.BuildArchive(Tiny.InstallDirectoryName, "tiny.en-tokens.txt");
        if (archive is null)
        {
            return;
        }

        var models = Path.Combine(this.root, "models");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WhisperModelDownloader(new HttpClient(new Handler(archive))).DownloadAsync(Tiny, models));
        Assert.Empty(WhisperOnnxModelLocator.Discover(models));
        Assert.False(Directory.Exists(Path.Combine(models, "whisper", Tiny.InstallDirectoryName)));
    }

    [Fact]
    public async Task Http_errors_and_broken_archives_leave_no_files_behind()
    {
        var models = Path.Combine(this.root, "models");
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new WhisperModelDownloader(new HttpClient(new Handler([], HttpStatusCode.NotFound))).DownloadAsync(Tiny, models));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            new WhisperModelDownloader(new HttpClient(new Handler([1, 2, 3]))).DownloadAsync(Tiny, models));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(models, "whisper")));
    }

    [Fact]
    public void Catalog_ids_match_the_wpf_folders_so_installs_are_shared()
    {
        Assert.Equal("sherpa-onnx-whisper-base.en", WhisperModelCatalog.Options.First(o => o.Id == "base.en").InstallDirectoryName);
        Assert.EndsWith("/asr-models/sherpa-onnx-whisper-base.en.tar.bz2", Tiny.DownloadUri.AbsoluteUri.Replace("tiny.en", "base.en"));
        Assert.Equal(7, WhisperModelCatalog.Options.Count);
    }
}

public sealed class RealArchiveLayoutTests
{
    [Fact]
    public void An_archive_holding_both_full_and_int8_files_resolves()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pd-real-" + Guid.NewGuid().ToString("N"), "sherpa-onnx-whisper-tiny.en");
        Directory.CreateDirectory(dir);
        foreach (var f in new[] { "tiny.en-decoder.onnx", "tiny.en-decoder.int8.onnx", "tiny.en-encoder.onnx", "tiny.en-encoder.int8.onnx", "tiny.en-tokens.txt" })
        {
            File.WriteAllText(Path.Combine(dir, f), "x");
        }

        Assert.True(PrimeDictate.Platforms.Speech.WhisperOnnxModelLocator.TryResolve(dir, out var model));
        Assert.Contains("int8", model.Encoder);
        Directory.Delete(Path.GetDirectoryName(dir)!, true);
    }
}
