# Qualcomm NPU (QNN) and the onnxruntime arrangement

Status of what ran where: `stage-7-dictation-status.md`, section "NPU". This note records the design and the rules that are easy to break.

## What is offered, and where

| Model | Runs on | Offered when |
|---|---|---|
| Qualcomm AI Hub Whisper Small (`qualcomm-qnn:qaihub-whisper-small-snapdragon-x-elite`) | QNN HTP, CPU fallback disabled | A native Windows ARM64 process with `QnnHtp.dll`, `QnnSystem.dll`, `onnxruntime_providers_qnn.dll` next to the app, and the package installed |
| An installed Moonshine model with a prepared `qnn` folder (`qualcomm-qnn:<moonshine id>`, listed as "(Qualcomm NPU)") | QNN HTP; sherpa-onnx on the CPU if the NPU session cannot be created or fails, unless `PRIMEDICTATE_QNN_STRICT=1` | Same, and `qnn/` holds every stage (see `MoonshineQnnArtifacts`) |

Everything else about the gate is `MachineSupport` (one record: Whisper.net CUDA, Vulkan, OpenVINO and the Qualcomm availability). Nothing outside `Platforms/Speech` decides what hardware can run.

The provider for a `qualcomm-qnn` model is chosen as the WPF engine chose: the AI Hub package by its catalog id, otherwise Moonshine (`SpeechProviders.Create`).

## One onnxruntime.dll per process

sherpa-onnx (Whisper, Parakeet, Moonshine on the CPU) and Microsoft.ML.OnnxRuntime.QNN each ship an `onnxruntime.dll` for win-arm64, both 1.24.4. A process loads one. As in the WPF app, the build and publish of `PrimeDictate.Desktop` copy the QNN package's natives (`onnxruntime.dll`, `onnxruntime_providers_qnn.dll`, `onnxruntime_providers_shared.dll`, `QnnHtp.dll`, `QnnSystem.dll`, the HTP stubs and skels) over the output for `win-arm64`, so sherpa-onnx and the managed ONNX Runtime API share the QNN build.

- `PrimeDictate.Platforms` references only `Microsoft.ML.OnnxRuntime.QNN`. It pulls the managed API (`Microsoft.ML.OnnxRuntime.Managed`) for every RID and natives for win-arm64 only, so it cannot collide with sherpa-onnx's library on x64, Linux or macOS. (The WPF app also referenced the full `Microsoft.ML.OnnxRuntime`, which brings natives for every RID; the new app does not need it.)
- Keep `OnnxRuntimeVersion` (`Directory.Build.props`) equal to the version inside the sherpa-onnx package.
- `scripts/Publish-Windows.ps1` fails a win-arm64 publish that lacks the QNN natives and any other publish that has them.
- On win-x64 nothing changes: sherpa-onnx's `onnxruntime.dll` stays, and the optional CUDA drop-in (`OnnxRuntimeDevice`) still replaces it as before.

## Do not probe by loading whisper.dll

Whisper.net's native builds (`runtimes/{cuda,vulkan,openvino}/win-x64/whisper.dll`) cannot be probed by loading and freeing them. Loading one variant, freeing it, then loading another (which Whisper.net does for its real runtime) aborts the process with `GGML_ASSERT(prev != ggml_uncaught_exception)`. `MachineSupport` therefore only looks for files: the variant's `whisper.dll`, the NVIDIA or Vulkan driver DLL, and for OpenVINO the `openvino.dll` the OpenVINO build imports, on the app folder, system folder and PATH. Whisper.net's own library order still falls through to the CPU if a variant fails to load.

## What cannot be tested here

The ONNX Runtime sessions and decode loops (`QualcommAihubWhisperTranscriber`, `MoonshineV1QnnTranscriber`, `MoonshineV2QnnTranscriber`) need a Snapdragon PC. On such a PC, `PrimeDictate.exe --qnn-aihub-whisper-transcribe <package folder> <16 kHz mono wav> out.json` is the quickest proof that the NPU runs; the report lists the loaded `onnxruntime` and `Qnn*` modules.
