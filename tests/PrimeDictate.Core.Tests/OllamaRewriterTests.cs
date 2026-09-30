using System.Net;
using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Core.Tests;

public sealed class OllamaRewriterTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;

        public string? LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.Calls);
            this.LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private static HttpResponseMessage Ok(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent($"{{\"message\":{{\"content\":\"{content}\"}}}}") };

    private static OllamaOptions On(string endpoint = "http://localhost:11434", bool remote = false) =>
        new(true, endpoint, "gemma:2b", OllamaMode.Bug, remote);

    [Fact]
    public async Task Rewrites_through_the_local_chat_endpoint_with_target_context()
    {
        var handler = new Handler(_ => Ok("Fixed text."));
        var rewriter = new OllamaRewriter(() => On(), new HttpClient(handler));
        var r = await rewriter.RewriteAsync("fixed tekst", null, default);
        Assert.True(r.Rewritten);
        Assert.Equal("Fixed text.", r.Text);
        Assert.Contains("QA engineer", r.SystemPrompt);
        Assert.Contains("fixed tekst", handler.LastBody);
    }

    [Fact]
    public async Task A_remote_endpoint_is_never_contacted_unless_allowed()
    {
        var handler = new Handler(_ => Ok("x"));
        string? reported = null;
        var blocked = new OllamaRewriter(() => On("http://ollama.example.com:11434"), new HttpClient(handler), m => reported = m);
        var r = await blocked.RewriteAsync("private words", null, default);
        Assert.False(r.Rewritten);
        Assert.Equal("private words", r.Text);
        Assert.Equal(0, handler.Calls);
        Assert.Contains("not on this computer", reported);

        var allowed = new OllamaRewriter(() => On("http://ollama.example.com:11434", remote: true), new HttpClient(handler));
        Assert.True((await allowed.RewriteAsync("private words", null, default)).Rewritten);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://[::1]:11434")]
    [InlineData("http://localhost:11434/")]
    public void Loopback_addresses_are_accepted(string endpoint) =>
        Assert.True(OllamaRewriter.TryGetChatUri(On(endpoint), out _, out _));

    [Fact]
    public async Task Server_errors_and_outages_fall_back_to_the_raw_text()
    {
        var down = new OllamaRewriter(() => On(), new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))));
        Assert.Equal("hello", (await down.RewriteAsync("hello", null, default)).Text);
        var broken = new OllamaRewriter(() => On(), new HttpClient(new Handler(_ => throw new HttpRequestException("refused"))));
        Assert.False((await broken.RewriteAsync("hello", null, default)).Rewritten);
        var junk = new OllamaRewriter(() => On(), new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") })));
        Assert.Equal("hello", (await junk.RewriteAsync("hello", null, default)).Text);
    }

    [Fact]
    public async Task Disabled_rewriter_does_nothing()
    {
        var handler = new Handler(_ => Ok("x"));
        var r = await new OllamaRewriter(() => OllamaOptions.Disabled, new HttpClient(handler)).RewriteAsync("hi", null, default);
        Assert.False(r.Rewritten);
        Assert.Equal(0, handler.Calls);
    }
}
