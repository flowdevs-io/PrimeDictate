using PrimeDictate.Platforms.Speech;

namespace PrimeDictate.Core.Tests;

public sealed class OnnxDeviceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-dev-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, true);
        }
    }

    private sealed class Probe(bool windows = true, string? pack = "/pack", HashSet<string>? files = null, bool loaded = false, string? loadFailure = null) : OnnxRuntimeDevice.IProbe
    {
        public bool IsWindows => windows;

        public string? FindPackDirectory() => pack;

        public IEnumerable<string> RuntimeDirectories(string packDirectory) => [packDirectory, "/cuda/bin"];

        public bool FileExists(string path) => (files ?? All()).Contains(path.Replace('\\', '/'));

        public bool OnnxRuntimeAlreadyLoaded => loaded;

        public int LoadCalls { get; private set; }

        public string? Load(string packDirectory, IReadOnlyList<string> runtimeDirectories)
        {
            this.LoadCalls++;
            return loadFailure;
        }

        public static HashSet<string> All() =>
        [
            .. OnnxRuntimeDevice.RequiredPackFiles.Select(f => "/pack/" + f),
            .. OnnxRuntimeDevice.RequiredRuntimeLibraries.Select(l => "/cuda/bin/" + l)
        ];
    }

    [Fact]
    public void Everything_present_selects_cuda_and_loads_once()
    {
        var probe = new Probe();
        var r = OnnxRuntimeDevice.Resolve(OnnxDevicePreference.Auto, probe);
        Assert.Equal("cuda", r.Provider);
        Assert.False(r.FellBack);
        Assert.Equal(1, probe.LoadCalls);
    }

    [Fact]
    public void Cpu_preference_never_touches_the_gpu()
    {
        var probe = new Probe();
        var r = OnnxRuntimeDevice.Resolve(OnnxDevicePreference.Cpu, probe);
        Assert.Equal("cpu", r.Provider);
        Assert.False(r.FellBack);
        Assert.Equal(0, probe.LoadCalls);
    }

    [Fact]
    public void Missing_cudnn_is_named_and_falls_back_to_cpu()
    {
        var files = Probe.All();
        files.Remove("/cuda/bin/cudnn64_9.dll");
        var probe = new Probe(files: files);
        var r = OnnxRuntimeDevice.Resolve(OnnxDevicePreference.Auto, probe);
        Assert.Equal("cpu", r.Provider);
        Assert.True(r.FellBack);
        Assert.Contains("cudnn64_9.dll", r.Summary);
        Assert.Contains("cuDNN 9", r.Summary);
        Assert.Equal(0, probe.LoadCalls);
    }

    [Fact]
    public void Explicit_cuda_skips_the_library_name_check_but_still_loads()
    {
        var files = Probe.All();
        files.Remove("/cuda/bin/cudnn64_9.dll");
        var probe = new Probe(files: files);
        Assert.Equal("cuda", OnnxRuntimeDevice.Resolve(OnnxDevicePreference.Cuda, probe).Provider);
        Assert.Equal(1, probe.LoadCalls);
    }

    [Fact]
    public void Missing_pack_and_non_windows_and_late_load_and_load_failure_all_fall_back_with_a_reason()
    {
        Assert.Contains("not installed", OnnxRuntimeDevice.Resolve(OnnxDevicePreference.Cuda, new Probe(pack: null)).Summary);
        Assert.Contains("only set up on Windows", OnnxRuntimeDevice.Resolve(OnnxDevicePreference.Cuda, new Probe(windows: false)).Summary);
        Assert.Contains("already loaded", OnnxRuntimeDevice.Resolve(OnnxDevicePreference.Auto, new Probe(loaded: true)).Summary);
        var failed = OnnxRuntimeDevice.Resolve(OnnxDevicePreference.Cuda, new Probe(loadFailure: "onnxruntime_providers_cuda.dll could not be loaded (error 126)"));
        Assert.Equal("cpu", failed.Provider);
        Assert.Contains("error 126", failed.Summary);
        Assert.Contains("CUDA was requested", failed.Summary);
        var files = Probe.All();
        files.Remove("/pack/onnxruntime_providers_cuda.dll");
        Assert.Contains("onnxruntime_providers_cuda.dll", OnnxRuntimeDevice.Resolve(OnnxDevicePreference.Auto, new Probe(files: files)).Summary);
    }

    [Theory]
    [InlineData("cpu", OnnxDevicePreference.Cpu)]
    [InlineData("CUDA", OnnxDevicePreference.Cuda)]
    [InlineData("gpu", OnnxDevicePreference.Cuda)]
    [InlineData("auto", OnnxDevicePreference.Auto)]
    [InlineData("nonsense", OnnxDevicePreference.Auto)]
    [InlineData(null, OnnxDevicePreference.Auto)]
    public void Preference_parsing(string? text, OnnxDevicePreference expected) => Assert.Equal(expected, OnnxRuntimeDevice.ParsePreference(text));

    [Fact]
    public void Whisper_keeps_both_precisions_and_prefers_int8_on_cpu()
    {
        var dir = Path.Combine(this.root, "sherpa-onnx-whisper-tiny.en");
        Directory.CreateDirectory(dir);
        foreach (var f in new[] { "tiny.en-encoder.onnx", "tiny.en-decoder.onnx", "tiny.en-encoder.int8.onnx", "tiny.en-decoder.int8.onnx", "tiny.en-tokens.txt" })
        {
            File.WriteAllText(Path.Combine(dir, f), "x");
        }

        Assert.True(WhisperOnnxModelLocator.TryResolve(dir, out var model));
        Assert.EndsWith("tiny.en-encoder.int8.onnx", model.Encoder);
        Assert.True(model.HasFullPrecision);
        Assert.EndsWith("tiny.en-encoder.onnx", model.EncoderFullPrecision);
    }

    [Fact]
    public void Parakeet_precision_follows_the_device_and_what_is_installed()
    {
        var dir = Path.Combine(this.root, "p");
        Directory.CreateDirectory(dir);
        void Touch(params string[] names)
        {
            foreach (var n in names)
            {
                File.WriteAllText(Path.Combine(dir, n), "x");
            }
        }

        Touch("encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt");
        Assert.Equal("int8", SpeechModelLocator.ParakeetPrecision(dir, preferHalf: true));
        Touch("encoder.fp16.onnx", "decoder.fp16.onnx", "joiner.fp16.onnx");
        Assert.Equal("fp16", SpeechModelLocator.ParakeetPrecision(dir, preferHalf: true));
        Assert.Equal("int8", SpeechModelLocator.ParakeetPrecision(dir, preferHalf: false));
        Assert.Null(SpeechModelLocator.ParakeetPrecision(Path.Combine(this.root, "none"), preferHalf: true));
    }

    [Fact]
    public void Fp16_parakeet_is_in_the_catalog_and_discovered_when_complete()
    {
        var option = SpeechModelCatalog.Options.Single(o => o.Id == "parakeet-tdt-0.6b-v2-fp16");
        var dir = SpeechModelLocator.InstallPath(this.root, option);
        Directory.CreateDirectory(dir);
        foreach (var f in new[] { "encoder.fp16.onnx", "decoder.fp16.onnx", "joiner.fp16.onnx", "tokens.txt" })
        {
            File.WriteAllText(Path.Combine(dir, f), "x");
        }

        Assert.Contains(SpeechModelLocator.Discover(this.root), m => m.ModelId == "parakeet-onnx:parakeet-tdt-0.6b-v2-fp16");
    }
}
