using System.Runtime.CompilerServices;
using PrimeDictate.Platforms.Nemotron;

namespace PrimeDictate.Core.Tests;

internal static class LogSandbox
{
    /// <summary>Keeps every test that starts a worker from writing to the developer's real log folder.</summary>
    [ModuleInitializer]
    internal static void Init() => NemotronLog.Directory = Path.Combine(Path.GetTempPath(), "primedictate-tests-logs");
}

[Collection("nemotron-log")]
public sealed class NemotronLogTests
{
    [Theory]
    [InlineData("[asr] model=.nemotron-3.5-asr-streaming-0.6b.q8_0.gguf head=rnnt backend=CUDA0 diarization=on", true)]
    [InlineData("[nemo-speech] serve session started", true)]
    [InlineData("[diar] loaded Nemotron-3-Diarization.q8_0.gguf on CUDA0", true)]
    [InlineData("[ctc-dbg] frame=12 id=40 piece=hello prob=0.98", false)]
    [InlineData("[boost] phrase=acme", false)]
    [InlineData("[unknown] anything", false)]
    [InlineData("[timing] encoder 120 ms", true)]
    [InlineData("[memstats] cuda 5073 MiB", true)]
    [InlineData("hello there this is what somebody said", false)]
    [InlineData("[asr] transcript: as far as your credits go", false)]
    [InlineData("[asr] partial hypothesis 'yeah now'", false)]
    [InlineData("""[http] {"type":"delta","delta":"hi"}""", false)]
    [InlineData("""[http] {"words":[{"word":"hi"}]}""", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void Only_the_workers_own_status_lines_are_kept(string? line, bool kept) =>
        Assert.Equal(kept, NemotronLog.Filter(line) is not null);

    [Fact]
    public void The_debug_variable_that_prints_word_pieces_is_removed_from_a_childs_environment()
    {
        var info = new System.Diagnostics.ProcessStartInfo("x");
        info.Environment["NEMO_SPEECH_CTC_DEBUG"] = "1";
        info.Environment["KEEP_ME"] = "1";

        NemotronLog.ScrubEnvironment(info);

        Assert.False(info.Environment.ContainsKey("NEMO_SPEECH_CTC_DEBUG"));
        Assert.True(info.Environment.ContainsKey("KEEP_ME"));
    }

    [Fact]
    public void Long_lines_are_cut_short()
    {
        var kept = NemotronLog.Filter("[asr] " + new string('x', 1000));

        Assert.True(kept!.Length <= 245);
    }

    [Fact]
    public void Lines_are_timestamped_and_the_file_rotates_at_its_size_cap()
    {
        var original = NemotronLog.Directory;
        var dir = Directory.CreateTempSubdirectory().FullName;
        NemotronLog.Directory = dir;
        try
        {
            NemotronLog.WorkerLine("[worker pid=1 cuda:0]", "[asr] backend=CUDA0");
            NemotronLog.WorkerLine("[worker pid=1 cuda:0]", "not a status line");
            var first = File.ReadAllLines(NemotronLog.FilePath);
            Assert.Single(first);
            Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z \[worker pid=1 cuda:0\] \[asr\] backend=CUDA0$", first[0]);

            File.WriteAllText(NemotronLog.FilePath, new string('x', 1_100_000));
            NemotronLog.Event("[worker pid=1 cuda:0]", "exited with code 0");

            Assert.True(File.Exists(NemotronLog.FilePath + ".1"));
            Assert.Single(File.ReadAllLines(NemotronLog.FilePath));
        }
        finally
        {
            NemotronLog.Directory = original;
            Directory.Delete(dir, true);
        }
    }
}
