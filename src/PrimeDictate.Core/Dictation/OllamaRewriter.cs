using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrimeDictate.Core.Dictation;

/// <summary>Rewrite styles, numbered as in the WPF settings file.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OllamaMode>))]
public enum OllamaMode
{
    Default = 0,
    Prompt = 1,
    Bug = 2,
    Update = 3,
    Communication = 4,
    Blog = 5,
    VibeCoding = 6
}

public sealed record RewriteResult(string Text, string? SystemPrompt, bool Rewritten);

/// <summary>Optional last step before typing: a local model rewrites the transcript. Any failure keeps the raw text.</summary>
public interface ITranscriptRewriter
{
    ValueTask<RewriteResult> RewriteAsync(string transcript, IForegroundTarget? target, CancellationToken cancellationToken);
}

public sealed record OllamaOptions(bool Enabled, string Endpoint, string Model, OllamaMode Mode, bool AllowRemoteEndpoint = false)
{
    public static OllamaOptions Disabled { get; } = new(false, "http://localhost:11434", "gemma:2b", OllamaMode.Default);
}

/// <summary>
/// Sends the transcript to an Ollama server. Dictated speech is private, so only a loopback endpoint is used unless the
/// user explicitly allows a remote one; otherwise the transcript is typed as spoken and the reason is reported.
/// </summary>
public sealed class OllamaRewriter(Func<OllamaOptions> options, HttpClient? http = null, Action<string>? report = null) : ITranscriptRewriter
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly HttpClient client = http ?? Shared;

    public async ValueTask<RewriteResult> RewriteAsync(string transcript, IForegroundTarget? target, CancellationToken cancellationToken)
    {
        var o = options();
        if (!o.Enabled || string.IsNullOrWhiteSpace(transcript))
        {
            return new RewriteResult(transcript, null, false);
        }

        var prompt = BuildSystemPrompt(o.Mode, target?.AppName, target?.WindowTitle);
        if (!TryGetChatUri(o, out var uri, out var problem))
        {
            report?.Invoke(problem);
            return new RewriteResult(transcript, prompt, false);
        }

        try
        {
            var body = new
            {
                model = o.Model,
                messages = new[] { new { role = "system", content = prompt }, new { role = "user", content = transcript } },
                stream = false
            };
            using var response = await this.client.PostAsJsonAsync(uri, body, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                report?.Invoke($"Ollama returned {(int)response.StatusCode}; typed your words as spoken.");
                return new RewriteResult(transcript, prompt, false);
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (json.RootElement.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.GetString()?.Trim() is { Length: > 0 } text)
            {
                return new RewriteResult(text, prompt, true);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            report?.Invoke($"Ollama could not be reached ({ex.Message}); typed your words as spoken.");
        }

        return new RewriteResult(transcript, prompt, false);
    }

    internal static bool TryGetChatUri(OllamaOptions o, out Uri uri, out string problem)
    {
        uri = null!;
        problem = string.Empty;
        if (!Uri.TryCreate(o.Endpoint.Trim().TrimEnd('/') + "/api/chat", UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https"))
        {
            problem = "The Ollama endpoint is not a valid http(s) address; typed your words as spoken.";
            return false;
        }

        var loopback = parsed.IsLoopback || (IPAddress.TryParse(parsed.Host, out var ip) && IPAddress.IsLoopback(ip));
        if (!loopback && !o.AllowRemoteEndpoint)
        {
            problem = $"Ollama at {parsed.Host} is not on this computer, so your speech was not sent there. Allow remote endpoints in settings to use it.";
            return false;
        }

        uri = parsed;
        return true;
    }

    public static string BuildSystemPrompt(OllamaMode mode, string? processName, string? windowTitle)
    {
        var basePrompt = mode switch
        {
            OllamaMode.Prompt => "You are an AI prompt engineer. Rewrite the user's dictated text into a clear, structured prompt for a large language model. Output ONLY the finalized prompt.",
            OllamaMode.Bug => "You are a QA engineer. Rewrite the user's dictated text into a formal bug report with steps to reproduce, expected behavior, and actual behavior. Output ONLY the bug report.",
            OllamaMode.Update => "You are a project manager. Rewrite the user's dictated text into a clear, concise status update or changelog entry. Output ONLY the finalized update text.",
            OllamaMode.Communication => "You are a professional assistant. Rewrite the user's dictated text into a professional message or email. Fix any errors and ensure a polite, clear tone. Output ONLY the message.",
            OllamaMode.Blog => "You are a content writer. Rewrite the user's dictated text into a well-written, engaging draft for a blog post. Output ONLY the blog post text.",
            OllamaMode.VibeCoding => "You are a senior software engineer. Rewrite the user's dictated text into clear, actionable, and precise instructions for an AI coding assistant. Output ONLY the instructions.",
            _ => "You are a dictation assistant. Fix any obvious grammatical, spelling, or transcription errors in the user's dictated text. Do not add conversational filler. Output ONLY the corrected text."
        };

        if (string.IsNullOrWhiteSpace(processName) && string.IsNullOrWhiteSpace(windowTitle))
        {
            return basePrompt;
        }

        var app = string.IsNullOrWhiteSpace(processName) ? "Unknown" : processName;
        var title = string.IsNullOrWhiteSpace(windowTitle) ? "Unknown" : windowTitle;
        return basePrompt + $" Context: The user is currently dictating into an application process named '{app}' with the window title '{title}'. Use this context to inform your formatting and tone, but do not mention the application name or window title in your output.";
    }
}
