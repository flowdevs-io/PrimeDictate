namespace PrimeDictate.Platforms.Speech.Qualcomm;

/// <summary>
/// The prepared QNN files of a Moonshine model: a <c>qnn</c> subfolder holding quantized (QDQ) copies of each stage, made by
/// <c>scripts/qnn/quantize_moonshine_for_qnn.py</c>. The stock Moonshine files are not NPU-ready, so a model without them is not offered
/// for the NPU. File-name rules live here and nowhere else.
/// </summary>
public static class MoonshineQnnArtifacts
{
    public const string FolderName = "qnn";

    private static readonly string[] V1Stages = ["preprocess.onnx", "encode.int8.onnx", "uncached_decode.int8.onnx", "cached_decode.int8.onnx"];

    /// <summary>
    /// The stage files to hand to the QNN provider (v2: encoder then decoder; v1: preprocess, encode, uncached decode, cached decode),
    /// or null when any stage is missing. v1 stages are looked up by the name derived from the model file (<c>encode.int8.qdq.onnx</c>)
    /// and by the plain stage name (<c>encode.qdq.onnx</c>), so what the script writes and what this accepts always agree.
    /// </summary>
    public static IReadOnlyList<string>? ResolveStages(string modelDirectory, MoonshineFiles files)
    {
        var qnnDirectory = Path.Combine(modelDirectory, FolderName);
        if (!Directory.Exists(qnnDirectory))
        {
            return null;
        }

        if (files.MergedDecoder is not null)
        {
            var encoder = Find(qnnDirectory, "encoder.qdq.onnx", "encoder.qnn.onnx", Derived(files.Encoder, ".qdq.onnx"), Derived(files.Encoder, ".qnn.onnx"));
            var decoder = Find(qnnDirectory, "decoder.qdq.onnx", "decoder.qnn.onnx", Derived(files.MergedDecoder, ".qdq.onnx"), Derived(files.MergedDecoder, ".qnn.onnx"));
            return encoder is not null && decoder is not null ? [encoder, decoder] : null;
        }

        var stages = new List<string>(V1Stages.Length);
        foreach (var stage in V1Stages)
        {
            var stem = stage[..^".onnx".Length];
            var plainStem = stem.Replace(".int8", string.Empty, StringComparison.Ordinal);
            var path = Find(
                qnnDirectory,
                $"{stem}.qdq.onnx",
                $"{stem}.qnn.onnx",
                $"{plainStem}.qdq.onnx",
                $"{plainStem}.qnn.onnx");
            if (path is null)
            {
                return null;
            }

            stages.Add(path);
        }

        return stages;
    }

    /// <summary>True when the installed Moonshine model has every prepared QNN stage.</summary>
    public static bool IsPrepared(string modelDirectory) =>
        SpeechModelLocator.ResolveMoonshine(modelDirectory) is { } files && ResolveStages(modelDirectory, files) is not null;

    private static string Derived(string stockFile, string suffix) =>
        Path.GetFileName(stockFile).Replace(".onnx", suffix, StringComparison.Ordinal).Replace(".ort", suffix, StringComparison.Ordinal);

    private static string? Find(string directory, params string[] names) =>
        names.Select(n => Path.Combine(directory, n)).FirstOrDefault(File.Exists);
}
