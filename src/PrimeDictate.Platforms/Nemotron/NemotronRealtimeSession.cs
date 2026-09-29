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
public sealed class NemotronRealtimeSession : IStreamingRecognitionSession
{
    private const int BlockSamples = 1600;

    private readonly ClientWebSocket socket;
    private readonly bool diarize;
    private readonly Channel<StreamingUpdate> updates = Channel.CreateUnbounded<StreamingUpdate>();
    private readonly Queue<double> pendingCommitStarts = new();
    private readonly object sync = new();
    private readonly List<float> block = new(BlockSamples * 2);
    private readonly CancellationTokenSource cancel = new();
    private Task? receiver;
    private long? utteranceStartSample;
    private long endSample;
    private int utteranceIndex;
    private int completedCount;
    private string partial = string.Empty;
    private TaskCompletionSource allCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private NemotronRealtimeSession(ClientWebSocket socket, bool diarize)
    {
        this.socket = socket;
        this.diarize = diarize;
    }

    public static async ValueTask<NemotronRealtimeSession> ConnectAsync(Uri baseAddress, string apiKey, bool diarize, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        var uri = new UriBuilder(baseAddress) { Scheme = "ws", Path = "/v1/audio/transcriptions/realtime" }.Uri;
        var session = new NemotronRealtimeSession(socket, diarize);
        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            await session.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            socket.Dispose();
            throw new NemotronException("worker-http", "Could not open the live connection to the Nemotron worker.", ex);
        }

        session.receiver = Task.Run(session.ReceiveLoopAsync, CancellationToken.None);
        return session;
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        var created = await this.ReceiveTextAsync(cancellationToken).ConfigureAwait(false);
        if (created is null || TypeOf(created) != "session.created")
        {
            throw new NemotronException("worker-http", "The worker did not start a realtime session.");
        }

        var settings = new Dictionary<string, object> { ["sample_rate"] = 16_000, ["word_timestamps"] = true };
        if (this.diarize)
        {
            settings["speaker_diarization"] = true;
        }

        var update = JsonSerializer.Serialize(new Dictionary<string, object> { ["type"] = "session.update", ["session"] = settings });
        await this.SendTextAsync(update, cancellationToken).ConfigureAwait(false);
        var reply = await this.ReceiveTextAsync(cancellationToken).ConfigureAwait(false);
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

        this.utteranceStartSample ??= frame.SampleOffset;
        this.endSample = frame.EndSampleOffset;
        var samples = frame.Samples.ToArray();
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
            this.pendingCommitStarts.Enqueue(start / 16_000d);
            this.allCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this.partial = string.Empty;
        }

        this.utteranceStartSample = null;
        await this.SendTextAsync("""{"type":"input_audio_buffer.commit"}""", cancellationToken).ConfigureAwait(false);
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
        await this.cancel.CancelAsync().ConfigureAwait(false);
        this.updates.Writer.TryComplete();
        this.socket.Dispose();
        if (this.receiver is not null)
        {
            try
            {
                await this.receiver.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        this.cancel.Dispose();
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
        await this.socket.SendAsync(bytes, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
    }

    private Task SendTextAsync(string text, CancellationToken cancellationToken) =>
        this.socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);

    private async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var result = await this.socket.ReceiveAsync(chunk, cancellationToken).ConfigureAwait(false);
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

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (this.socket.State == WebSocketState.Open)
            {
                var text = await this.ReceiveTextAsync(this.cancel.Token).ConfigureAwait(false);
                if (text is null)
                {
                    break;
                }

                this.Handle(text);
            }

            this.updates.Writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            this.updates.Writer.TryComplete();
        }
        catch (WebSocketException ex)
        {
            this.updates.Writer.TryComplete(new NemotronException("worker-http", "The live connection to the Nemotron worker was lost.", ex));
        }
    }

    private void Handle(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        switch (TypeOf(json))
        {
            case "conversation.item.input_audio_transcription.delta":
                this.HandleDelta(root);
                break;
            case "conversation.item.input_audio_transcription.completed":
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
            offset = this.pendingCommitStarts.Count > 0 ? this.pendingCommitStarts.Dequeue() : 0;
            index = this.completedCount++;
            this.utteranceIndex = this.completedCount;
            this.partial = string.Empty;
        }

        var processed = root.TryGetProperty("audio_processed", out var p) && p.TryGetDouble(out var s) ? s : 0;
        var transcript = root.TryGetProperty("transcript", out var t) ? t.GetString() : null;
        var window = TimeSpan.FromSeconds(processed);
        var segments = NemotronResponseParser.ParseVerboseJson(root, window, this.diarize, transcript);
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
            if (this.pendingCommitStarts.Count == 0)
            {
                this.allCompleted.TrySetResult();
            }
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
