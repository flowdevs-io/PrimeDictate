using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Platforms.Nemotron;

/// <summary>
/// One realtime socket to the worker. Audio goes out as binary PCM16 in blocks of about 100 ms.
/// The worker only reports final text (with word times and speaker numbers) after a commit, and word
/// times restart at zero after each commit, so this class adds the session-timeline offset of the
/// audio each commit covered. Deltas are display-only: they can be a suffix or a whole revised partial,
/// so they are reconciled by prefix and always replaced by the final text.
/// </summary>
public sealed class NemotronRealtimeSession : IStreamingRecognitionSession, IStreamingNotices
{
    private const int BlockSamples = 1600;

    private static readonly System.Text.RegularExpressions.Regex LanguageTag = new(@"^[a-z]{2,3}(-[A-Za-z]{2,4})?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Exact-zero (or flagged synthetic) audio longer than this is not sent. NeMo-Speech.cpp issue #48: a long
    /// run of zero PCM with endpointing on wedges the stream. The audio stays in the saved recording; only the wire skips it.
    /// </summary>
    private const int MaxSilentRunSamples = 8_000;

    private readonly Uri baseAddress;
    private readonly string apiKey;
    private readonly TimeSpan stallTimeout;
    private readonly SemaphoreSlim ioGate = new(1, 1);
    private readonly bool diarize;
    private ClientWebSocket socket;
    private readonly Channel<StreamingUpdate> updates = Channel.CreateUnbounded<StreamingUpdate>();
    private readonly Queue<double> pendingCommitStarts = new();
    private readonly object sync = new();
    private readonly List<float> block = new(BlockSamples * 2);
    private CancellationTokenSource cancel = new();
    private readonly CancellationTokenSource lifetime = new();
    private Task? receiver;
    private Task? watchdog;
    private long silentRun;
    private double voicedSinceOutput;
    private long voicedSinceTicks;
    private long oldestPendingTicks;
    private int speakerBase;
    private int maxSpeaker;
    private long? utteranceStartSample;
    private long endSample;
    private int utteranceIndex;
    private int completedCount;
    private string partial = string.Empty;
    private TaskCompletionSource allCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly string? language;

    private NemotronRealtimeSession(Uri baseAddress, string apiKey, bool diarize, TimeSpan stallTimeout, string? language)
    {
        this.language = language;
        this.baseAddress = baseAddress;
        this.apiKey = apiKey;
        this.diarize = diarize;
        this.stallTimeout = stallTimeout;
        this.socket = new ClientWebSocket();
    }

    /// <summary>Set when the worker did not accept the language setting and the stream runs with auto-detection.</summary>
    public string? StartupNotice { get; private set; }

    /// <summary>Raised when the session recovered from a stalled worker connection. Speaker numbers may restart.</summary>
    public event Action<string>? Notice;

    /// <summary>How many times a wedged connection was replaced.</summary>
    public int Restarts { get; private set; }

    /// <param name="stallTimeout">
    /// How long speech may go in without any text coming back before the connection is replaced. Default 6 s.
    /// </param>
    public static async ValueTask<NemotronRealtimeSession> ConnectAsync(Uri baseAddress, string apiKey, bool diarize, CancellationToken cancellationToken, TimeSpan? stallTimeout = null, string? language = null)
    {
        var wanted = string.IsNullOrWhiteSpace(language) || language == "auto" ? null : language;
        // The worker accepts any string (even "xx-YY") and decodes as it likes, so the app checks the value itself.
        string? invalidNote = null;
        if (wanted is not null && !LanguageTag.IsMatch(wanted))
        {
            invalidNote = $"'{wanted}' is not a language tag like en-US, so the language is being auto-detected.";
            wanted = null;
        }
        var session = new NemotronRealtimeSession(baseAddress, apiKey, diarize, stallTimeout ?? TimeSpan.FromSeconds(6), wanted) { StartupNotice = invalidNote };
        try
        {
            await session.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NemotronException) when (wanted is not null)
        {
            session.socket.Dispose();
            // Some worker builds may not know the language setting. Never lose the recording over it: retry
            // without it, and say so.
            session = new NemotronRealtimeSession(baseAddress, apiKey, diarize, stallTimeout ?? TimeSpan.FromSeconds(6), null)
            {
                StartupNotice = $"The speech worker did not accept the language '{wanted}', so it is detecting the language itself. Wrong-language words may appear."
            };
            await session.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        session.watchdog = Task.Run(() => session.WatchdogAsync(session.lifetime.Token), CancellationToken.None);
        return session;
    }

    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        var socket = this.socket;
        socket.Options.SetRequestHeader("Authorization", "Bearer " + this.apiKey);
        var uri = new UriBuilder(this.baseAddress) { Scheme = "ws", Path = "/v1/audio/transcriptions/realtime" }.Uri;
        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            await this.HandshakeAsync(socket, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            socket.Dispose();
            throw new NemotronException("worker-http", "Could not open the live connection to the Nemotron worker.", ex);
        }

        var token = this.cancel.Token;
        this.receiver = Task.Run(() => this.ReceiveLoopAsync(socket, token), CancellationToken.None);
    }

    private async Task HandshakeAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var created = await this.ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
        if (created is null || TypeOf(created) != "session.created")
        {
            throw new NemotronException("worker-http", "The worker did not start a realtime session.");
        }

        var settings = new Dictionary<string, object> { ["sample_rate"] = 16_000, ["word_timestamps"] = true };
        if (this.language is not null)
        {
            settings["language"] = this.language;
        }

        if (this.diarize)
        {
            settings["speaker_diarization"] = true;
        }

        var update = JsonSerializer.Serialize(new Dictionary<string, object> { ["type"] = "session.update", ["session"] = settings });
        await this.SendTextAsync(socket, update, cancellationToken).ConfigureAwait(false);
        var reply = await this.ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
        if (reply is null || TypeOf(reply) != "session.updated")
        {
            throw new NemotronException("worker-http", $"The worker rejected the session settings: {ErrorText(reply)}");
        }
    }

    public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Format.SampleRate != 16_000 || frame.Format.Channels != 1)
        {
            throw new ArgumentException("Nemotron streaming needs 16 kHz mono frames.", nameof(frame));
        }

        this.endSample = frame.EndSampleOffset;
        var samples = frame.Samples.ToArray();
        if (frame.IsSyntheticSilence || IsAllZero(samples))
        {
            this.silentRun += samples.Length;
            if (this.silentRun > MaxSilentRunSamples)
            {
                return;
            }
        }
        else
        {
            this.silentRun = 0;
            this.NoteVoiced(samples);
        }

        this.utteranceStartSample ??= frame.SampleOffset;
        for (var i = 0; i < samples.Length; i++)
        {
            this.block.Add(samples[i]);
            if (this.block.Count >= BlockSamples)
            {
                await this.FlushBlockAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        await this.FlushBlockAsync(cancellationToken).ConfigureAwait(false);
        if (this.utteranceStartSample is not { } start)
        {
            return;
        }

        lock (this.sync)
        {
            if (this.pendingCommitStarts.Count == 0)
            {
                this.oldestPendingTicks = Environment.TickCount64;
            }

            this.pendingCommitStarts.Enqueue(start / 16_000d);
            this.allCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this.partial = string.Empty;
        }

        this.utteranceStartSample = null;
        await this.SendGatedTextAsync("""{"type":"input_audio_buffer.commit"}""", cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        await this.CommitAsync(cancellationToken).ConfigureAwait(false);
        Task wait;
        lock (this.sync)
        {
            wait = this.pendingCommitStarts.Count == 0 ? Task.CompletedTask : this.allCompleted.Task;
        }

        // The last commit's text comes back after a moment of inference; do not wait forever if the worker died.
        await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken)).ConfigureAwait(false);
        this.updates.Writer.TryComplete();
        await this.lifetime.CancelAsync().ConfigureAwait(false);
        if (this.socket.State == WebSocketState.Open)
        {
            try
            {
                // Output only: the receive loop owns reads, and a full close handshake would race it.
                await this.socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
            }
        }
    }

    public IAsyncEnumerable<StreamingUpdate> ReadUpdatesAsync(CancellationToken cancellationToken) =>
        this.updates.Reader.ReadAllAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await this.lifetime.CancelAsync().ConfigureAwait(false);
        await this.cancel.CancelAsync().ConfigureAwait(false);
        this.updates.Writer.TryComplete();
        this.socket.Dispose();
        foreach (var task in new[] { this.receiver, this.watchdog })
        {
            if (task is not null)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        this.cancel.Dispose();
        this.lifetime.Dispose();
    }

    private async Task FlushBlockAsync(CancellationToken cancellationToken)
    {
        if (this.block.Count == 0)
        {
            return;
        }

        var bytes = new byte[this.block.Count * 2];
        AudioConversion.FloatToPcm16(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(this.block), bytes);
        this.block.Clear();
        await this.ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.socket.SendAsync(bytes, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException) when (this.Restarting)
        {
            // The connection is being replaced; this block belonged to the stalled stream.
        }
        finally
        {
            this.ioGate.Release();
        }
    }

    private bool Restarting => this.restarting;

    private volatile bool restarting;

    private async Task SendGatedTextAsync(string text, CancellationToken cancellationToken)
    {
        await this.ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.SendTextAsync(this.socket, text, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException) when (this.Restarting)
        {
        }
        finally
        {
            this.ioGate.Release();
        }
    }

    private Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);

    private static bool IsAllZero(float[] samples)
    {
        foreach (var s in samples)
        {
            if (s != 0f)
            {
                return false;
            }
        }

        return true;
    }

    private void NoteVoiced(float[] samples)
    {
        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        if (Math.Sqrt(sum / Math.Max(1, samples.Length)) < 0.01)
        {
            return;
        }

        lock (this.sync)
        {
            if (this.voicedSinceOutput == 0)
            {
                this.voicedSinceTicks = Environment.TickCount64;
            }

            this.voicedSinceOutput += samples.Length / 16_000d;
        }
    }

    /// <summary>Any text from the worker proves the stream is alive.</summary>
    private void NoteOutput()
    {
        lock (this.sync)
        {
            this.voicedSinceOutput = 0;
        }
    }

    private bool IsStalled()
    {
        var now = Environment.TickCount64;
        lock (this.sync)
        {
            var speechStalled = this.voicedSinceOutput >= 2 && now - this.voicedSinceTicks >= this.stallTimeout.TotalMilliseconds;
            var commitStalled = this.pendingCommitStarts.Count > 0 && now - this.oldestPendingTicks >= this.stallTimeout.TotalMilliseconds * 2;
            return speechStalled || commitStalled;
        }
    }

    private async Task WatchdogAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                if (this.IsStalled())
                {
                    await this.RestartAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Replaces a connection that stopped answering. Text the old stream never returned is dropped (its provisional
    /// lines are cleared), the timeline offsets are untouched because they come from sample positions, and speaker
    /// numbers continue above the highest one seen so a restarted worker cannot merge two different people.
    /// </summary>
    private async Task RestartAsync(CancellationToken cancellationToken)
    {
        await this.ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        this.restarting = true;
        try
        {
            var old = this.socket;
            await this.cancel.CancelAsync().ConfigureAwait(false);
            old.Dispose();
            if (this.receiver is not null)
            {
                await this.receiver.ConfigureAwait(false);
            }

            this.cancel.Dispose();
            this.cancel = new CancellationTokenSource();
            lock (this.sync)
            {
                while (this.pendingCommitStarts.Count > 0)
                {
                    var offset = this.pendingCommitStarts.Dequeue();
                    this.EmitEmptyFinal(this.completedCount++, offset);
                }

                if (this.utteranceStartSample is { } open)
                {
                    this.EmitEmptyFinal(this.utteranceIndex, open / 16_000d);
                }

                this.completedCount = Math.Max(this.completedCount, this.utteranceIndex + 1);
                this.utteranceIndex = this.completedCount;
                this.utteranceStartSample = null;
                this.partial = string.Empty;
                this.voicedSinceOutput = 0;
                this.allCompleted.TrySetResult();
                this.speakerBase = this.maxSpeaker;
            }

            this.block.Clear();
            this.silentRun = 0;
            this.socket = new ClientWebSocket();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await this.OpenAsync(timeout.Token).ConfigureAwait(false);
            this.Restarts++;
            this.Notice?.Invoke("Speech recognition stopped answering and was restarted. Audio in the last few seconds was not transcribed, and speaker numbers may restart.");
        }
        catch (Exception ex) when (ex is NemotronException or OperationCanceledException or WebSocketException)
        {
            this.updates.Writer.TryComplete(ex as NemotronException ?? new NemotronException("worker-http", "Could not reconnect to the Nemotron worker after it stopped answering.", ex));
        }
        finally
        {
            this.restarting = false;
            this.ioGate.Release();
        }
    }

    private void EmitEmptyFinal(int index, double offset) =>
        this.updates.Writer.TryWrite(new StreamingUpdate($"{index}.0", new RecognizedSegment(TimeSpan.FromSeconds(offset), TimeSpan.FromSeconds(offset), string.Empty, null, null, null, TimingProvenance.ApproximateChunk), true));

    private async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            buffer.Write(chunk.AsSpan(0, result.Count));
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(buffer.WrittenSpan);
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var text = await this.ReceiveTextAsync(socket, token).ConfigureAwait(false);
                if (text is null)
                {
                    break;
                }

                this.Handle(text);
            }

            this.CompleteIfCurrent(socket, null);
        }
        catch (OperationCanceledException)
        {
            this.CompleteIfCurrent(socket, null);
        }
        catch (WebSocketException ex)
        {
            this.CompleteIfCurrent(socket, new NemotronException("worker-http", "The live connection to the Nemotron worker was lost.", ex));
        }
    }

    /// <summary>A replaced connection ending is expected and must not end the update stream.</summary>
    private void CompleteIfCurrent(ClientWebSocket socket, Exception? error)
    {
        if (!this.restarting && ReferenceEquals(socket, this.socket))
        {
            this.updates.Writer.TryComplete(error);
        }
    }

    private void Handle(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        switch (TypeOf(json))
        {
            case "conversation.item.input_audio_transcription.delta":
                this.NoteOutput();
                this.HandleDelta(root);
                break;
            case "conversation.item.input_audio_transcription.completed":
                this.NoteOutput();
                this.HandleCompleted(root);
                break;
            case "error":
                this.updates.Writer.TryComplete(new NemotronException("worker-http", ErrorText(json)));
                break;
        }
    }

    private void HandleDelta(JsonElement root)
    {
        var delta = root.TryGetProperty("delta", out var d) ? d.GetString() : null;
        double start;
        int index;
        lock (this.sync)
        {
            // Deltas that arrive while a commit is being finalized belong to audio already committed; skip them.
            if (string.IsNullOrEmpty(delta) || this.pendingCommitStarts.Count > 0 || this.utteranceStartSample is null)
            {
                return;
            }

            // A delta is either the next piece or the whole revised partial; the client cannot tell which.
            this.partial = delta.StartsWith(this.partial, StringComparison.Ordinal) && this.partial.Length > 0
                ? delta
                : this.partial + delta;
            start = this.utteranceStartSample.Value / 16_000d;
            index = this.utteranceIndex;
        }

        var processed = root.TryGetProperty("audio_processed", out var p) && p.TryGetDouble(out var seconds) ? seconds : 0;
        var text = this.partial.Trim();
        var end = start + Math.Max(processed, 0.1);
        this.updates.Writer.TryWrite(new StreamingUpdate(
            $"{index}.0",
            new RecognizedSegment(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text, null, null, null, TimingProvenance.ApproximateChunk),
            IsFinal: false));
    }

    private void HandleCompleted(JsonElement root)
    {
        double offset;
        int index;
        lock (this.sync)
        {
            // Only peek: the commit stays pending until its finals are written, otherwise CompleteAsync could see
            // "nothing pending" and close the update stream before this method has emitted them.
            offset = this.pendingCommitStarts.Count > 0 ? this.pendingCommitStarts.Peek() : 0;
            index = this.completedCount++;
            this.utteranceIndex = this.completedCount;
            this.partial = string.Empty;
        }

        var processed = root.TryGetProperty("audio_processed", out var p) && p.TryGetDouble(out var s) ? s : 0;
        var transcript = root.TryGetProperty("transcript", out var t) ? t.GetString() : null;
        var window = TimeSpan.FromSeconds(processed);
        var segments = this.ShiftSpeakers(NemotronResponseParser.ParseVerboseJson(root, window, this.diarize, transcript));
        if (segments.Count == 0)
        {
            // An empty final tells the session to drop the utterance's provisional line.
            this.updates.Writer.TryWrite(new StreamingUpdate($"{index}.0", new RecognizedSegment(TimeSpan.FromSeconds(offset), TimeSpan.FromSeconds(offset), string.Empty, null, null, null, TimingProvenance.ApproximateChunk), true));
        }

        var shift = TimeSpan.FromSeconds(offset);
        for (var k = 0; k < segments.Count; k++)
        {
            var seg = segments[k];
            this.updates.Writer.TryWrite(new StreamingUpdate(
                $"{index}.{k}",
                seg with
                {
                    Start = seg.Start + shift,
                    End = seg.End + shift,
                    Words = seg.Words?.Select(word => word with { Start = word.Start + shift, End = word.End + shift }).ToList()
                },
                IsFinal: true));
        }

        lock (this.sync)
        {
            if (this.pendingCommitStarts.Count > 0)
            {
                this.pendingCommitStarts.Dequeue();
            }

            if (this.pendingCommitStarts.Count == 0)
            {
                this.allCompleted.TrySetResult();
            }
        }
    }

    /// <summary>After a restart the worker numbers speakers from 1 again; keep them apart from earlier ones.</summary>
    private IReadOnlyList<RecognizedSegment> ShiftSpeakers(IReadOnlyList<RecognizedSegment> segments)
    {
        lock (this.sync)
        {
            var result = new List<RecognizedSegment>(segments.Count);
            foreach (var seg in segments)
            {
                if (seg.SpeakerLabel is { } label && label.StartsWith("speaker-", StringComparison.Ordinal) && int.TryParse(label.AsSpan(8), out var n))
                {
                    var shifted = n + this.speakerBase;
                    this.maxSpeaker = Math.Max(this.maxSpeaker, shifted);
                    result.Add(this.speakerBase == 0 ? seg : seg with { SpeakerLabel = $"speaker-{shifted}" });
                }
                else
                {
                    result.Add(seg);
                }
            }

            return result;
        }
    }

    private static string? TypeOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ErrorText(string? json)
    {
        if (json is null)
        {
            return "the connection closed";
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() ?? "unknown error" : "unexpected reply";
        }
        catch (JsonException)
        {
            return "unreadable reply";
        }
    }
}
