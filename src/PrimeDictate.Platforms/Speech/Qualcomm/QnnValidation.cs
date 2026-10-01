using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using NAudio.Wave;
using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Platforms.Speech.Qualcomm;

/// <summary>One of the developer validation commands (<c>--qnn-*</c>) of the WPF app.</summary>
public sealed record QnnValidationCommand(string Name, string ModelDirectory, string? Argument, string? OutputFile)
{
    public const string AihubTranscribe = "--qnn-aihub-whisper-transcribe";
    public const string WhisperSmoke = "--qnn-whisper-smoke";
    public const string WhisperProof = "--qnn-whisper-proof";
    public const string MoonshineSmoke = "--qnn-smoke";
    public const string MoonshineProof = "--qnn-proof";

    private static readonly string[] Names = [AihubTranscribe, WhisperSmoke, WhisperProof, MoonshineSmoke, MoonshineProof];

    /// <summary>
    /// Recognizes the command in <paramref name="args"/>. Returns false when the first argument is not a <c>--qnn-*</c> command (the app starts
    /// normally); when it is one with missing arguments, <paramref name="usageError"/> says how to call it. Argument shapes match the WPF app.
    /// </summary>
    public static bool TryParse(string[] args, out QnnValidationCommand? command, out string? usageError)
    {
        command = null;
        usageError = null;
        if (args.Length == 0 || !Names.Contains(args[0], StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = Names.First(n => string.Equals(n, args[0], StringComparison.OrdinalIgnoreCase));
        switch (name)
        {
            case AihubTranscribe when args.Length < 3:
                usageError = "Usage: --qnn-aihub-whisper-transcribe <model-directory> <pcm16khz-mono-wav> [output-file]";
                return true;
            case AihubTranscribe:
                command = new QnnValidationCommand(name, args[1], args[2], args.Length >= 4 ? args[3] : null);
                return true;
            case WhisperSmoke or MoonshineSmoke when args.Length < 3:
                usageError = $"Usage: {name} <model-directory> <Cpu|Npu> [output-file]";
                return true;
            case WhisperSmoke or MoonshineSmoke:
                command = new QnnValidationCommand(name, args[1], args[2], args.Length >= 4 ? args[3] : null);
                return true;
            default:
                if (args.Length < 2)
                {
                    usageError = $"Usage: {name} <model-directory> [true|false] [output-file]";
                    return true;
                }

                if (args.Length >= 3 && !bool.TryParse(args[2], out _))
                {
                    usageError = $"Usage: {name} <model-directory> [true|false] [output-file] (the third argument must be true or false)";
                    return true;
                }

                command = new QnnValidationCommand(name, args[1], args.Length >= 3 ? args[2] : "true", args.Length >= 4 ? args[3] : null);
                return true;
        }
    }
}

/// <summary>
/// The WPF app's developer validation harness, for proving on a Snapdragon PC that the NPU does the work: each command prints (and optionally
/// writes) a JSON report with the runtime plan, whether strict validation passed, and the onnxruntime/QNN modules actually loaded. The
/// AI Hub command also prints the transcript of the given WAV, which is the whole point of it; nothing else in the app logs transcript text.
/// </summary>
public static class QnnValidation
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Runs a <c>--qnn-*</c> command and returns the process exit code (0, or 1 for a usage error or a failure to write). Returns -1 when <paramref name="args"/> is not one of these commands.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (!QnnValidationCommand.TryParse(args, out var command, out var usageError))
        {
            return -1;
        }

        if (command is null)
        {
            error.WriteLine(usageError);
            return 1;
        }

        try
        {
            var report = command.Name switch
            {
                QnnValidationCommand.AihubTranscribe => RunAihubWavTranscription(command.ModelDirectory, command.Argument!),
                QnnValidationCommand.WhisperSmoke => RunWhisperProbe(command.ModelDirectory, IsNpu(command.Argument), strict: IsNpu(command.Argument)),
                QnnValidationCommand.WhisperProof => RunWhisperProbe(command.ModelDirectory, npu: true, strict: bool.Parse(command.Argument!)),
                QnnValidationCommand.MoonshineSmoke => RunMoonshine(command.ModelDirectory, IsNpu(command.Argument), strict: IsNpu(command.Argument), proof: false),
                _ => RunMoonshine(command.ModelDirectory, npu: true, strict: bool.Parse(command.Argument!), proof: true)
            };
            output.WriteLine(report);
            WriteReport(report, command.OutputFile);
            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static bool IsNpu(string? computeInterface) =>
        string.Equals(computeInterface, nameof(LegacyComputeInterface.Npu), StringComparison.OrdinalIgnoreCase);

    private static void WriteReport(string report, string? outputFile)
    {
        if (string.IsNullOrWhiteSpace(outputFile))
        {
            return;
        }

        var full = Path.GetFullPath(outputFile);
        if (Path.GetDirectoryName(full) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(full, report);
    }

    private static Dictionary<string, object?> NewReport(string backend, string modelDirectory, bool strict) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["requestedBackend"] = backend,
        ["modelDirectory"] = modelDirectory,
        ["strictValidation"] = strict,
        ["availability"] = QnnRuntimeSupport.GetAvailability().Summary,
        ["baseDirectory"] = AppContext.BaseDirectory
    };

    /// <summary>Full transcription of a 16 kHz mono PCM WAV with the AI Hub package on the NPU (strict).</summary>
    public static string RunAihubWavTranscription(string modelDirectory, string wavFilePath)
    {
        var directory = Path.GetFullPath(modelDirectory);
        var wav = Path.GetFullPath(wavFilePath);
        var result = NewReport("Qualcomm AI Hub Whisper QNN", directory, strict: true);
        result["requestedRuntime"] = QnnActiveRuntime.QnnHtp.ToString();
        result["wavFile"] = wav;
        result["validationScope"] = "full-precompiled-qnn-onnx-transcription";
        try
        {
            if (!QualcommAihubWhisperCatalog.TryResolveArtifacts(directory, out var artifacts))
            {
                throw new FileNotFoundException("Qualcomm AI Hub Whisper model folder is incomplete. Expected encoder.onnx, decoder.onnx, context binaries, metadata.json, and multilingual.tiktoken.");
            }

            var samples = LoadPcm16KhzMonoWav(wav, out var duration, out var sourceFormat);
            var options = QnnRuntimeSupport.GetRuntimeOptions(directory, strictValidationOverride: true) with { EnableContextCache = false };
            using var transcriber = QualcommAihubWhisperTranscriber.Create(directory, options);
            var clock = Stopwatch.StartNew();
            var transcript = transcriber.Transcribe(samples, CancellationToken.None);
            clock.Stop();

            result["artifacts"] = DescribeArtifacts(artifacts.Value);
            result["sourceFormat"] = sourceFormat;
            result["audioSeconds"] = duration.TotalSeconds;
            result["elapsedSeconds"] = clock.Elapsed.TotalSeconds;
            result["transcript"] = transcript;
            result["runSucceeded"] = !string.IsNullOrWhiteSpace(transcript);
            result["proofStatus"] = string.IsNullOrWhiteSpace(transcript) ? "QNN HTP transcription ran but returned no text." : "QNN HTP transcription produced text.";
        }
        catch (Exception ex)
        {
            result["runSucceeded"] = false;
            result["proofStatus"] = "QNN HTP transcription validation failed.";
            result["error"] = ex.ToString();
        }

        result["loadedModules"] = CaptureRuntimeModuleSnapshot();
        return JsonSerializer.Serialize(result, Json);
    }

    /// <summary>Session creation only for the AI Hub package, or for a stock Whisper ONNX folder (with its <c>qnn</c> subfolder) on the QNN provider.</summary>
    public static string RunWhisperProbe(string modelDirectory, bool npu, bool strict)
    {
        var directory = Path.GetFullPath(modelDirectory);
        var runtime = npu ? QnnActiveRuntime.QnnHtp : QnnActiveRuntime.Cpu;
        var result = NewReport("Whisper ONNX QNN Probe", directory, strict);
        result["requestedRuntime"] = runtime.ToString();
        result["validationScope"] = "session-creation-only";
        result["limitations"] = "This harness validates direct encoder/decoder session creation only. It does not run Whisper feature extraction or autoregressive decoding in managed code.";
        try
        {
            var options = QnnRuntimeSupport.GetRuntimeOptions(directory, strictValidationOverride: strict) with { EnableContextCache = false };
            if (QualcommAihubWhisperCatalog.IsRawContextOnlyDirectory(directory))
            {
                throw new InvalidOperationException("This folder contains a raw qnn_context_binary package only. ONNX Runtime needs the matching precompiled_qnn_onnx package with encoder.onnx and decoder.onnx EPContext wrappers.");
            }

            if (QualcommAihubWhisperCatalog.TryResolveArtifacts(directory, out var aihub))
            {
                if (runtime != QnnActiveRuntime.QnnHtp)
                {
                    throw new InvalidOperationException("Qualcomm AI Hub precompiled_qnn_onnx Whisper packages require the QNN HTP provider; CPU execution is not supported for EPContext wrappers.");
                }

                using var encoder = CreateSession("QaihubWhisperEncoder", aihub.Value.EncoderOnnxPath, QnnActiveRuntime.QnnHtp, options, directory, contextCache: false);
                using var decoder = CreateSession("QaihubWhisperDecoder", aihub.Value.DecoderOnnxPath, QnnActiveRuntime.QnnHtp, options, directory, contextCache: false);
                result["requestedBackend"] = "Qualcomm AI Hub Whisper QNN";
                result["validationScope"] = "precompiled-qnn-onnx-session-creation-only";
                result["artifacts"] = DescribeArtifacts(aihub.Value);
                AddSessions(result, encoder, decoder);
                result["proofStatus"] = "QNN HTP strict validation passed for Qualcomm AI Hub Whisper EPContext session creation.";
                result["loadedModules"] = CaptureRuntimeModuleSnapshot();
                return JsonSerializer.Serialize(result, Json);
            }

            if (!WhisperOnnxModelLocator.TryResolve(directory, out var whisper))
            {
                throw new FileNotFoundException("Whisper ONNX model folder is incomplete. Expected encoder, decoder, and tokens files.");
            }

            var qnnDirectory = Path.Combine(directory, "qnn");
            var qnnEncoder = npu ? FindWhisperQnnArtifact(qnnDirectory, whisper.Encoder) : null;
            var qnnDecoder = npu ? FindWhisperQnnArtifact(qnnDirectory, whisper.Decoder) : null;
            var encoderPath = qnnEncoder ?? whisper.Encoder;
            var decoderPath = qnnDecoder ?? whisper.Decoder;
            using var encoderSession = CreateSession("QnnWhisperEncoder", encoderPath, runtime, options, directory, contextCache: true);
            using var decoderSession = CreateSession("QnnWhisperDecoder", decoderPath, runtime, options, directory, contextCache: true);
            result["artifacts"] = $"modelDirectory={directory}, encoder={Path.GetFileName(encoderPath)}, decoder={Path.GetFileName(decoderPath)}, tokens={Path.GetFileName(whisper.Tokens)}, qnnArtifacts={qnnEncoder is not null || qnnDecoder is not null}";
            AddSessions(result, encoderSession, decoderSession);
            result["proofStatus"] = runtime == QnnActiveRuntime.QnnHtp && strict
                ? "QNN HTP strict validation passed for Whisper encoder/decoder session creation."
                : "Whisper encoder/decoder session creation was not strictly validated on QNN HTP.";
        }
        catch (Exception ex)
        {
            result["runSucceeded"] = false;
            result["proofStatus"] = "QNN HTP strict validation failed for Whisper session creation.";
            result["error"] = ex.ToString();
        }

        result["loadedModules"] = CaptureRuntimeModuleSnapshot();
        return JsonSerializer.Serialize(result, Json);
    }

    /// <summary>One second of silence through a Moonshine model: on the NPU (strict when asked, so a CPU fallback cannot pass) or on the CPU.</summary>
    public static string RunMoonshine(string modelDirectory, bool npu, bool strict, bool proof)
    {
        var directory = Path.GetFullPath(modelDirectory);
        var runtime = npu ? QnnActiveRuntime.QnnHtp : QnnActiveRuntime.Cpu;
        var result = NewReport("QualcommQnn", directory, strict);
        if (!proof)
        {
            result["requestedRuntime"] = runtime.ToString();
        }

        try
        {
            var silence = new float[16_000];
            if (npu)
            {
                var options = QnnRuntimeSupport.GetRuntimeOptions(directory, strictValidationOverride: strict);
                using var transcriber = MoonshineQnnTranscriber.CreateNpu(directory, options);
                var transcript = transcriber.Transcribe(silence, CancellationToken.None);
                result["runtime"] = QnnActiveRuntime.QnnHtp.ToString();
                result["diagnostics"] = transcriber.DiagnosticsSummary;
                result["runSucceeded"] = true;
                result["transcriptLength"] = transcript.Length;
                if (proof)
                {
                    result["proofStatus"] = strict
                        ? "QNN HTP strict validation passed for session creation and inference run."
                        : "QNN HTP was not strictly validated.";
                }
            }
            else
            {
                var model = new InstalledSpeechModel(LegacyBackend.Moonshine, Path.GetFileName(directory), Path.GetFileName(directory), directory, true);
                var provider = new SherpaMoonshineProvider(model);
                IReadOnlyList<PrimeDictate.Core.Providers.RecognizedSegment> segments;
                try
                {
                    segments = provider.RecognizeWindowAsync(silence, "en", CancellationToken.None).AsTask().GetAwaiter().GetResult();
                }
                finally
                {
                    provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                result["runtime"] = QnnActiveRuntime.Cpu.ToString();
                result["diagnostics"] = "activeRuntime=Cpu, runtime=sherpa-onnx, provider=cpu";
                result["runSucceeded"] = true;
                result["transcriptLength"] = segments.Sum(s => s.Text.Length);
            }
        }
        catch (Exception ex)
        {
            result["runSucceeded"] = false;
            if (proof)
            {
                result["proofStatus"] = "QNN HTP strict validation failed.";
            }

            result["error"] = ex.ToString();
        }

        result["loadedModules"] = CaptureRuntimeModuleSnapshot();
        return JsonSerializer.Serialize(result, Json);
    }

    private static object DescribeArtifacts(QualcommAihubWhisperArtifacts artifacts) => new
    {
        artifacts.ModelDirectory,
        Encoder = Path.GetFileName(artifacts.EncoderOnnxPath),
        Decoder = Path.GetFileName(artifacts.DecoderOnnxPath),
        EncoderContext = Path.GetFileName(artifacts.EncoderContextPath),
        DecoderContext = Path.GetFileName(artifacts.DecoderContextPath)
    };

    private static void AddSessions(Dictionary<string, object?> result, InferenceSession encoder, InferenceSession decoder)
    {
        result["encoderSessionCreated"] = true;
        result["decoderSessionCreated"] = true;
        result["encoderInputs"] = encoder.InputNames;
        result["encoderOutputs"] = encoder.OutputNames;
        result["decoderInputs"] = decoder.InputNames;
        result["decoderOutputs"] = decoder.OutputNames;
        result["runSucceeded"] = true;
    }

    private static string? FindWhisperQnnArtifact(string qnnDirectory, string baseModelPath)
    {
        if (!Directory.Exists(qnnDirectory))
        {
            return null;
        }

        var baseFileName = Path.GetFileName(baseModelPath);
        var candidates = new[]
        {
            baseFileName,
            baseFileName.Replace(".int8.onnx", ".qdq.onnx", StringComparison.OrdinalIgnoreCase),
            baseFileName.Replace(".onnx", ".qdq.onnx", StringComparison.OrdinalIgnoreCase),
            baseFileName.Replace(".onnx", ".qnn.onnx", StringComparison.OrdinalIgnoreCase)
        };
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).Select(c => Path.Combine(qnnDirectory, c)).FirstOrDefault(File.Exists);
    }

    private static InferenceSession CreateSession(string sessionName, string modelPath, QnnActiveRuntime runtime, QnnRuntimeOptions options, string modelDirectory, bool contextCache)
    {
        string? contextPath = null;
        if (contextCache && runtime == QnnActiveRuntime.QnnHtp)
        {
            var contextDirectory = Path.Combine(modelDirectory, ".qnn-cache");
            Directory.CreateDirectory(contextDirectory);
            contextPath = Path.Combine(contextDirectory, $"{sessionName}_{Guid.NewGuid():N}_ctx.onnx");
        }

        using var sessionOptions = QnnRuntimeSupport.CreateSessionOptions(runtime, options, $"PrimeDictate.{sessionName}", contextPath);
        return new InferenceSession(modelPath, sessionOptions);
    }

    private static float[] LoadPcm16KhzMonoWav(string wavFilePath, out TimeSpan duration, out string sourceFormat)
    {
        using var reader = new WaveFileReader(wavFilePath);
        sourceFormat = reader.WaveFormat.ToString();
        if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm || reader.WaveFormat.SampleRate != 16_000 || reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels != 1)
        {
            throw new InvalidOperationException($"Validation WAV must be 16 kHz, 16-bit, mono PCM. Actual format: {sourceFormat}.");
        }

        var bytes = new byte[reader.Length];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = reader.Read(bytes, total, bytes.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        var samples = new float[total / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        }

        duration = TimeSpan.FromSeconds(samples.Length / 16_000d);
        return samples;
    }

    private static IReadOnlyList<string> CaptureRuntimeModuleSnapshot()
    {
        try
        {
            var snapshot = new List<string>();
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                if (module.ModuleName.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase) || module.ModuleName.Contains("qnn", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot.Add($"{module.ModuleName} => {module.FileName}");
                }
            }

            snapshot.Sort(StringComparer.OrdinalIgnoreCase);
            return snapshot;
        }
        catch (Exception ex)
        {
            return [$"<module snapshot unavailable>: {ex.Message}"];
        }
    }
}
