using PrimeDictate.Core.Audio;

// Usage: ChannelSkew <recording-16k-stereo.wav>
// Left channel = microphone, right channel = system audio. Works when the microphone heard the speakers.
if (args.Length >= 1 && args[0] == "--probe")
{
    // ChannelSkew --probe [--save recording.wav]: self-contained measurement, no app needed.
    var save = args.Length >= 3 && args[1] == "--save" ? args[2] : null;
    return await Probe.RunAsync(save, args.Contains("--allow-headphones"));
}

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: ChannelSkew <recording-16k-stereo.wav> | ChannelSkew --probe [--save out.wav]");
    return 2;
}

var left = new List<float>();
var right = new List<float>();
var rate = 16_000;
await foreach (var frame in new WavAudioDecoder().DecodeAsync(args[0], 0, default))
{
    if (frame.Format.Channels != 2)
    {
        Console.Error.WriteLine("The recording is not stereo.");
        return 2;
    }

    rate = frame.Format.SampleRate;
    var s = frame.Samples.Span;
    for (var i = 0; i < s.Length; i += 2)
    {
        left.Add(s[i]);
        right.Add(s[i + 1]);
    }
}

var result = ChannelSkew.Estimate(left.ToArray(), right.ToArray(), rate);
Console.WriteLine($"microphone is {result.MicrophoneLagMs:+0;-0;0} ms after system audio (correlation {result.Correlation:0.00}, {left.Count / (double)rate:0.0} s)");
if (result.Correlation < 0.3)
{
    Console.WriteLine("Low correlation: the microphone probably did not hear the speakers, so this number means nothing.");
}

return 0;
