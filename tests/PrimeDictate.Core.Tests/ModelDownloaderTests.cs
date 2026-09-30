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

    private static readonly ModelDownloadOption Tiny = SpeechModelCatalog.Options.First(o => o.Id == "tiny.en");

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
        var installed = await new ModelDownloader(new HttpClient(new Handler(archive))).DownloadAsync(
            Tiny, models, new Progress<ModelDownloadProgress>(p => reports.Add(p.Stage)));
        Assert.EndsWith("sherpa-onnx-whisper-tiny.en", installed);
        Assert.Contains(SpeechModelLocator.Discover(models), m => m.ModelId == "whisper-onnx:tiny.en");
        Assert.DoesNotContain(Directory.GetFileSystemEntries(Path.Combine(models, "whisper")), p => p.EndsWith(".download") || p.EndsWith(".extract"));

        // Already installed: no second download.
        var again = await new ModelDownloader(new HttpClient(new Handler([]))).DownloadAsync(Tiny, models);
        Assert.Equal(installed, again);
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
            new ModelDownloader(new HttpClient(new Handler(archive))).DownloadAsync(Tiny, models));
        Assert.Empty(SpeechModelLocator.Discover(models));
        Assert.False(Directory.Exists(Path.Combine(models, "whisper", Tiny.InstallDirectoryName)));
    }

    [Fact]
    public async Task Http_errors_and_broken_archives_leave_no_files_behind()
    {
        var models = Path.Combine(this.root, "models");
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new ModelDownloader(new HttpClient(new Handler([], HttpStatusCode.NotFound))).DownloadAsync(Tiny, models));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            new ModelDownloader(new HttpClient(new Handler([1, 2, 3]))).DownloadAsync(Tiny, models));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(models, "whisper")));
    }

    [Fact]
    public void Catalog_ids_match_the_wpf_folders_so_installs_are_shared()
    {
        Assert.Equal("sherpa-onnx-whisper-base.en", SpeechModelCatalog.Options.First(o => o.Id == "base.en").InstallDirectoryName);
        Assert.EndsWith("/asr-models/sherpa-onnx-whisper-base.en.tar.bz2", Tiny.DownloadUri.AbsoluteUri.Replace("tiny.en", "base.en"));
        Assert.Equal(12, SpeechModelCatalog.Options.Count);
        Assert.Equal("parakeet-onnx:parakeet-tdt-0.6b-v3", SpeechModelCatalog.Options.First(o => o.Backend == PrimeDictate.Core.Dictation.LegacyBackend.Parakeet).ModelId);
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

public sealed class OtherFamilyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-fam-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, true);
        }
    }

    private void Touch(string sub, string folder, params string[] files)
    {
        var dir = Path.Combine(this.root, sub, folder);
        Directory.CreateDirectory(dir);
        foreach (var f in files)
        {
            File.WriteAllText(Path.Combine(dir, f), "x");
        }
    }

    [Fact]
    public void Parakeet_and_both_moonshine_layouts_are_discovered()
    {
        this.Touch("parakeet", "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt");
        this.Touch("moonshine", "sherpa-onnx-moonshine-tiny-en-quantized-2026-02-27", "encoder_model.ort", "decoder_model_merged.ort", "tokens.txt");
        this.Touch("moonshine", "sherpa-onnx-moonshine-base-en-int8", "preprocess.onnx", "encode.int8.onnx", "uncached_decode.int8.onnx", "cached_decode.int8.onnx", "tokens.txt");
        var ids = SpeechModelLocator.Discover(this.root).Select(m => m.ModelId).Order().ToArray();
        Assert.Equal(["moonshine-onnx:moonshine-base-en", "moonshine-onnx:moonshine-tiny-v2-en", "parakeet-onnx:parakeet-tdt-0.6b-v3"], ids);
        var v2 = SpeechModelLocator.ResolveMoonshine(Path.Combine(this.root, "moonshine", "sherpa-onnx-moonshine-tiny-en-quantized-2026-02-27"))!;
        Assert.NotNull(v2.MergedDecoder);
        Assert.Null(SpeechModelLocator.ResolveMoonshine(Path.Combine(this.root, "moonshine", "sherpa-onnx-moonshine-base-en-int8"))!.MergedDecoder);
    }

    [Fact]
    public void Incomplete_folders_are_not_listed()
    {
        this.Touch("parakeet", "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", "encoder.int8.onnx", "tokens.txt");
        this.Touch("moonshine", "sherpa-onnx-moonshine-base-en-int8", "tokens.txt");
        Assert.Empty(SpeechModelLocator.Discover(this.root));
    }

    [Fact]
    public void Setting_resolves_the_model_id_for_each_backend()
    {
        var s = new PrimeDictate.Core.Dictation.DictationSettings { SelectedModelId = "x", TranscriptionBackend = PrimeDictate.Core.Dictation.LegacyBackend.Moonshine };
        Assert.Equal("moonshine-onnx:x", s.ResolveModelId());
        s.TranscriptionBackend = PrimeDictate.Core.Dictation.LegacyBackend.Parakeet;
        Assert.Equal("parakeet-onnx:x", s.ResolveModelId());
        s.SelectedModelId = null;
        Assert.Null(s.ResolveModelId());
    }
}
