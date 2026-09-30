using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Transcripts;
using PrimeDictate.Platforms.Nemotron;

namespace PrimeDictate.Core.Tests;

[Collection("nemotron-log")]
public sealed class DiarizationOverlayTests
{
    private const string Rttm = """
        SPEAKER file 1 1.000 4.000 <NA> <NA> speaker_0 <NA> <NA>
        SPEAKER file 1 3.000 3.000 <NA> <NA> speaker_1 <NA> <NA>
        SPEAKER file 1 9.000 1.000 <NA> <NA> speaker_0 <NA> <NA>
        garbage line
        """;

    [Fact]
    public void Rttm_is_parsed_and_overlap_between_speakers_is_measured()
    {
        var overlay = new DiarizationOverlay(DiarizationOverlay.ParseRttm(Rttm));

        Assert.Equal(3, overlay.Segments.Count);
        Assert.Equal(2, overlay.SpeakerCount);
        Assert.Equal(new DiarizationSegment("speaker_0", 1, 5), overlay.Segments[0]);
        Assert.Equal(2, overlay.OverlapSeconds, 3); // 3 s to 5 s
    }

    [Fact]
    public void Worker_json_becomes_segments_and_bad_json_becomes_none()
    {
        var segments = DiarizationOverlay.ParseJson("""{"file":"x","segments":[{"start":2.0,"end":3.0,"speaker":2},{"start":0.0,"end":2.039,"speaker":1},{"start":5,"end":4,"speaker":1}]}""");

        Assert.Equal([new DiarizationSegment("speaker_1", 0, 2.039), new DiarizationSegment("speaker_2", 2, 3)], segments);
        Assert.Empty(DiarizationOverlay.ParseJson("not json"));
        Assert.Empty(DiarizationOverlay.ParseJson("{}"));
    }

    private static TranscriptSegment Line(string id, string speaker, double start, double end) =>
        TestData.Segment(id, "words", start, end) with { Speakers = [new SpeakerAttribution(speaker, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), null)] };

    [Fact]
    public void Diarizer_speakers_map_to_the_transcript_speaker_they_overlap_and_the_microphone_is_left_alone()
    {
        var document = TestData.NewDocument() with
        {
            Speakers = [new("local", "You", null), new("speaker-1", "Speaker 1", null), new("speaker-2", "Speaker 2", null)],
            Segments = [Line("a", "speaker-1", 1, 5), Line("b", "speaker-2", 3, 6), Line("c", "local", 2, 4), Line("d", "speaker-1", 9, 10)]
        };
        var overlay = new DiarizationOverlay(DiarizationOverlay.ParseRttm(Rttm));

        var bars = overlay.MapTo(document);

        Assert.Equal(3, bars.Count);
        Assert.Equal(["speaker-1", "speaker-2", "speaker-1"], bars.Select(b => b.SpeakerId));
        Assert.DoesNotContain(bars, b => b.SpeakerId == "local");
    }

    [Fact]
    public void Overlay_survives_a_save_and_load()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            new DiarizationOverlay(DiarizationOverlay.ParseRttm(Rttm)).Save(dir);
            Assert.Equal(3, DiarizationOverlay.TryLoad(dir)!.Segments.Count);
            Assert.Null(DiarizationOverlay.TryLoad(Path.Combine(dir, "missing")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Only_the_system_channel_is_sent_to_the_diarizer_and_its_answer_is_read()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var stereo = Path.Combine(dir, "recording-16k-stereo.wav");
            using (var writer = new WavFileWriter(stereo, 16_000, 2))
            {
                var samples = new float[16_000 * 2];
                for (var i = 0; i < 16_000; i++)
                {
                    samples[i * 2] = 0.5f; // left: microphone
                    samples[(i * 2) + 1] = -0.25f; // right: system audio
                }

                writer.Write(samples);
            }

            var seen = Path.Combine(dir, "args.txt");
            var script = Path.Combine(dir, "nemo-speech");
            File.WriteAllText(script, $$"""
                #!/bin/sh
                echo "$@" > {{seen}}
                cp "$2" {{dir}}/received.wav
                echo '{"segments":[{"start":0.000,"end":0.500,"speaker":1}]}'
                """);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var (overlay, error) = await NemotronDiarizer.RunAsync(script, "/m/diar.gguf", "cuda:0", stereo, dir, TimeSpan.FromSeconds(30), null, default);

            Assert.Null(error);
            Assert.Single(overlay!.Segments);
            var args = File.ReadAllText(seen);
            Assert.Contains("diarize", args);
            Assert.Contains("--format json", args);
            Assert.Contains("--model /m/diar.gguf", args);
            Assert.Contains("--device cuda:0", args);
            var received = new List<float>();
            await foreach (var frame in new WavAudioDecoder().DecodeAsync(Path.Combine(dir, "received.wav"), 0, default))
            {
                Assert.Equal(1, frame.Format.Channels);
                received.AddRange(frame.Samples.ToArray());
            }

            Assert.Equal(16_000, received.Count);
            Assert.All(received, v => Assert.InRange(v, -0.26f, -0.24f));
            Assert.False(File.Exists(Path.Combine(dir, "system-channel-16k-mono.wav")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task A_failing_diarizer_reports_why_and_returns_no_overlay()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var stereo = Path.Combine(dir, "s.wav");
            using (var writer = new WavFileWriter(stereo, 16_000, 2))
            {
                writer.Write(new float[3200]);
            }

            var script = Path.Combine(dir, "nemo-speech");
            File.WriteAllText(script, "#!/bin/sh\necho 'unknown option' >&2\nexit 2\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var (overlay, error) = await NemotronDiarizer.RunAsync(script, "/m/d.gguf", "cpu", stereo, dir, TimeSpan.FromSeconds(30), null, default);

            Assert.Null(overlay);
            Assert.Contains("code 2", error);
            Assert.Contains("unknown option", error);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
