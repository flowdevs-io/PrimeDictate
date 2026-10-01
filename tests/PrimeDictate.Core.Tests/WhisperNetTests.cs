using System.Net;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Storage;
using PrimeDictate.Platforms;
using PrimeDictate.Platforms.Speech;
using Whisper.net.LibraryLoader;

namespace PrimeDictate.Core.Tests;

public sealed class WhisperNetTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-wn-" + Guid.NewGuid().ToString("N"));

    public WhisperNetTests() => Directory.CreateDirectory(this.root);

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private static ModelDownloadOption Option(string id) => SpeechModelCatalog.Options.Single(o => o.Backend == LegacyBackend.WhisperNet && o.Id == id);

    private string Install(string id, long? size = null)
    {
        var option = Option(id);
        var path = SpeechModelLocator.InstallPath(this.root, option);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        file.SetLength(size ?? option.ApproximateBytes);
        return path;
    }

    // --- catalog and locator ---

    [Fact]
    public void Catalog_matches_the_wpf_ids_files_folder_and_urls()
    {
        var wn = SpeechModelCatalog.Options.Where(o => o.Backend == LegacyBackend.WhisperNet).ToList();
        Assert.Equal(["large-v3-turbo", "large-v3", "base.en", "tiny.en"], wn.Select(o => o.Id));
        Assert.All(wn, o =>
        {
            Assert.Equal($"ggml-{o.Id}.bin", o.FileName);
            Assert.True(o.IsSingleFile);
            Assert.Equal("whisper.net", o.SubFolder);
            Assert.Equal(new Uri($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{o.Id}.bin"), o.DownloadUri);
            Assert.Equal($"whisper-net:{o.Id}", o.ModelId);
        });
        Assert.Equal(1_618_426_976, Option("large-v3-turbo").ApproximateBytes);
        Assert.Equal(4_277_163_902, Option("large-v3").ApproximateBytes);
        Assert.Equal(147_964_352, Option("base.en").ApproximateBytes);
        Assert.Equal(77_720_256, Option("tiny.en").ApproximateBytes);
    }

    [Fact]
    public void Archive_models_are_not_single_files()
    {
        Assert.All(SpeechModelCatalog.Options.Where(o => o.Backend != LegacyBackend.WhisperNet), o => Assert.False(o.IsSingleFile));
    }

    [Fact]
    public void The_locator_finds_an_installed_model_at_the_shared_wpf_path()
    {
        var path = this.Install("large-v3-turbo", size: 1_624_555_275);
        Assert.Equal(Path.Combine(this.root, "whisper.net", "ggml-large-v3-turbo.bin"), path);

        var found = Assert.Single(SpeechModelLocator.Discover(this.root), m => m.Backend == LegacyBackend.WhisperNet);
        Assert.Equal("whisper-net:large-v3-turbo", found.ModelId);
        Assert.Equal(Path.GetFullPath(path), found.Directory);
        Assert.False(found.IsEnglishOnly);
    }

    [Fact]
    public void English_only_ggml_models_are_marked()
    {
        this.Install("base.en");
        Assert.True(Assert.Single(SpeechModelLocator.Discover(this.root)).IsEnglishOnly);
    }

    [Fact]
    public void An_empty_or_cut_off_file_is_not_an_installed_model()
    {
        this.Install("large-v3-turbo", size: 0);
        this.Install("base.en", size: 1024);
        Assert.Empty(SpeechModelLocator.Discover(this.root));
    }

    [Fact]
    public void FindWhisperNet_is_case_insensitive_and_null_for_unknown_ids()
    {
        Assert.Equal("large-v3-turbo", SpeechModelLocator.FindWhisperNet("Large-V3-Turbo")?.Id);
        Assert.Null(SpeechModelLocator.FindWhisperNet("medium"));
        Assert.Null(SpeechModelLocator.FindWhisperNet(null));
    }

    [Fact]
    public void OpenVino_encoder_needs_both_sidecar_files()
    {
        var model = this.Install("large-v3", size: 10);
        Assert.Null(SpeechModelLocator.WhisperNetOpenVinoEncoder(model));
        var xml = Path.Combine(this.root, "whisper.net", "ggml-large-v3-encoder-openvino.xml");
        File.WriteAllText(xml, "x");
        Assert.Null(SpeechModelLocator.WhisperNetOpenVinoEncoder(model));
        File.WriteAllText(Path.Combine(this.root, "whisper.net", "ggml-large-v3-encoder-openvino.bin"), "x");
        Assert.Equal(xml, SpeechModelLocator.WhisperNetOpenVinoEncoder(model));
    }

    // --- download ---

    private sealed class Handler(byte[] body, HttpStatusCode status = HttpStatusCode.OK, long? declaredLength = null) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(request.RequestUri!);
            var content = new ByteArrayContent(body);
            if (declaredLength is { } length)
            {
                content.Headers.ContentLength = length;
            }

            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    // A tiny catalog entry so the test does not need a real 77 MB body: same rules, small size.
    private static readonly ModelDownloadOption Small = Option("tiny.en") with { ApproximateBytes = 1000 };

    [Fact]
    public async Task A_model_file_is_written_to_a_temp_file_then_moved_into_place()
    {
        var handler = new Handler(new byte[1500]);
        var stages = new List<string>();
        var path = await new ModelDownloader(new HttpClient(handler)).DownloadAsync(Small, this.root, new Progress<ModelDownloadProgress>(p => stages.Add(p.Stage)));

        Assert.Equal(Path.Combine(this.root, "whisper.net", "ggml-tiny.en.bin"), path);
        Assert.Equal(1500, new FileInfo(path).Length);
        Assert.Equal(Small.DownloadUri, Assert.Single(handler.Requests));
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(this.root, "whisper.net")), f => f.EndsWith(".download"));

        // Already installed: nothing is fetched again.
        var again = new Handler([]);
        await new ModelDownloader(new HttpClient(again)).DownloadAsync(Small, this.root);
        Assert.Empty(again.Requests);
    }

    [Fact]
    public async Task A_download_that_is_too_small_installs_nothing()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ModelDownloader(new HttpClient(new Handler(new byte[10]))).DownloadAsync(Small, this.root));
        Assert.Empty(Directory.GetFiles(Path.Combine(this.root, "whisper.net")));
    }

    [Fact]
    public async Task A_cut_connection_installs_nothing()
    {
        await Assert.ThrowsAsync<IOException>(() =>
            new ModelDownloader(new HttpClient(new Handler(new byte[1500], declaredLength: 4000))).DownloadAsync(Small, this.root));
        Assert.Empty(Directory.GetFiles(Path.Combine(this.root, "whisper.net")));
    }

    [Fact]
    public async Task An_http_error_installs_nothing()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new ModelDownloader(new HttpClient(new Handler([], HttpStatusCode.NotFound))).DownloadAsync(Small, this.root));
        Assert.Empty(Directory.GetFiles(Path.Combine(this.root, "whisper.net")));
    }

    // --- id mapping and settings migration ---

    [Fact]
    public void A_whisper_net_selection_maps_to_the_whisper_net_model_id()
    {
        var settings = new DictationSettings { TranscriptionBackend = LegacyBackend.WhisperNet, SelectedModelId = "large-v3-turbo" };
        Assert.Equal("whisper-net:large-v3-turbo", settings.ResolveModelId());
        Assert.Equal("whisper-onnx:base.en", new DictationSettings { SelectedModelId = "base.en" }.ResolveModelId());
    }

    private const string WpfWhisperNetJson = """
        {
          "FirstRunCompleted": true,
          "TranscriptionBackend": "WhisperNet",
          "SelectedModelId": "large-v3-turbo",
          "TranscriptionComputeInterface": "Gpu"
        }
        """;

    [Fact]
    public void The_wpf_whisper_net_setup_is_imported_without_touching_the_wpf_file()
    {
        var store = new DictationSettingsStore(new AppDataPaths(this.root));
        File.WriteAllText(store.WpfSettingsPath, WpfWhisperNetJson);

        var settings = store.Load().Settings;
        Assert.Equal("whisper-net:large-v3-turbo", settings.ResolveModelId());
        Assert.Equal(LegacyComputeInterface.Gpu, settings.TranscriptionComputeInterface);
        Assert.Equal(WhisperNetDevicePreference.Gpu, settings.ResolveWhisperNetDevice());

        store.Save(settings);
        Assert.Equal(WpfWhisperNetJson, File.ReadAllText(store.WpfSettingsPath));
        Assert.Equal(WhisperNetDevicePreference.Gpu, store.Load().Settings.ResolveWhisperNetDevice());
    }

    [Theory]
    [InlineData("\"Cpu\"", WhisperNetDevicePreference.Cpu)]
    [InlineData("\"Gpu\"", WhisperNetDevicePreference.Gpu)]
    [InlineData("\"Npu\"", WhisperNetDevicePreference.Npu)]
    [InlineData("1", WhisperNetDevicePreference.Gpu)]
    public void The_wpf_compute_interface_maps_to_a_device(string json, WhisperNetDevicePreference expected)
    {
        var store = new DictationSettingsStore(new AppDataPaths(this.root));
        File.WriteAllText(store.WpfSettingsPath, $$"""{ "TranscriptionComputeInterface": {{json}} }""");
        Assert.Equal(expected, store.Load().Settings.ResolveWhisperNetDevice());
    }

    [Fact]
    public void An_explicit_choice_in_this_app_beats_the_wpf_value_and_no_value_means_auto()
    {
        Assert.Equal(WhisperNetDevicePreference.Auto, new DictationSettings().ResolveWhisperNetDevice());
        var settings = new DictationSettings { TranscriptionComputeInterface = LegacyComputeInterface.Gpu, WhisperNetDevice = "cpu" };
        Assert.Equal(WhisperNetDevicePreference.Cpu, settings.ResolveWhisperNetDevice());
        Assert.Equal(WhisperNetDevicePreference.Gpu, WhisperNetDevicePreferences.Parse("CUDA"));
        Assert.Equal(WhisperNetDevicePreference.Auto, WhisperNetDevicePreferences.Parse("nonsense"));
    }

    // --- runtime order and language ---

    [Fact]
    public void Runtime_library_order_matches_the_wpf_app()
    {
        Assert.Equal([RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx], WhisperNetRuntime.LibraryOrder(WhisperNetDevicePreference.Cpu, false));
        Assert.Equal([RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx], WhisperNetRuntime.LibraryOrder(WhisperNetDevicePreference.Cpu, true));
        Assert.Equal(
            [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            WhisperNetRuntime.LibraryOrder(WhisperNetDevicePreference.Gpu, false));
        // OpenVINO runs only for the NPU choice, as in the WPF app (it never ran OpenVINO for any other choice).
        Assert.Equal([RuntimeLibrary.OpenVino, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx], WhisperNetRuntime.LibraryOrder(WhisperNetDevicePreference.Npu, true));
        Assert.Equal([RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx], WhisperNetRuntime.LibraryOrder(WhisperNetDevicePreference.Npu, false));
        Assert.Equal([RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx], WhisperNetRuntime.LibraryOrder(WhisperNetDevicePreference.Auto, true));
        Assert.Equal([RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx], WhisperNetRuntime.LibraryOrder(WhisperNetDevicePreference.Auto, false));
    }

    [Fact]
    public void A_gpu_request_that_loaded_the_cpu_is_reported()
    {
        Assert.True(WhisperNetRuntime.GpuWantedButNotLoaded(WhisperNetDevicePreference.Gpu, RuntimeLibrary.Cpu));
        Assert.True(WhisperNetRuntime.GpuWantedButNotLoaded(WhisperNetDevicePreference.Gpu, null));
        Assert.False(WhisperNetRuntime.GpuWantedButNotLoaded(WhisperNetDevicePreference.Gpu, RuntimeLibrary.Cuda));
        Assert.False(WhisperNetRuntime.GpuWantedButNotLoaded(WhisperNetDevicePreference.Gpu, RuntimeLibrary.Vulkan));
        Assert.False(WhisperNetRuntime.GpuWantedButNotLoaded(WhisperNetDevicePreference.Cpu, RuntimeLibrary.Cpu));
    }

    [Theory]
    [InlineData(null, false, "en")]
    [InlineData("en-US", false, "en")]
    [InlineData("de-DE", false, "de")]
    [InlineData("auto", false, null)]
    [InlineData("de", true, "en")]
    [InlineData("auto", true, "en")]
    public void Language_is_resolved_like_the_wpf_app_with_auto_detection_on_request(string? input, bool englishOnly, string? expected) =>
        Assert.Equal(expected, WhisperNetProvider.ResolveLanguage(input, englishOnly));

    // --- workspace picker default ---

    [Fact]
    public void The_workspace_picker_starts_on_the_dictation_model_then_turbo_then_the_first()
    {
        SpeechModelChoice Choice(string id) => new(id, id, null, false, null);
        var all = new[] { Choice("whisper-onnx:tiny.en"), Choice("whisper-net:base.en"), Choice("whisper-net:large-v3-turbo") };
        Assert.Equal(1, TranscriptionWorkspaceService.DefaultModelIndex(all, "whisper-net:base.en"));
        Assert.Equal(2, TranscriptionWorkspaceService.DefaultModelIndex(all, null));
        Assert.Equal(2, TranscriptionWorkspaceService.DefaultModelIndex(all, "whisper-onnx:missing"));
        Assert.Equal(0, TranscriptionWorkspaceService.DefaultModelIndex(all.Take(2).ToArray(), null));
        Assert.Equal(-1, TranscriptionWorkspaceService.DefaultModelIndex([], null));
    }

    // --- shared factory cache ---

    private sealed class FakeModel : IDisposable
    {
        public int Disposed { get; private set; }

        public void Dispose() => this.Disposed++;
    }

    [Fact]
    public void Two_users_of_one_model_share_a_single_load_and_the_last_release_disposes_it()
    {
        var cache = new SharedResourceCache<FakeModel>();
        var created = 0;
        FakeModel Create() { created++; return new FakeModel(); }

        var a = cache.Acquire("m", Create);
        var b = cache.Acquire("M", Create); // paths compare case-insensitively on Windows
        Assert.Same(a.Value, b.Value);
        Assert.Equal(1, created);
        Assert.Equal(2, cache.LeaseCount("m"));

        a.Dispose();
        Assert.Equal(0, a.Value.Disposed);
        Assert.Equal(1, cache.LeaseCount("m"));

        b.Dispose();
        b.Dispose(); // releasing twice must not over-release
        Assert.Equal(1, b.Value.Disposed);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void A_double_dispose_of_one_lease_does_not_unload_a_model_another_user_holds()
    {
        var cache = new SharedResourceCache<FakeModel>();
        var a = cache.Acquire("m", () => new FakeModel());
        var b = cache.Acquire("m", () => new FakeModel());
        a.Dispose();
        a.Dispose();
        Assert.Equal(0, b.Value.Disposed);
        Assert.Equal(1, cache.LeaseCount("m"));
    }

    [Fact]
    public void Different_models_load_separately_and_a_released_model_loads_again_on_next_use()
    {
        var cache = new SharedResourceCache<FakeModel>();
        var turbo = cache.Acquire("turbo", () => new FakeModel());
        var tiny = cache.Acquire("tiny", () => new FakeModel());
        Assert.NotSame(turbo.Value, tiny.Value);
        Assert.Equal(2, cache.Count);

        var first = turbo.Value;
        turbo.Dispose();
        Assert.Equal(1, first.Disposed);
        using var again = cache.Acquire("turbo", () => new FakeModel());
        Assert.NotSame(first, again.Value);
        tiny.Dispose();
    }

    [Fact]
    public void A_failed_load_leaves_nothing_behind_and_can_be_retried()
    {
        var cache = new SharedResourceCache<FakeModel>();
        Assert.Throws<InvalidOperationException>(() => cache.Acquire("m", () => throw new InvalidOperationException("no gpu")));
        Assert.Equal(0, cache.Count);
        using var lease = cache.Acquire("m", () => new FakeModel());
        Assert.Equal(1, cache.LeaseCount("m"));
    }

    [Fact]
    public async Task Concurrent_users_load_the_model_once()
    {
        var cache = new SharedResourceCache<FakeModel>();
        var created = 0;
        var leases = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => cache.Acquire("m", () =>
        {
            Interlocked.Increment(ref created);
            Thread.Sleep(20);
            return new FakeModel();
        }))));
        Assert.Equal(1, created);
        Assert.Equal(16, cache.LeaseCount("m"));
        var model = leases[0].Value;
        foreach (var lease in leases)
        {
            lease.Dispose();
        }

        Assert.Equal(1, model.Disposed);
    }
}
