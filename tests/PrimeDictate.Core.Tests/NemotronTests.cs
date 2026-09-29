using System.Net;
using System.Text;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Pipeline;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Transcripts;
using PrimeDictate.Platforms.Nemotron;

namespace PrimeDictate.Core.Tests;

public sealed class NemotronTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-nemo-tests", Guid.NewGuid().ToString("N"));

    public NemotronTests() => Directory.CreateDirectory(this.root);

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    private static string SamplePath(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PrimeDictate.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return Path.Combine(dir!, "docs", "architecture", "nemotron-samples", name);
    }

    [Fact]
    public void Real_diarized_response_becomes_speaker_turns_with_word_timings()
    {
        var json = File.ReadAllText(SamplePath("file-verbose-json-diarization.json"));
        var segments = NemotronResponseParser.ParseVerboseJson(json, TimeSpan.FromSeconds(12), withSpeakers: true);

        Assert.True(segments.Count >= 2);
        Assert.All(segments, s => Assert.StartsWith("speaker-", s.SpeakerLabel));
        Assert.Contains(segments, s => s.SpeakerLabel == "speaker-1");
        Assert.Contains(segments, s => s.SpeakerLabel == "speaker-2");
        Assert.All(segments, s => Assert.Equal(TimingProvenance.Model, s.Provenance));
        Assert.All(segments, s => Assert.True(s.Words!.Count > 0 && s.Start <= s.End));
        // No word is lost or reordered by grouping.
        var joined = string.Join(' ', segments.Select(s => s.Text));
        Assert.StartsWith("From a plain text file", joined);
        Assert.Equal(segments.Sum(s => s.Words!.Count), segments.SelectMany(s => s.Words!).Count());
    }

    [Fact]
    public void Without_speakers_the_same_words_stay_one_segment()
    {
        var json = File.ReadAllText(SamplePath("file-verbose-json-diarization.json"));
        var segments = NemotronResponseParser.ParseVerboseJson(json, TimeSpan.FromSeconds(12), withSpeakers: false);
        var single = Assert.Single(segments);
        Assert.Null(single.SpeakerLabel);
    }

    [Fact]
    public void Response_without_words_falls_back_to_the_text_and_empty_text_gives_nothing()
    {
        var one = NemotronResponseParser.ParseVerboseJson("""{"text":"hello there"}""", TimeSpan.FromSeconds(3), false);
        Assert.Equal("hello there", Assert.Single(one).Text);
        Assert.Empty(NemotronResponseParser.ParseVerboseJson("""{"text":"  ","words":[]}""", TimeSpan.FromSeconds(3), false));
    }

    [Fact]
    public void Worker_arguments_are_loopback_cpu_and_local_paths_only()
    {
        var files = new NemotronModelFiles("/m/asr.gguf", "/m/diar.gguf");
        var args = NemotronWorker.BuildArguments(18080, files);

        Assert.Equal("serve", args[0]);
        Assert.Contains("--no-ui", args);
        Assert.Equal("127.0.0.1", args[args.ToList().IndexOf("--host") + 1]);
        Assert.Equal("cpu", args[args.ToList().IndexOf("--device") + 1]);
        Assert.Equal("/m/asr.gguf", args[args.ToList().IndexOf("--asr-model") + 1]);
        Assert.Equal("/m/diar.gguf", args[args.ToList().IndexOf("--diar-model") + 1]);
        Assert.DoesNotContain(args, a => a.Contains("api", StringComparison.OrdinalIgnoreCase) && a.Contains("key", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("--diar-model", NemotronWorker.BuildArguments(1, new NemotronModelFiles("/m/asr.gguf", null)));
    }

    [Fact]
    public void Only_pinned_model_files_with_the_pinned_size_are_accepted()
    {
        var wrongName = Path.Combine(this.root, "other.gguf");
        File.WriteAllText(wrongName, "x");
        Assert.Equal("model-unrecognized", Assert.Throws<NemotronException>(() => NemotronModelFiles.Verify(wrongName, null)).Code);

        var pinnedName = Path.Combine(this.root, NemotronPins.Multilingual.FileName);
        File.WriteAllText(pinnedName, "too small");
        Assert.Equal("model-corrupt", Assert.Throws<NemotronException>(() => NemotronModelFiles.Verify(pinnedName, null)).Code);

        Assert.Equal("model-missing", Assert.Throws<NemotronException>(() => NemotronModelFiles.Verify(Path.Combine(this.root, "none.gguf"), null)).Code);

        using (var fs = new FileStream(pinnedName, FileMode.Create))
        {
            fs.SetLength(NemotronPins.Multilingual.Bytes);
        }

        Assert.Equal(pinnedName, NemotronModelFiles.Verify(pinnedName, null).AsrPath);
    }

    private sealed class FakeEndpoint(HttpClient client, bool hasDiarizer) : INemotronEndpoint
    {
        public HttpClient Client { get; } = client;

        public bool HasDiarizer { get; } = hasDiarizer;

        public Uri BaseAddress { get; } = client.BaseAddress ?? new Uri("http://127.0.0.1:1/");

        public string ApiKey => "test-key";
    }

    private static async Task<(string Body, string Response)> ServeOnceAsync(HttpListener listener, string responseJson, int status = 200)
    {
        var context = await listener.GetContextAsync();
        using var reader = new StreamReader(context.Request.InputStream, Encoding.Latin1);
        var body = await reader.ReadToEndAsync();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(responseJson);
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
        return (body, responseJson);
    }

    private static (HttpListener Listener, HttpClient Client) StartFake()
    {
        var port = FreePort.Next();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return (listener, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") });
    }

    [Fact]
    public async Task Provider_requests_diarization_only_when_the_diarizer_is_loaded()
    {
        var sample = File.ReadAllText(SamplePath("file-verbose-json-diarization.json"));
        var audio = new float[16_000 * 2];

        var (withListener, withClient) = StartFake();
        using (withListener)
        {
            var provider = new NemotronProvider(new FakeEndpoint(withClient, hasDiarizer: true), "nemotron:test");
            var serve = ServeOnceAsync(withListener, sample);
            var segments = await provider.RecognizeWindowAsync(audio, "en", default);
            var (request, _) = await serve;
            Assert.Contains("name=diarization", request);
            Assert.Contains("verbose_json", request);
            Assert.Contains("name=language", request);
            Assert.True(provider.Capabilities.CombinedDiarization);
            Assert.Contains(segments, s => s.SpeakerLabel == "speaker-2");
        }

        var (withoutListener, withoutClient) = StartFake();
        using (withoutListener)
        {
            var provider = new NemotronProvider(new FakeEndpoint(withoutClient, hasDiarizer: false), "nemotron:test");
            var serve = ServeOnceAsync(withoutListener, sample);
            var segments = await provider.RecognizeWindowAsync(audio, null, default);
            var (request, _) = await serve;
            Assert.DoesNotContain("name=diarization", request);
            Assert.False(provider.Capabilities.CombinedDiarization);
            Assert.All(segments, s => Assert.Null(s.SpeakerLabel));
        }
    }

    [Fact]
    public async Task Provider_reports_worker_errors_with_the_workers_message_and_refuses_oversized_windows()
    {
        var (listener, client) = StartFake();
        using (listener)
        {
            var provider = new NemotronProvider(new FakeEndpoint(client, true), "nemotron:test");
            var serve = ServeOnceAsync(listener, """{"error":{"message":"speaker diarization requested but no diarizer model is loaded"}}""", 400);
            var ex = await Assert.ThrowsAsync<NemotronException>(async () => await provider.RecognizeWindowAsync(new float[16_000], null, default));
            await serve;
            Assert.Contains("no diarizer", ex.Message);
        }

        var offline = new NemotronProvider(new FakeEndpoint(new HttpClient(), true), "nemotron:test");
        await Assert.ThrowsAsync<ArgumentException>(async () => await offline.RecognizeWindowAsync(new float[NemotronProvider.MaxWindowSamples + 1], null, default));
    }

    [Fact]
    public void Speaker_labels_from_a_provider_register_speakers_and_keep_renames_on_the_mapping_only()
    {
        var doc = TestData.NewDocument();
        var host = new SessionDocumentHost(doc, new NullStore());
        var recognized = new[]
        {
            new RecognizedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(2), "hi", null, null, "speaker-1", TimingProvenance.Model),
            new RecognizedSegment(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), "hello", null, null, "speaker-2", TimingProvenance.Model)
        };
        var segments = SegmentMapper.Map(recognized, "f0", 0, TimeSpan.FromSeconds(4), 1, 1, SegmentState.Final).ToList();
        host.EnsureSpeakers(segments);
        host.EnsureSpeakers(segments);
        Assert.Equal(["Speaker 1", "Speaker 2"], host.Document.Speakers.Select(s => s.Name));

        host.RenameSpeaker("speaker-2", "Dana");
        Assert.Equal("Dana", host.Document.Speakers.Single(s => s.Id == "speaker-2").Name);
        Assert.Equal("speaker-2", segments[1].Speakers[0].SpeakerId);
    }

    private sealed class NullStore : PrimeDictate.Core.Sessions.ITranscriptionSessionStore
    {
        public ValueTask InitializeAsync(CancellationToken c) => ValueTask.CompletedTask;

        public ValueTask SaveCheckpointAsync(TranscriptDocument d, CancellationToken c) => ValueTask.CompletedTask;

        public ValueTask<TranscriptDocument?> LoadAsync(Guid id, CancellationToken c) => ValueTask.FromResult<TranscriptDocument?>(null);

        public ValueTask<IReadOnlyList<PrimeDictate.Core.Sessions.TranscriptSessionSummary>> ListAsync(int s, int t, CancellationToken c) => ValueTask.FromResult<IReadOnlyList<PrimeDictate.Core.Sessions.TranscriptSessionSummary>>([]);

        public ValueTask<IReadOnlyList<PrimeDictate.Core.Sessions.TranscriptSessionSummary>> MarkInterruptedSessionsAsync(CancellationToken c) => ValueTask.FromResult<IReadOnlyList<PrimeDictate.Core.Sessions.TranscriptSessionSummary>>([]);

        public ValueTask<IReadOnlyList<string>> DeleteOwnedAudioAsync(Guid id, CancellationToken c) => ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask<PrimeDictate.Core.Sessions.SessionDeletionResult> DeleteAsync(Guid id, CancellationToken c) => ValueTask.FromResult(new PrimeDictate.Core.Sessions.SessionDeletionResult(false, [], []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record FakeRealtime(HttpListener Listener, Uri Base, Task Server, List<string> Received);

    /// <summary>A stand-in for the worker's realtime socket using the message shapes captured from the real one.</summary>
    private static FakeRealtime StartRealtime(bool requireDiarizerSetting)
    {
        var port = FreePort.Next();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var received = new List<string>();
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
            lock (received)
            {
                received.Add("auth=" + context.Request.Headers["Authorization"]);
            }

            async Task Send(string json) => await ws.SendAsync(Encoding.UTF8.GetBytes(json), System.Net.WebSockets.WebSocketMessageType.Text, true, default);
            await Send("""{"event_id":"e1","type":"session.created","session":{"sample_rate":16000}}""");
            var buffer = new byte[64 * 1024];
            var commits = 0;
            long audioBytes = 0;
            while (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, default);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Binary)
                {
                    // Like the real worker, partial text streams out while audio is still arriving.
                    if (audioBytes == 0)
                    {
                        await Send("""{"event_id":"e3","type":"conversation.item.input_audio_transcription.delta","delta":"hello","audio_processed":1}""");
                    }

                    audioBytes += result.Count;
                    continue;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                lock (received)
                {
                    received.Add(text);
                }

                if (text.Contains("session.update"))
                {
                    await Send("""{"event_id":"e2","type":"session.updated","session":{"speaker_diarization":true,"word_timestamps":true}}""");
                }
                else if (text.Contains("input_audio_buffer.commit"))
                {
                    commits++;
                    if (commits == 1)
                    {
                        await Send("""
                            {"event_id":"e4","type":"conversation.item.input_audio_transcription.completed","audio_processed":4,"transcript":"hello there general kenobi",
                             "words":[{"word":"hello","start":0.5,"end":0.8,"speaker":1,"confidence":1},{"word":"there","start":0.9,"end":1.2,"speaker":1,"confidence":1},
                                      {"word":"general","start":1.5,"end":1.9,"speaker":2,"confidence":1},{"word":"kenobi","start":1.9,"end":2.4,"speaker":2,"confidence":1}]}
                            """);
                    }
                    else
                    {
                        await Send("""{"event_id":"e5","type":"conversation.item.input_audio_transcription.completed","audio_processed":1,"transcript":"","words":[]}""");
                    }

                    await Send("""{"event_id":"e6","type":"input_audio_buffer.committed"}""");
                }
            }
        });
        return new FakeRealtime(listener, new Uri($"http://127.0.0.1:{port}/"), server, received);
    }

    [Fact]
    public async Task Realtime_session_offsets_word_times_and_reports_speakers_only_on_final_text()
    {
        var fake = StartRealtime(true);
        using var _ = fake.Listener;
        await using var session = await NemotronRealtimeSession.ConnectAsync(fake.Base, "test-key", diarize: true, default);

        // 3 s of audio starting at session sample 48000 (3 s into the recording).
        for (var i = 0; i < 30; i++)
        {
            await session.WriteAsync(AudioFrame.CopyFrom(new float[1600], AudioFormat.SpeechTimeline, i, 48_000 + (i * 1600L)), default);
        }

        await session.CommitAsync(default);
        await session.CompleteAsync(default);
        var updates = new List<StreamingUpdate>();
        await foreach (var u in session.ReadUpdatesAsync(default))
        {
            updates.Add(u);
        }

        var finals = updates.Where(u => u.IsFinal && u.Segment.Text.Length > 0).ToList();
        Assert.Equal(2, finals.Count);
        Assert.Equal("0.0", finals[0].UtteranceId);
        Assert.Equal("0.1", finals[1].UtteranceId);
        Assert.Equal("speaker-1", finals[0].Segment.SpeakerLabel);
        Assert.Equal("speaker-2", finals[1].Segment.SpeakerLabel);
        // Word time 0.5 s after the commit start (3 s) is 3.5 s on the session timeline.
        Assert.Equal(3.5, finals[0].Segment.Start.TotalSeconds, 2);
        Assert.Equal(4.5, finals[1].Segment.Start.TotalSeconds, 2);
        Assert.Equal(3.5, finals[0].Segment.Words![0].Start.TotalSeconds, 2);

        lock (fake.Received)
        {
            Assert.Contains("auth=Bearer test-key", fake.Received);
            Assert.Contains(fake.Received, m => m.Contains("speaker_diarization"));
        }
    }

    [Fact]
    public async Task Realtime_session_does_not_request_speakers_when_diarize_is_off()
    {
        var fake = StartRealtime(false);
        using var _ = fake.Listener;
        await using var session = await NemotronRealtimeSession.ConnectAsync(fake.Base, "k", diarize: false, default);
        await session.CompleteAsync(default);
        lock (fake.Received)
        {
            Assert.DoesNotContain(fake.Received, m => m.Contains("speaker_diarization"));
        }
    }

    [Fact]
    public async Task Live_session_streams_to_the_worker_and_fills_speaker_lanes_from_final_text()
    {
        var fake = StartRealtime(true);
        using var _ = fake.Listener;
        await using var store = PrimeDictate.Core.Storage.SqliteTranscriptionSessionStore.Create(new PrimeDictate.Core.Storage.AppDataPaths(this.root));
        await store.InitializeAsync(default);
        var endpoint = new FakeEndpoint(new HttpClient { BaseAddress = fake.Base }, hasDiarizer: true);
        var provider = new NemotronProvider(endpoint, "nemotron:test", identifySpeakers: true);
        // 2 s of tone then 2.5 s of silence: the pause ends the utterance and triggers a commit.
        var audio = Enumerable.Range(0, 32_000).Select(i => 0.3f * MathF.Sin(i * 0.05f)).Concat(new float[40_000]).ToArray();
        var options = new LiveSessionOptions(
            new PrimeDictate.Core.Sessions.TranscriptionSessionOptions("nemotron:test", null, "cpu", null, null, PrimeDictate.Core.Sessions.AudioRetention.KeepAudio, PrimeDictate.Core.Sessions.DownmixMode.Average, null, null, null),
            "t", store.GetSessionMediaDirectory, TimeSpan.FromSeconds(60));
        // Hold the rest of the audio until the partial text has been seen, so the test does not depend on how fast
        // the fake source, the socket and the reducer run relative to each other.
        var restOfAudio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new LiveTranscriptionSession(new LiveSourceTests.FakeSource(1, audio, holdAfterFrames: 3, release: restOfAudio.Task), provider, new PrimeDictate.Core.Coordination.MicrophoneCoordinator(), new PrimeDictate.Core.Coordination.ModelLeaseScheduler(), store, options);
        await session.StartAsync(default);

        var seen = new List<string>();
        var sawPartial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Host!.Changed += d =>
        {
            lock (seen)
            {
                seen.AddRange(d.ActiveSegments.Select(s => $"{s.State}:{s.DisplayText}"));
                if (seen.Contains("Provisional:hello"))
                {
                    sawPartial.TrySetResult();
                }
            }
        };
        await sawPartial.Task.WaitAsync(TimeSpan.FromSeconds(20));
        restOfAudio.SetResult();
        var finalsSeen = DateTime.UtcNow.AddSeconds(20);
        while (session.Host.Document.ActiveSegments.Count(s => s.State == SegmentState.Final) < 2 && DateTime.UtcNow < finalsSeen)
        {
            await Task.Delay(20);
        }

        await session.StopAsync(default);

        var doc = session.Host.Document;
        Assert.Equal(TranscriptSessionStatus.Completed, doc.Status);
        Assert.Equal(2, doc.Speakers.Count);
        var finals = doc.ActiveSegments.ToList();
        Assert.Equal(2, finals.Count);
        Assert.All(finals, s => Assert.Equal(SegmentState.Final, s.State));
        Assert.Equal(["hello there", "general kenobi"], finals.Select(s => s.RawText));
        Assert.Equal(["speaker-1", "speaker-2"], finals.Select(s => s.Speakers[0].SpeakerId));
        // The provisional "hello" preview was replaced in place by the first final segment.
        Assert.Contains(seen, x => x == "Provisional:hello");
        Assert.DoesNotContain(doc.Segments, s => s.State != SegmentState.Final);
    }

    /// <summary>Counts audio bytes per connection; connection 1 optionally never answers (the wedge in NeMo-Speech.cpp#48).</summary>
    private static (HttpListener Listener, Uri Base, List<long> AudioBytes) StartCountingServer(bool wedgeFirst)
    {
        var port = FreePort.Next();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var bytes = new List<long>();
        _ = Task.Run(async () =>
        {
            var n = 0;
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                var wedged = wedgeFirst && n == 0;
                var slot = n++;
                lock (bytes)
                {
                    bytes.Add(0);
                }

                _ = Task.Run(async () =>
                {
                    var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
                    async Task Send(string json) => await ws.SendAsync(Encoding.UTF8.GetBytes(json), System.Net.WebSockets.WebSocketMessageType.Text, true, default);
                    await Send("""{"type":"session.created"}""");
                    var buffer = new byte[64 * 1024];
                    try
                    {
                        while (ws.State == System.Net.WebSockets.WebSocketState.Open)
                        {
                            var r = await ws.ReceiveAsync(buffer, default);
                            if (r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                            {
                                break;
                            }

                            if (r.MessageType == System.Net.WebSockets.WebSocketMessageType.Binary)
                            {
                                lock (bytes)
                                {
                                    bytes[slot] += r.Count;
                                }

                                if (!wedged && bytes[slot] == r.Count)
                                {
                                    await Send("""{"type":"conversation.item.input_audio_transcription.delta","delta":"hi","audio_processed":1}""");
                                }

                                continue;
                            }

                            var text = Encoding.UTF8.GetString(buffer, 0, r.Count);
                            if (text.Contains("session.update"))
                            {
                                await Send("""{"type":"session.updated"}""");
                            }
                            else if (text.Contains("commit") && !wedged)
                            {
                                await Send("""{"type":"conversation.item.input_audio_transcription.completed","audio_processed":1,"transcript":"hi there","words":[{"word":"hi","start":0.1,"end":0.3,"speaker":1},{"word":"there","start":0.4,"end":0.6,"speaker":1}]}""");
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                });
            }
        });
        return (listener, new Uri($"http://127.0.0.1:{port}/"), bytes);
    }

    private static AudioFrame Frame(float[] samples, long offset, long seq, bool synthetic = false) =>
        AudioFrame.CopyFrom(samples, new AudioFormat(16_000, 1, AudioSampleFormat.Float32), seq, offset, synthetic);

    [Fact]
    public async Task Realtime_session_does_not_send_long_runs_of_zero_audio_but_keeps_offsets()
    {
        var (listener, uri, bytes) = StartCountingServer(false);
        using var _ = listener;
        await using var session = await NemotronRealtimeSession.ConnectAsync(uri, "k", diarize: true, default);
        var tone = Enumerable.Range(0, 1600).Select(i => 0.3f * MathF.Sin(i * 0.05f)).ToArray();

        long seq = 0, offset = 0;
        for (var i = 0; i < 10; i++)
        {
            await session.WriteAsync(Frame(tone, offset, seq++), default);
            offset += 1600;
        }

        // 60 s of exact zeros, some flagged and some not: only the first half second may reach the worker.
        for (var i = 0; i < 600; i++)
        {
            await session.WriteAsync(Frame(new float[1600], offset, seq++, synthetic: i % 2 == 0), default);
            offset += 1600;
        }

        var resumeAt = offset;
        for (var i = 0; i < 10; i++)
        {
            await session.WriteAsync(Frame(tone, offset, seq++), default);
            offset += 1600;
        }

        await session.CommitAsync(default);
        await Task.Delay(300);
        long sent;
        lock (bytes)
        {
            sent = bytes[0];
        }

        // 20 tone blocks + at most 0.5 s of zeros + the commit flush; certainly far below the 60 s of zeros.
        Assert.True(sent <= (20 * 1600 + 8_000 + 1600) * 2, $"sent {sent} bytes");
        Assert.True(sent >= 20 * 1600 * 2);
        // The post-gap speech carries its true timeline position (the first commit covers speech from sample 0).
        var updates = new List<StreamingUpdate>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (var u in session.ReadUpdatesAsync(cts.Token))
        {
            updates.Add(u);
            if (u.IsFinal)
            {
                break;
            }
        }

        Assert.Contains(updates, u => u.IsFinal && u.Segment.Text.Contains("hi"));
        Assert.True(resumeAt > 16_000 * 60);
    }

    [Fact]
    public async Task Realtime_session_restarts_a_stalled_connection_and_keeps_speaker_numbers_apart()
    {
        var (listener, uri, bytes) = StartCountingServer(true);
        using var _ = listener;
        await using var session = await NemotronRealtimeSession.ConnectAsync(uri, "k", diarize: true, default, TimeSpan.FromSeconds(1));
        var notices = new List<string>();
        session.Notice += notices.Add;
        var tone = Enumerable.Range(0, 1600).Select(i => 0.3f * MathF.Sin(i * 0.05f)).ToArray();

        long seq = 0, offset = 0;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (session.Restarts == 0 && DateTime.UtcNow < deadline)
        {
            await session.WriteAsync(Frame(tone, offset, seq++), default);
            offset += 1600;
            await Task.Delay(50);
        }

        Assert.Equal(1, session.Restarts);
        Assert.Single(notices);

        // The new connection works: speech gets text and the final's speaker is kept apart from earlier numbering.
        for (var i = 0; i < 5; i++)
        {
            await session.WriteAsync(Frame(tone, offset, seq++), default);
            offset += 1600;
        }

        await session.CommitAsync(default);
        var finals = new List<StreamingUpdate>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var u in session.ReadUpdatesAsync(cts.Token))
        {
            if (u.IsFinal && u.Segment.Text.Length > 0)
            {
                finals.Add(u);
                break;
            }
        }

        Assert.Single(finals);
        lock (bytes)
        {
            Assert.True(bytes.Count >= 2 && bytes[1] > 0, "audio should flow on the replacement connection");
        }
    }

    private static string? FakeWorker(string dir, bool cudaFails)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/python3"))
        {
            return null;
        }

        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "nemo-speech");
        File.WriteAllText(path, $$"""
            #!/bin/sh
            dev=cpu; port=0
            while [ $# -gt 0 ]; do case "$1" in --device) dev="$2";; --port) port="$2";; esac; shift; done
            case "$dev" in
              cuda*) {{(cudaFails ? "echo 'CUDA error' >&2; exit 5" : "echo '[asr] loaded backend=CUDA0'")}};;
              *) echo '[asr] loaded backend=CPU';;
            esac
            exec /usr/bin/python3 -c "
            import http.server
            class H(http.server.BaseHTTPRequestHandler):
                def do_GET(self):
                    self.send_response(200); self.end_headers(); self.wfile.write(b'{}')
                def log_message(self, *a): pass
            http.server.HTTPServer(('127.0.0.1', $port), H).serve_forever()
            "
            """.Replace("\r", string.Empty));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public void Worker_arguments_carry_the_device_and_refuse_vulkan()
    {
        var files = new NemotronModelFiles("/m/asr.gguf", null);
        var args = NemotronWorker.BuildArguments(1, files, "cuda:0");
        Assert.Equal("cuda:0", args[args.ToList().IndexOf("--device") + 1]);
        Assert.Throws<ArgumentException>(() => NemotronWorker.BuildArguments(1, files, "vulkan:0"));
        Assert.Equal("cuda:0", NemotronWorker.NormalizeBackend("CUDA0"));
        Assert.Equal("cpu", NemotronWorker.NormalizeBackend("CPU"));
        Assert.Equal("unknown", NemotronWorker.NormalizeBackend(null));
    }

    [Fact]
    public async Task Worker_reports_the_backend_it_actually_used_and_falls_back_visibly()
    {
        var root = Path.Combine(Path.GetTempPath(), "pd-nemo-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cpu = FakeWorker(Path.Combine(root, "cpu"), cudaFails: false);
            if (cpu is null)
            {
                return; // needs a POSIX shell and python3 to stand in for the worker
            }

            var goodCuda = FakeWorker(Path.Combine(root, "cuda-ok"), cudaFails: false)!;
            var badCuda = FakeWorker(Path.Combine(root, "cuda-bad"), cudaFails: true)!;
            var files = new NemotronModelFiles("/m/asr.gguf", null);
            var notices = new List<string>();

            await using (var gpu = await NemotronWorker.StartPreferredAsync(cpu, goodCuda, files, "auto", _ => [], notices.Add, default, TimeSpan.FromSeconds(20)))
            {
                Assert.Equal("cuda:0", gpu.EffectiveBackend);
                Assert.Null(gpu.FallbackReason);
            }

            await using (var fell = await NemotronWorker.StartPreferredAsync(cpu, badCuda, files, "cuda:0", _ => [], notices.Add, default, TimeSpan.FromSeconds(20)))
            {
                Assert.Equal("cpu", fell.EffectiveBackend);
                Assert.Equal("cuda:0", fell.RequestedBackend);
                Assert.Contains("could not start on the GPU", fell.FallbackReason);
            }

            await using (var forced = await NemotronWorker.StartPreferredAsync(cpu, goodCuda, files, "cpu", _ => [], notices.Add, default, TimeSpan.FromSeconds(20)))
            {
                Assert.Equal("cpu", forced.EffectiveBackend);
                Assert.Null(forced.FallbackReason);
            }

            Assert.Contains(notices, n => n.Contains("running on cuda:0"));
            Assert.Contains(notices, n => n.Contains("using the CPU instead"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
