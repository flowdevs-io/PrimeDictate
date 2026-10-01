using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Storage;
using PrimeDictate.Platforms.Speech;
using PrimeDictate.Platforms.Speech.Qualcomm;

namespace PrimeDictate.Core.Tests;

/// <summary>
/// The Qualcomm (QNN) model path in the new app: gating, catalog, install layout, download, discovery, settings migration and
/// normalization. None of it needs a Snapdragon PC: the NPU itself is not exercised here (it cannot be on this hardware).
/// </summary>
public sealed class QualcommCatalogTests : IDisposable
{
    private const string AihubId = "qaihub-whisper-small-snapdragon-x-elite";
    private const string AihubFolder = "whisper_small-precompiled_qnn_onnx-float-qualcomm_snapdragon_x_elite";

    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-qnn-" + Guid.NewGuid().ToString("N"));

    public QualcommCatalogTests() => Directory.CreateDirectory(this.root);

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    internal static QnnAvailability Arm64WithNatives { get; } = QnnRuntimeSupport.Evaluate(true, Architecture.Arm64, name => $@"C:\app\{name}");

    internal static MachineSupport Snapdragon { get; } = new(false, false, false, Arm64WithNatives);

    private string AihubPath => Path.Combine(this.root, "qualcomm-aihub-whisper", AihubFolder);

    private void InstallAihub(bool tokenizer = true)
    {
        Directory.CreateDirectory(this.AihubPath);
        foreach (var file in QualcommAihubWhisperCatalog.RequiredFiles.Where(f => tokenizer || f != QualcommAihubWhisperCatalog.TokenizerFileName))
        {
            File.WriteAllText(Path.Combine(this.AihubPath, file), "x");
        }
    }

    // --- gating ---

    [Fact]
    public void Qnn_needs_a_native_windows_arm64_process_with_every_native()
    {
        Assert.True(Arm64WithNatives.SupportsQnnHtp);
        Assert.True(Arm64WithNatives.IsArm64Process);

        var x64 = QnnRuntimeSupport.Evaluate(true, Architecture.X64, name => $@"C:\app\{name}");
        Assert.False(x64.SupportsQnnHtp);
        Assert.Contains("native Windows ARM64 process", x64.Summary);
        Assert.Contains("X64", x64.Summary);

        var linux = QnnRuntimeSupport.Evaluate(false, Architecture.Arm64, name => $"/app/{name}");
        Assert.False(linux.SupportsQnnHtp);
        Assert.Equal("Qualcomm QNN HTP requires Windows.", linux.Summary);

        var missing = QnnRuntimeSupport.Evaluate(true, Architecture.Arm64, name => name == QnnRuntimeSupport.SystemLibrary ? null : "x");
        Assert.False(missing.SupportsQnnHtp);
        Assert.Contains("native assets are not present", missing.Summary);
        Assert.False(QnnRuntimeSupport.Evaluate(true, Architecture.Arm64, _ => null).SupportsQnnHtp);
    }

    [Fact]
    public void The_machine_here_does_not_offer_qualcomm_models()
    {
        // This test machine is not a Snapdragon PC; the gate must say so rather than fail later inside native code.
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            Assert.False(MachineSupport.Current.QualcommQnn);
            Assert.DoesNotContain(SpeechModelCatalog.AvailableOptions(MachineSupport.Current), o => o.Backend == LegacyBackend.QualcommQnn);
        }
    }

    [Fact]
    public void The_qualcomm_download_is_offered_only_where_qnn_can_run()
    {
        Assert.Contains(SpeechModelCatalog.AvailableOptions(Snapdragon), o => o.Id == AihubId);
        Assert.DoesNotContain(SpeechModelCatalog.AvailableOptions(MachineSupport.None), o => o.Backend == LegacyBackend.QualcommQnn);
        Assert.Equal(SpeechModelCatalog.Options.Count - 1, SpeechModelCatalog.AvailableOptions(MachineSupport.None).Count);
    }

    // --- catalog ---

    [Fact]
    public void The_catalog_matches_the_wpf_id_folder_urls_and_size()
    {
        var option = Assert.Single(SpeechModelCatalog.Options, o => o.Backend == LegacyBackend.QualcommQnn);
        Assert.Equal(AihubId, option.Id);
        Assert.Equal(AihubFolder, option.InstallDirectoryName);
        Assert.Equal("qualcomm-aihub-whisper", option.SubFolder);
        Assert.Equal("qualcomm-qnn:" + AihubId, option.ModelId);
        Assert.Equal(520_029_222, option.ApproximateBytes);
        Assert.False(option.IsSingleFile);
        Assert.Equal(
            new Uri("https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/whisper_small/releases/v0.52.0/" + AihubFolder + ".zip"),
            option.DownloadUri);
        Assert.Equal(AihubFolder + ".zip", option.ArchiveFileName);

        var source = QualcommAihubWhisperCatalog.Options.Single();
        Assert.Equal(
            "https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/whisper_small/releases/v0.52.0/whisper_small-qnn_context_binary-float-qualcomm_snapdragon_x_elite.zip",
            source.RawContextDownloadUri.AbsoluteUri);
        Assert.StartsWith("https://raw.githubusercontent.com/openai/whisper/", QualcommAihubWhisperCatalog.TokenizerUri);
        Assert.Equal(
            ["encoder.onnx", "decoder.onnx", "encoder_qairt_context.bin", "decoder_qairt_context.bin", "multilingual.tiktoken", "metadata.json"],
            QualcommAihubWhisperCatalog.RequiredFiles);
    }

    [Fact]
    public void A_package_is_valid_only_with_every_file_including_the_tokenizer()
    {
        this.InstallAihub(tokenizer: false);
        Assert.False(QualcommAihubWhisperCatalog.IsValidModelDirectory(this.AihubPath));
        Assert.False(QualcommAihubWhisperCatalog.TryResolveDirectory(this.AihubPath, out _));

        File.WriteAllText(Path.Combine(this.AihubPath, "multilingual.tiktoken"), "x");
        Assert.True(QualcommAihubWhisperCatalog.IsValidModelDirectory(this.AihubPath));
        Assert.True(QualcommAihubWhisperCatalog.TryResolveArtifacts(this.AihubPath, out var artifacts));
        Assert.Equal(Path.Combine(Path.GetFullPath(this.AihubPath), "encoder_qairt_context.bin"), artifacts!.Value.EncoderContextPath);
        Assert.Equal(AihubId, QualcommAihubWhisperCatalog.TryGetByPath(this.AihubPath)?.Id);
        Assert.Null(QualcommAihubWhisperCatalog.TryGetByPath(Path.Combine(this.root, "nowhere")));
    }

    [Fact]
    public void The_raw_context_package_is_recognized_so_the_user_is_told_it_cannot_run()
    {
        var raw = Path.Combine(this.root, "raw");
        Directory.CreateDirectory(raw);
        foreach (var f in new[] { "encoder.bin", "decoder.bin", "metadata.json" })
        {
            File.WriteAllText(Path.Combine(raw, f), "x");
        }

        Assert.True(QualcommAihubWhisperCatalog.IsRawContextOnlyDirectory(raw));
        Assert.False(QualcommAihubWhisperCatalog.IsValidModelDirectory(raw));
        File.WriteAllText(Path.Combine(raw, "encoder.onnx"), "x");
        Assert.False(QualcommAihubWhisperCatalog.IsRawContextOnlyDirectory(raw));
    }

    // --- discovery ---

    [Fact]
    public void An_installed_package_is_listed_only_on_a_machine_that_can_run_it()
    {
        this.InstallAihub();

        Assert.DoesNotContain(SpeechModelLocator.Discover(this.root, MachineSupport.None), m => m.Backend == LegacyBackend.QualcommQnn);
        var found = Assert.Single(SpeechModelLocator.Discover(this.root, Snapdragon));
        Assert.Equal("qualcomm-qnn:" + AihubId, found.ModelId);
        Assert.Equal(LegacyBackend.QualcommQnn, found.Backend);
        Assert.True(found.IsEnglishOnly);
        Assert.Equal(Path.GetFullPath(this.AihubPath), found.Directory);
        Assert.True(SpeechModelLocator.IsQualcommAihubWhisper(found));
    }

    private void InstallMoonshine(bool qnn)
    {
        var option = SpeechModelCatalog.Options.Single(o => o.Id == "moonshine-tiny-v2-en");
        var dir = SpeechModelLocator.InstallPath(this.root, option);
        Directory.CreateDirectory(dir);
        foreach (var f in new[] { "tokens.txt", "encoder_model.ort", "decoder_model_merged.ort" })
        {
            File.WriteAllText(Path.Combine(dir, f), "x");
        }

        if (qnn)
        {
            Directory.CreateDirectory(Path.Combine(dir, "qnn"));
            File.WriteAllText(Path.Combine(dir, "qnn", "encoder.qdq.onnx"), "x");
            File.WriteAllText(Path.Combine(dir, "qnn", "decoder.qdq.onnx"), "x");
        }
    }

    [Fact]
    public void A_moonshine_model_with_prepared_qnn_files_is_also_offered_on_the_npu()
    {
        this.InstallMoonshine(qnn: true);

        var onCpuOnly = SpeechModelLocator.Discover(this.root, MachineSupport.None);
        Assert.Equal(["moonshine-onnx:moonshine-tiny-v2-en"], onCpuOnly.Select(m => m.ModelId));

        var onSnapdragon = SpeechModelLocator.Discover(this.root, Snapdragon);
        Assert.Equal(["moonshine-onnx:moonshine-tiny-v2-en", "qualcomm-qnn:moonshine-tiny-v2-en"], onSnapdragon.Select(m => m.ModelId));
        var npu = onSnapdragon.Single(m => m.Backend == LegacyBackend.QualcommQnn);
        Assert.EndsWith("(Qualcomm NPU)", npu.DisplayName);
        Assert.False(SpeechModelLocator.IsQualcommAihubWhisper(npu));
    }

    [Fact]
    public void A_moonshine_model_without_prepared_qnn_files_is_not_offered_on_the_npu()
    {
        this.InstallMoonshine(qnn: false);
        Assert.Equal(["moonshine-onnx:moonshine-tiny-v2-en"], SpeechModelLocator.Discover(this.root, Snapdragon).Select(m => m.ModelId));
    }

    // --- settings migration ---

    private const string WpfQualcommJson = $$"""
        {
          "FirstRunCompleted": true,
          "TranscriptionBackend": "QualcommQnn",
          "SelectedModelId": "{{AihubId}}",
          "TranscriptionComputeInterface": "Npu"
        }
        """;

    [Fact]
    public void The_wpf_qualcomm_setup_selects_the_qualcomm_model_without_touching_the_wpf_file()
    {
        var store = new DictationSettingsStore(new AppDataPaths(this.root));
        File.WriteAllText(store.WpfSettingsPath, WpfQualcommJson);

        var settings = store.Load().Settings;
        Assert.Equal(LegacyBackend.QualcommQnn, settings.TranscriptionBackend);
        Assert.Equal(LegacyComputeInterface.Npu, settings.TranscriptionComputeInterface);
        Assert.Equal("qualcomm-qnn:" + AihubId, settings.ResolveModelId());

        store.Save(settings);
        Assert.Equal(WpfQualcommJson, File.ReadAllText(store.WpfSettingsPath));
        Assert.Equal("qualcomm-qnn:" + AihubId, store.Load().Settings.ResolveModelId());
    }

    [Fact]
    public void The_selected_qualcomm_model_resolves_on_a_snapdragon_pc()
    {
        this.InstallAihub();
        var settings = new DictationSettings { TranscriptionBackend = LegacyBackend.QualcommQnn, SelectedModelId = AihubId };
        var installed = SpeechModelLocator.Discover(this.root, Snapdragon);
        Assert.Equal("qualcomm-qnn:" + AihubId, SpeechModelLocator.Resolve(installed, settings)?.ModelId);
        Assert.Null(SpeechModelLocator.Resolve(SpeechModelLocator.Discover(this.root, MachineSupport.None), settings));
    }

    [Fact]
    public void A_qualcomm_moonshine_selection_without_the_npu_runs_the_same_moonshine_on_the_cpu()
    {
        this.InstallMoonshine(qnn: false);
        var settings = new DictationSettings { TranscriptionBackend = LegacyBackend.QualcommQnn, SelectedModelId = "moonshine-tiny-v2-en" };
        var resolved = SpeechModelLocator.Resolve(SpeechModelLocator.Discover(this.root, Snapdragon), settings);
        Assert.Equal("moonshine-onnx:moonshine-tiny-v2-en", resolved?.ModelId);
    }

    [Fact]
    public void A_custom_model_path_resolves_a_qualcomm_package_as_in_wpf()
    {
        var custom = Path.Combine(this.root, "my-package");
        Directory.CreateDirectory(custom);
        foreach (var f in QualcommAihubWhisperCatalog.RequiredFiles)
        {
            File.WriteAllText(Path.Combine(custom, f), "x");
        }

        Assert.True(SpeechModelLocator.TryResolveCustom(LegacyBackend.QualcommQnn, custom, out var model, out _));
        Assert.Equal("qualcomm-qnn:" + AihubId, model.ModelId);
        Assert.True(model.IsCustom);
        Assert.True(SpeechModelLocator.IsQualcommAihubWhisper(model));
        // Only tried when the Qualcomm family is the configured one.
        Assert.False(SpeechModelLocator.TryResolveCustom(custom, LegacyBackend.Whisper, out _, out _));
        Assert.True(SpeechModelLocator.TryResolveCustom(custom, LegacyBackend.QualcommQnn, out _, out _));

        var settings = new DictationSettings { TranscriptionBackend = LegacyBackend.QualcommQnn, ModelPath = custom };
        Assert.Contains(SpeechModelLocator.DiscoverFor(this.root, settings, [], Snapdragon), m => m.IsCustom);
        Assert.DoesNotContain(SpeechModelLocator.DiscoverFor(this.root, settings, [], MachineSupport.None), m => m.IsCustom);
    }

    [Fact]
    public void A_raw_context_folder_as_a_custom_path_is_refused_with_the_wpf_explanation()
    {
        var raw = Path.Combine(this.root, "raw2");
        Directory.CreateDirectory(raw);
        foreach (var f in new[] { "encoder.bin", "decoder.bin", "metadata.json" })
        {
            File.WriteAllText(Path.Combine(raw, f), "x");
        }

        Assert.False(SpeechModelLocator.TryResolveCustom(LegacyBackend.QualcommQnn, raw, out _, out var problem));
        Assert.Contains("raw qnn_context_binary", problem);
    }

    [Fact]
    public void A_custom_qualcomm_path_is_cleared_when_the_pc_cannot_run_it()
    {
        var custom = Path.Combine(this.root, "pkg");
        Directory.CreateDirectory(custom);
        foreach (var f in QualcommAihubWhisperCatalog.RequiredFiles)
        {
            File.WriteAllText(Path.Combine(custom, f), "x");
        }

        var settings = new DictationSettings { TranscriptionBackend = LegacyBackend.QualcommQnn, ModelPath = custom };
        Assert.NotNull(HardwareNormalization.Normalize(settings, MachineSupport.None, this.root));
        Assert.Equal(LegacyBackend.Whisper, settings.TranscriptionBackend);
        Assert.Null(settings.ModelPath);
    }

    [Fact]
    public void Nothing_selected_resolves_to_nothing()
    {
        Assert.Null(SpeechModelLocator.Resolve([], new DictationSettings()));
    }

    // --- download and install ---

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

    internal static byte[] Zip(params string[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                writer.Write("content of " + entry);
            }
        }

        return stream.ToArray();
    }

    private static readonly Uri TokenizerUri = new("https://example.test/multilingual.tiktoken");

    private ModelDownloader Downloader(Router router) =>
        new(new HttpClient(router)) { QualcommTokenizerUri = TokenizerUri };

    private static readonly ModelDownloadOption AihubOption = SpeechModelCatalog.Options.Single(o => o.Backend == LegacyBackend.QualcommQnn);

    [Fact]
    public async Task The_package_is_unpacked_the_tokenizer_added_and_the_folder_moved_into_place()
    {
        var zip = Zip($"{AihubFolder}/encoder.onnx", $"{AihubFolder}/decoder.onnx", $"{AihubFolder}/encoder_qairt_context.bin", $"{AihubFolder}/decoder_qairt_context.bin", $"{AihubFolder}/metadata.json");
        var router = new Router(uri => (HttpStatusCode.OK, uri == TokenizerUri ? "tokens"u8.ToArray() : zip));
        var stages = new List<string>();

        var path = await this.Downloader(router).DownloadAsync(AihubOption, this.root, new Progress<ModelDownloadProgress>(p => stages.Add(p.Stage)));

        Assert.Equal(this.AihubPath, path);
        Assert.True(QualcommAihubWhisperCatalog.IsValidModelDirectory(path));
        Assert.Equal("tokens", File.ReadAllText(Path.Combine(path, "multilingual.tiktoken")));
        Assert.Equal([AihubOption.DownloadUri, TokenizerUri], router.Requests);
        Assert.Contains("extract", stages);
        Assert.Equal("ready", stages[^1]);
        Assert.All(Directory.GetFileSystemEntries(Path.Combine(this.root, "qualcomm-aihub-whisper")), p => Assert.False(p.EndsWith(".download") || p.EndsWith(".extract")));

        // Already installed: nothing is fetched again.
        var again = new Router(_ => (HttpStatusCode.OK, []));
        await this.Downloader(again).DownloadAsync(AihubOption, this.root);
        Assert.Empty(again.Requests);
    }

    [Fact]
    public async Task A_package_missing_a_context_binary_installs_nothing()
    {
        var zip = Zip("encoder.onnx", "decoder.onnx", "metadata.json");
        var router = new Router(_ => (HttpStatusCode.OK, zip));
        await Assert.ThrowsAsync<InvalidOperationException>(() => this.Downloader(router).DownloadAsync(AihubOption, this.root));
        Assert.False(Directory.Exists(this.AihubPath));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(this.root, "qualcomm-aihub-whisper")));
    }

    [Fact]
    public async Task A_tokenizer_that_cannot_be_fetched_installs_nothing()
    {
        var zip = Zip("a/encoder.onnx", "a/decoder.onnx", "a/encoder_qairt_context.bin", "a/decoder_qairt_context.bin", "a/metadata.json");
        var router = new Router(uri => uri == TokenizerUri ? (HttpStatusCode.NotFound, []) : (HttpStatusCode.OK, zip));
        await Assert.ThrowsAsync<HttpRequestException>(() => this.Downloader(router).DownloadAsync(AihubOption, this.root));
        Assert.False(Directory.Exists(this.AihubPath));
    }

    [Fact]
    public async Task An_http_error_installs_nothing()
    {
        var router = new Router(_ => (HttpStatusCode.NotFound, []));
        await Assert.ThrowsAsync<HttpRequestException>(() => this.Downloader(router).DownloadAsync(AihubOption, this.root));
        Assert.False(Directory.Exists(this.AihubPath));
    }

    // --- normalization of a setup carried over from another PC ---

    private DictationSettings WpfQualcomm(string id = AihubId, LegacyComputeInterface? compute = LegacyComputeInterface.Npu) =>
        new() { TranscriptionBackend = LegacyBackend.QualcommQnn, SelectedModelId = id, TranscriptionComputeInterface = compute };

    [Fact]
    public void A_qualcomm_setup_is_kept_on_a_snapdragon_pc()
    {
        var settings = this.WpfQualcomm();
        Assert.Null(HardwareNormalization.Normalize(settings, Snapdragon, this.root));
        Assert.Equal(LegacyBackend.QualcommQnn, settings.TranscriptionBackend);
        Assert.Equal(AihubId, settings.SelectedModelId);
        Assert.Equal(LegacyComputeInterface.Npu, settings.TranscriptionComputeInterface);
    }

    [Fact]
    public void A_qualcomm_package_selection_falls_back_to_whisper_on_the_cpu_elsewhere()
    {
        var settings = this.WpfQualcomm();
        var note = HardwareNormalization.Normalize(settings, MachineSupport.None, this.root);
        Assert.NotNull(note);
        Assert.Equal(LegacyBackend.Whisper, settings.TranscriptionBackend);
        Assert.Null(settings.SelectedModelId);
        Assert.Equal(LegacyComputeInterface.Cpu, settings.TranscriptionComputeInterface);
        Assert.Null(settings.ResolveModelId());
    }

    [Fact]
    public void A_qualcomm_moonshine_selection_keeps_its_model_on_the_cpu_elsewhere()
    {
        var settings = this.WpfQualcomm("moonshine-tiny-v2-en");
        Assert.NotNull(HardwareNormalization.Normalize(settings, MachineSupport.None, this.root));
        Assert.Equal(LegacyBackend.Moonshine, settings.TranscriptionBackend);
        Assert.Equal("moonshine-onnx:moonshine-tiny-v2-en", settings.ResolveModelId());
        Assert.Equal(LegacyComputeInterface.Cpu, settings.TranscriptionComputeInterface);
    }

    [Fact]
    public void A_hardware_choice_this_pc_cannot_honour_moves_to_the_best_installed_hardware_setup()
    {
        // The setup says Qualcomm NPU; this PC has an NVIDIA GPU and the Whisper.net model installed.
        var option = SpeechModelCatalog.Options.Single(o => o.Backend == LegacyBackend.WhisperNet && o.Id == "large-v3-turbo");
        var path = SpeechModelLocator.InstallPath(this.root, option);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = File.Create(path))
        {
            file.SetLength(option.ApproximateBytes);
        }

        var gpuPc = new MachineSupport(false, true, false, MachineSupport.None.Qnn);
        var settings = this.WpfQualcomm();
        Assert.NotNull(HardwareNormalization.Normalize(settings, gpuPc, this.root));
        Assert.Equal(LegacyBackend.WhisperNet, settings.TranscriptionBackend);
        Assert.Equal(LegacyComputeInterface.Gpu, settings.TranscriptionComputeInterface);
        Assert.Equal("large-v3-turbo", settings.SelectedModelId);
    }

    [Fact]
    public void A_gpu_choice_on_a_pc_without_one_goes_back_to_the_best_available()
    {
        var settings = new DictationSettings { TranscriptionBackend = LegacyBackend.WhisperNet, SelectedModelId = "base.en", TranscriptionComputeInterface = LegacyComputeInterface.Gpu };
        Assert.NotNull(HardwareNormalization.Normalize(settings, MachineSupport.None, this.root));
        Assert.Equal(LegacyComputeInterface.Cpu, settings.TranscriptionComputeInterface);
        Assert.Equal(LegacyBackend.WhisperNet, settings.TranscriptionBackend);
    }

    [Fact]
    public void A_pc_without_a_wpf_compute_choice_is_left_alone_and_an_explicit_device_wins()
    {
        var fresh = new DictationSettings { TranscriptionBackend = LegacyBackend.WhisperNet, SelectedModelId = "base.en" };
        Assert.Null(HardwareNormalization.Normalize(fresh, MachineSupport.None, this.root));

        var explicitChoice = new DictationSettings { TranscriptionBackend = LegacyBackend.WhisperNet, SelectedModelId = "base.en", TranscriptionComputeInterface = LegacyComputeInterface.Gpu, WhisperNetDevice = "cpu" };
        Assert.Null(HardwareNormalization.Normalize(explicitChoice, MachineSupport.None, this.root));
        Assert.Equal(LegacyComputeInterface.Gpu, explicitChoice.TranscriptionComputeInterface);
    }

    [Fact]
    public void Compute_choices_follow_the_wpf_rules()
    {
        var all = new MachineSupport(true, true, true, Arm64WithNatives);
        Assert.Equal([LegacyComputeInterface.Cpu], HardwareNormalization.ComputeChoices(LegacyBackend.Whisper, null, all));
        Assert.Equal([LegacyComputeInterface.Cpu, LegacyComputeInterface.Gpu, LegacyComputeInterface.Npu], HardwareNormalization.ComputeChoices(LegacyBackend.WhisperNet, "large-v3", all));
        // Only some models have an OpenVINO bundle.
        Assert.Equal([LegacyComputeInterface.Cpu, LegacyComputeInterface.Gpu], HardwareNormalization.ComputeChoices(LegacyBackend.WhisperNet, "base.en", all));
        Assert.Equal([LegacyComputeInterface.Cpu, LegacyComputeInterface.Npu], HardwareNormalization.ComputeChoices(LegacyBackend.QualcommQnn, AihubId, all));
        Assert.Equal([LegacyComputeInterface.Cpu], HardwareNormalization.ComputeChoices(LegacyBackend.QualcommQnn, AihubId, MachineSupport.None));
        Assert.Equal(LegacyComputeInterface.Gpu, HardwareNormalization.BestCompute(LegacyBackend.WhisperNet, "large-v3", all));
        Assert.Equal(LegacyComputeInterface.Npu, HardwareNormalization.BestCompute(LegacyBackend.QualcommQnn, AihubId, all));
        Assert.Equal(LegacyComputeInterface.Cpu, HardwareNormalization.BestCompute(LegacyBackend.Parakeet, null, all));
    }
}
