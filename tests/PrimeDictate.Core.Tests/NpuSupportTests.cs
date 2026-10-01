using System.Net;
using System.Runtime.InteropServices;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Platforms.Speech;
using PrimeDictate.Platforms.Speech.Qualcomm;
using Whisper.net.LibraryLoader;

namespace PrimeDictate.Core.Tests;

/// <summary>Machine gating for the Whisper.net GPU and OpenVINO NPU choices, the device list, and the OpenVINO bundle download.</summary>
public sealed class NpuSupportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-npu-" + Guid.NewGuid().ToString("N"));

    public NpuSupportTests() => Directory.CreateDirectory(this.root);

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private static readonly QnnAvailability NoQnn = MachineSupport.None.Qnn;

    private static MachineSupport Pc(bool openVino = false, bool cuda = false, bool vulkan = false) => new(openVino, cuda, vulkan, NoQnn);

    // --- machine support ---

    [Fact]
    public void Whisper_net_accelerators_need_windows_x64_the_driver_library_and_the_runtime_build()
    {
        var system = @"C:\Windows\System32";
        bool Exists(string path) => path.EndsWith("nvcuda.dll") || path.EndsWith("vulkan-1.dll");

        var full = MachineSupport.Evaluate(true, Architecture.X64, Exists, system, _ => true, NoQnn);
        Assert.True(full.WhisperNetOpenVino);
        Assert.True(full.WhisperNetCuda);
        Assert.True(full.WhisperNetVulkan);
        Assert.True(full.WhisperNetGpu);

        var arm = MachineSupport.Evaluate(true, Architecture.Arm64, Exists, system, _ => true, NoQnn);
        Assert.False(arm.WhisperNetOpenVino || arm.WhisperNetGpu);
        var linux = MachineSupport.Evaluate(false, Architecture.X64, Exists, system, _ => true, NoQnn);
        Assert.False(linux.WhisperNetOpenVino || linux.WhisperNetGpu);

        // No NVIDIA driver and no OpenVINO runtime build.
        var partial = MachineSupport.Evaluate(true, Architecture.X64, path => path.EndsWith("vulkan-1.dll"), system, name => name != "openvino", NoQnn);
        Assert.False(partial.WhisperNetOpenVino);
        Assert.False(partial.WhisperNetCuda);
        Assert.True(partial.WhisperNetVulkan);
        Assert.Equal("Vulkan", partial.WhisperNetGpuRuntimeLabel);
        Assert.Equal("Vulkan GPU", partial.WhisperNetGpuLabel);
        Assert.Equal("CUDA/Vulkan", full.WhisperNetGpuRuntimeLabel);
        Assert.Equal("GPU", MachineSupport.None.WhisperNetGpuRuntimeLabel);
    }

    [Fact]
    public void The_openvino_runtime_is_looked_for_on_the_search_path_and_never_loaded()
    {
        var dirs = new[] { @"C:\app", @"C:\Windows\System32", @"D:\intel\openvino\bin", string.Empty };
        Assert.Equal(@"D:\intel\openvino\bin", MachineSupport.FindOnSearchPath("openvino.dll", dirs, path => path == @"D:\intel\openvino\bin\openvino.dll"));
        Assert.Equal(@"C:\app", MachineSupport.FindOnSearchPath("openvino.dll", dirs, path => path.EndsWith("openvino.dll")));
        Assert.Null(MachineSupport.FindOnSearchPath("openvino.dll", dirs, _ => false));
        Assert.Null(MachineSupport.FindOnSearchPath("openvino.dll", [], _ => true));
    }

    [Fact]
    public void Probing_this_machine_loads_no_native_library()
    {
        // The probe must be safe to run in any order, any number of times, before Whisper.net loads its real runtime:
        // loading and freeing two whisper.cpp builds in one process aborts it. Reading Current exercises the real probe here.
        var support = MachineSupport.Current;
        Assert.Same(support, MachineSupport.Current);
        Assert.NotNull(WhisperNetRuntime.AutoLabel(support));
    }

    [Fact]
    public void The_runtime_summary_matches_the_wpf_sentences()
    {
        Assert.Equal("Whisper.net GGML can use CPU, CUDA GPU, and OpenVINO NPU on this machine.", Pc(openVino: true, cuda: true).WhisperNetRuntimeSummary(Architecture.X64, true));
        Assert.Equal("Whisper.net GGML can use CPU and OpenVINO NPU on this machine.", Pc(openVino: true).WhisperNetRuntimeSummary(Architecture.X64, true));
        Assert.Equal("Whisper.net GGML can use CPU and CUDA GPU on this machine.", Pc(cuda: true).WhisperNetRuntimeSummary(Architecture.X64, true));
        Assert.Equal("Whisper.net GGML runs natively on ARM64 with CPU. GPU/NPU acceleration currently requires an x64 build.", Pc().WhisperNetRuntimeSummary(Architecture.Arm64, true));
        Assert.Contains("no supported GPU/NPU runtime was detected for X64", Pc().WhisperNetRuntimeSummary(Architecture.X64, true));
    }

    // --- Auto and the explicit choices ---

    [Fact]
    public void Auto_picks_the_gpu_then_the_npu_when_the_model_has_its_files_then_the_cpu()
    {
        Assert.Equal(WhisperNetDevicePreference.Gpu, WhisperNetRuntime.Resolve(WhisperNetDevicePreference.Auto, Pc(cuda: true, openVino: true), true));
        Assert.Equal(WhisperNetDevicePreference.Gpu, WhisperNetRuntime.Resolve(WhisperNetDevicePreference.Auto, Pc(vulkan: true), false));
        Assert.Equal(WhisperNetDevicePreference.Npu, WhisperNetRuntime.Resolve(WhisperNetDevicePreference.Auto, Pc(openVino: true), true));
        Assert.Equal(WhisperNetDevicePreference.Cpu, WhisperNetRuntime.Resolve(WhisperNetDevicePreference.Auto, Pc(openVino: true), false));
        Assert.Equal(WhisperNetDevicePreference.Cpu, WhisperNetRuntime.Resolve(WhisperNetDevicePreference.Auto, Pc(), true));
    }

    [Theory]
    [InlineData(WhisperNetDevicePreference.Cpu)]
    [InlineData(WhisperNetDevicePreference.Gpu)]
    [InlineData(WhisperNetDevicePreference.Npu)]
    public void An_explicit_choice_is_kept_as_chosen(WhisperNetDevicePreference choice) =>
        Assert.Equal(choice, WhisperNetRuntime.Resolve(choice, Pc(), true));

    [Fact]
    public void Openvino_runs_on_the_npu_only_and_only_with_the_models_files()
    {
        Assert.Equal("NPU", WhisperNetRuntime.OpenVinoDevice);
        Assert.Equal([RuntimeLibrary.OpenVino, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx], WhisperNetRuntime.LibraryOrder(WhisperNetDevicePreference.Npu, hasOpenVinoEncoder: true));
        Assert.Equal([RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx], WhisperNetRuntime.LibraryOrder(WhisperNetDevicePreference.Npu, hasOpenVinoEncoder: false));
        Assert.DoesNotContain(RuntimeLibrary.OpenVino, WhisperNetRuntime.LibraryOrder(WhisperNetDevicePreference.Gpu, hasOpenVinoEncoder: true));
        Assert.DoesNotContain(RuntimeLibrary.OpenVino, WhisperNetRuntime.LibraryOrder(WhisperNetDevicePreference.Cpu, hasOpenVinoEncoder: true));
    }

    [Fact]
    public void The_auto_label_says_what_auto_does_here()
    {
        Assert.Equal("Auto (CUDA GPU, CPU if it fails)", WhisperNetRuntime.AutoLabel(Pc(cuda: true, openVino: true)));
        Assert.StartsWith("Auto (the NPU", WhisperNetRuntime.AutoLabel(Pc(openVino: true)));
        Assert.StartsWith("Auto (the CPU", WhisperNetRuntime.AutoLabel(Pc()));
    }

    // --- device list ---

    [Fact]
    public void Only_devices_this_pc_can_run_are_listed()
    {
        Assert.Equal(
            [WhisperNetDevicePreference.Auto, WhisperNetDevicePreference.Cpu],
            WhisperNetDeviceChoices.For(Pc()).Select(c => c.Device));
        Assert.Equal(
            [WhisperNetDevicePreference.Auto, WhisperNetDevicePreference.Cpu, WhisperNetDevicePreference.Gpu, WhisperNetDevicePreference.Npu],
            WhisperNetDeviceChoices.For(Pc(openVino: true, cuda: true)).Select(c => c.Device));
        Assert.Contains("OpenVINO", WhisperNetDeviceChoices.For(Pc(openVino: true)).Single(c => c.Device == WhisperNetDevicePreference.Npu).Label);
    }

    [Fact]
    public void A_saved_choice_this_pc_does_not_offer_shows_as_auto_and_is_not_overwritten_by_an_untouched_save()
    {
        var withoutNpu = WhisperNetDeviceChoices.For(Pc(cuda: true));
        Assert.Equal(0, WhisperNetDeviceChoices.IndexOf(withoutNpu, WhisperNetDevicePreference.Npu));
        Assert.Equal(1, WhisperNetDeviceChoices.IndexOf(withoutNpu, WhisperNetDevicePreference.Cpu));
        Assert.Equal(2, WhisperNetDeviceChoices.IndexOf(withoutNpu, WhisperNetDevicePreference.Gpu));

        var wpf = new DictationSettings { TranscriptionComputeInterface = LegacyComputeInterface.Npu };
        Assert.Null(WhisperNetDeviceChoices.ValueToSave(withoutNpu, 0, wpf));
        Assert.Equal("cpu", WhisperNetDeviceChoices.ValueToSave(withoutNpu, 1, wpf));
        Assert.Equal("gpu", WhisperNetDeviceChoices.ValueToSave(withoutNpu, 2, wpf));

        // A saved choice that is offered is written back as shown, and an explicit value in this app is always written.
        var withNpu = WhisperNetDeviceChoices.For(Pc(openVino: true));
        Assert.Equal("auto", WhisperNetDeviceChoices.ValueToSave(withNpu, 0, wpf));
        Assert.Equal("auto", WhisperNetDeviceChoices.ValueToSave(withoutNpu, 0, new DictationSettings { WhisperNetDevice = "npu" }));
    }

    // --- OpenVINO bundle download ---

    private static readonly ModelDownloadOption Large = SpeechModelCatalog.Options.Single(o => o.Backend == LegacyBackend.WhisperNet && o.Id == "large-v3") with { ApproximateBytes = 1000 };

    private sealed class Router(Func<Uri, (HttpStatusCode, byte[])> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(request.RequestUri!);
            var (status, body) = respond(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }

    private static byte[] BundleZip(bool withBin = true)
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, int size)
            {
                using var entry = zip.CreateEntry("bundle/" + name).Open();
                entry.Write(new byte[size]);
            }

            Add("ggml-large-v3.bin", 1500);
            Add("ggml-large-v3-encoder-openvino.xml", 10);
            if (withBin)
            {
                Add("ggml-large-v3-encoder-openvino.bin", 20);
            }
        }

        return stream.ToArray();
    }

    private string ModelPath => Path.Combine(this.root, "whisper.net", "ggml-large-v3.bin");

    [Fact]
    public void Only_large_v3_has_an_openvino_bundle_with_the_intel_address()
    {
        var withBundle = SpeechModelCatalog.Options.Where(o => o.SupportsOpenVinoBundle).ToList();
        var large = Assert.Single(withBundle);
        Assert.Equal("large-v3", large.Id);
        Assert.Equal(new Uri("https://huggingface.co/Intel/whisper.cpp-openvino-models/resolve/main/ggml-large-v3-models.zip"), large.OpenVinoBundleUri);
        Assert.Null(SpeechModelCatalog.Options.First(o => o.Id == "tiny.en").OpenVinoBundleUri);
    }

    [Fact]
    public async Task On_an_openvino_pc_the_bundle_installs_the_model_and_its_npu_files_together()
    {
        var zip = BundleZip();
        var router = new Router(_ => (HttpStatusCode.OK, zip));
        var stages = new List<string>();
        var downloader = new ModelDownloader(new HttpClient(router)) { OpenVinoSupported = true };

        var path = await downloader.DownloadAsync(Large, this.root, new Progress<ModelDownloadProgress>(p => stages.Add(p.Stage)));

        Assert.Equal(this.ModelPath, path);
        Assert.Equal(1500, new FileInfo(path).Length);
        Assert.NotNull(SpeechModelLocator.WhisperNetOpenVinoEncoder(path));
        Assert.Equal(Large.OpenVinoBundleUri, Assert.Single(router.Requests));
        Assert.Contains("extract", stages);
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(this.root, "whisper.net")), f => f.EndsWith(".download"));

        // Complete: nothing is fetched again.
        var again = new Router(_ => (HttpStatusCode.OK, []));
        await new ModelDownloader(new HttpClient(again)) { OpenVinoSupported = true }.DownloadAsync(Large, this.root);
        Assert.Empty(again.Requests);
    }

    [Fact]
    public async Task A_model_installed_without_its_npu_files_is_completed_from_the_bundle_on_an_openvino_pc()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(this.ModelPath)!);
        await File.WriteAllBytesAsync(this.ModelPath, new byte[800]);
        var router = new Router(_ => (HttpStatusCode.OK, BundleZip()));

        await new ModelDownloader(new HttpClient(router)) { OpenVinoSupported = true }.DownloadAsync(Large, this.root);

        Assert.Single(router.Requests);
        Assert.Equal(1500, new FileInfo(this.ModelPath).Length);
        Assert.NotNull(SpeechModelLocator.WhisperNetOpenVinoEncoder(this.ModelPath));
    }

    [Fact]
    public async Task Without_openvino_the_plain_model_is_downloaded_and_an_installed_one_is_left_alone()
    {
        var router = new Router(_ => (HttpStatusCode.OK, new byte[1500]));
        var path = await new ModelDownloader(new HttpClient(router)) { OpenVinoSupported = false }.DownloadAsync(Large, this.root);
        Assert.Equal(Large.DownloadUri, Assert.Single(router.Requests));
        Assert.Null(SpeechModelLocator.WhisperNetOpenVinoEncoder(path));

        var again = new Router(_ => (HttpStatusCode.OK, []));
        await new ModelDownloader(new HttpClient(again)) { OpenVinoSupported = false }.DownloadAsync(Large, this.root);
        Assert.Empty(again.Requests);
    }

    [Fact]
    public async Task A_bundle_missing_a_file_installs_nothing()
    {
        var router = new Router(_ => (HttpStatusCode.OK, BundleZip(withBin: false)));
        await Assert.ThrowsAsync<FileNotFoundException>(() => new ModelDownloader(new HttpClient(router)) { OpenVinoSupported = true }.DownloadAsync(Large, this.root));
        Assert.Empty(Directory.GetFiles(Path.Combine(this.root, "whisper.net")));
    }

    [Fact]
    public async Task A_cut_bundle_download_installs_nothing()
    {
        var router = new Router(_ => (HttpStatusCode.NotFound, []));
        await Assert.ThrowsAsync<HttpRequestException>(() => new ModelDownloader(new HttpClient(router)) { OpenVinoSupported = true }.DownloadAsync(Large, this.root));
        Assert.Empty(Directory.GetFiles(Path.Combine(this.root, "whisper.net")));
    }

    [Fact]
    public async Task A_bundle_whose_model_is_too_small_installs_nothing()
    {
        var tooSmall = Large with { ApproximateBytes = 100_000 };
        var router = new Router(_ => (HttpStatusCode.OK, BundleZip()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ModelDownloader(new HttpClient(router)) { OpenVinoSupported = true }.DownloadAsync(tooSmall, this.root));
        Assert.Empty(Directory.GetFiles(Path.Combine(this.root, "whisper.net")));
    }
}
