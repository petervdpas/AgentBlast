using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentBlast;
using AgentBlast.Interfaces;
using Xunit;

namespace AgentBlast.Tests;

/// <summary>
/// Tests for the generic <see cref="AgentClient"/> + the
/// <see cref="AnthropicProvider"/> implementation. Uses a mock
/// <see cref="HttpMessageHandler"/> so we can simulate every Anthropic
/// response shape (200, 401, 404, network failure) without touching
/// the network, and a stub <see cref="ConnectionFieldResolver"/> so the
/// client is exercised without involving any vault or connection store.
/// </summary>
public sealed class AgentClientTests
{
    private const string Anthropic = "Anthropic";

    [Fact]
    public async Task PingAsync_HappyPath_ReturnsSuccessWithLatency()
    {
        var client = NewClient(StubHandler.Ok("""{"id":"msg_01","content":[{"type":"text","text":"ok"}]}"""),
            AnthropicResolver(apiKey: "sk-test", baseUrl: "https://api.anthropic.com", model: "claude-opus-4-7"));

        var result = await client.PingAsync(Anthropic);

        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(result.Latency);
        Assert.Contains("Connected", result.Message);
    }

    [Fact]
    public async Task PingAsync_401_MapsToInvalidApiKey()
    {
        var client = NewClient(StubHandler.Status(HttpStatusCode.Unauthorized, """{"error":{"message":"invalid x-api-key"}}"""),
            AnthropicResolver(apiKey: "sk-bogus", baseUrl: "https://api.anthropic.com", model: "claude-opus-4-7"));

        var result = await client.PingAsync(Anthropic);

        Assert.False(result.Success);
        Assert.Equal(401, result.StatusCode);
        Assert.Contains("Invalid API key", result.Message);
    }

    [Fact]
    public async Task PingAsync_404_MapsToEndpointOrModelNotFound()
    {
        var client = NewClient(StubHandler.Status(HttpStatusCode.NotFound, "{}"),
            AnthropicResolver(apiKey: "sk-test", baseUrl: "https://api.anthropic.com", model: "no-such-model"));

        var result = await client.PingAsync(Anthropic);

        Assert.False(result.Success);
        Assert.Contains("model name", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PingAsync_NetworkException_MapsToNetworkError()
    {
        var client = NewClient(StubHandler.Throws(new HttpRequestException("name resolution failed")),
            AnthropicResolver(apiKey: "sk-test", baseUrl: "https://api.anthropic.com", model: "claude-opus-4-7"));

        var result = await client.PingAsync(Anthropic);

        Assert.False(result.Success);
        Assert.Contains("Network error", result.Message);
        Assert.Null(result.StatusCode);
    }

    [Fact]
    public async Task PingAsync_BaseUrlAlreadyEndsInMessages_DoesNotDoubleAppend()
    {
        // Some users put the full endpoint into baseUrl; tolerate it
        // instead of producing /v1/messages/v1/messages.
        var capturing = new StubHandler((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("ok"),
            RequestMessage = req,
        }));
        var client = NewClient(capturing,
            AnthropicResolver(apiKey: "k", baseUrl: "https://api.anthropic.com/v1/messages", model: "claude-opus-4-7"));

        var result = await client.PingAsync(Anthropic);

        Assert.True(result.Success);
        Assert.NotNull(capturing.LastUrl);
        Assert.Equal("https://api.anthropic.com/v1/messages", capturing.LastUrl!.ToString());
    }

    [Fact]
    public async Task PingAsync_MissingKindField_FailsBeforeReachingProvider()
    {
        var client = NewClient(StubHandler.Throws(new InvalidOperationException("HTTP should not be called")),
            new StubResolver()
                .Set("noKind", "baseUrl", "https://api.anthropic.com")
                .Set("noKind", "model", "claude-opus-4-7")
                .Set("noKind", "apikey", "k"));

        var result = await client.PingAsync("noKind");

        Assert.False(result.Success);
        Assert.Contains("kind", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PingAsync_UnknownKind_ListsRegisteredKinds()
    {
        var client = NewClient(StubHandler.Throws(new InvalidOperationException("HTTP should not be called")),
            new StubResolver()
                .Set("alien", "kind", "alien-llm")
                .Set("alien", "baseUrl", "https://example.com")
                .Set("alien", "model", "x")
                .Set("alien", "apikey", "k"));

        var result = await client.PingAsync("alien");

        Assert.False(result.Success);
        Assert.Contains("alien-llm", result.Message);
        Assert.Contains("anthropic", result.Message);
    }

    [Fact]
    public async Task PingAsync_VaultBackedApiKey_ResolvesThroughResolver()
    {
        // The resolver delegate models the vault-backed-apikey case: it
        // returns the resolved string regardless of whether the underlying
        // field was plaintext or vault-ref. The provider does not care.
        var capturing = new StubHandler((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("ok"),
            RequestMessage = req,
        }));
        var client = NewClient(capturing,
            AnthropicResolver(apiKey: "sk-from-vault", baseUrl: "https://api.anthropic.com", model: "claude-opus-4-7"));

        var result = await client.PingAsync(Anthropic);

        Assert.True(result.Success);
        Assert.Equal("sk-from-vault", capturing.LastApiKeyHeader);
    }

    [Fact]
    public async Task PingAsync_MissingApiKeyField_FailsCleanly()
    {
        var client = NewClient(StubHandler.Throws(new InvalidOperationException("HTTP should not be called")),
            new StubResolver()
                .Set(Anthropic, "kind", "anthropic")
                .Set(Anthropic, "baseUrl", "https://api.anthropic.com")
                .Set(Anthropic, "model", "claude-opus-4-7"));
                // no apikey

        var result = await client.PingAsync(Anthropic);

        Assert.False(result.Success);
        Assert.Contains("apikey", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnthropicProvider_KnownModels_IncludesOpus47SonnetAndHaiku()
    {
        var ids = new AnthropicProvider().KnownModels.Select(m => m.Id).ToArray();
        Assert.Contains("claude-opus-4-7",          ids);
        Assert.Contains("claude-sonnet-4-6",        ids);
        Assert.Contains("claude-haiku-4-5-20251001", ids);
    }

    [Fact]
    public void AgentClient_FindProvider_IsCaseInsensitive()
    {
        var client = NewClient(StubHandler.Throws(new InvalidOperationException()), new StubResolver());
        Assert.NotNull(client.FindProvider("anthropic"));
        Assert.NotNull(client.FindProvider("ANTHROPIC"));
        Assert.NotNull(client.FindProvider("Anthropic"));
        Assert.Null(client.FindProvider("openai")); // not registered
    }

    [Fact]
    public async Task PingAsync_PassesConnectionNameToResolver()
    {
        // Guard the contract: the provider must ask the resolver for the
        // exact connection name AgentClient was called with, not e.g. its kind.
        var seenNames = new List<string>();
        var resolver = new ConnectionFieldResolver((name, field, _) =>
        {
            seenNames.Add(name);
            return field switch
            {
                "kind"    => Task.FromResult("anthropic"),
                "apikey"  => Task.FromResult("k"),
                "baseUrl" => Task.FromResult("https://api.anthropic.com"),
                "model"   => Task.FromResult("claude-opus-4-7"),
                _         => Task.FromResult(string.Empty),
            };
        });
        var http = new HttpClient(StubHandler.Ok("""{"content":[{"type":"text","text":"ok"}]}"""));
        var client = new AgentClient(http, new IAgentProvider[] { new AnthropicProvider() }, resolver);

        await client.PingAsync("MyAgentConnection");

        Assert.NotEmpty(seenNames);
        Assert.All(seenNames, n => Assert.Equal("MyAgentConnection", n));
    }

    // ───────────────────────── SendAsync ────────────────────────────

    [Fact]
    public async Task SendAsync_HappyPath_ReturnsExtractedText()
    {
        var client = NewClient(StubHandler.Ok(
            """{"id":"msg_01","content":[{"type":"text","text":"Hi from Claude."}],"stop_reason":"end_turn"}"""),
            AnthropicResolver(apiKey: "sk-test", baseUrl: "https://api.anthropic.com", model: "claude-opus-4-7"));

        var result = await client.SendAsync(Anthropic, "you are helpful", new[] { AgentMessage.User("hi") });

        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("Hi from Claude.", result.Text);
        Assert.NotNull(result.Latency);
    }

    [Fact]
    public async Task SendAsync_MultipleTextBlocks_AreConcatenated()
    {
        var client = NewClient(StubHandler.Ok(
            """{"content":[{"type":"text","text":"first"},{"type":"text","text":"second"}]}"""),
            AnthropicResolver(apiKey: "k", baseUrl: "https://api.anthropic.com", model: "claude-opus-4-7"));

        var result = await client.SendAsync(Anthropic, "", new[] { AgentMessage.User("x") });

        Assert.True(result.Success);
        Assert.Equal("first\nsecond", result.Text);
    }

    [Fact]
    public async Task SendAsync_EmptySystemPrompt_OmitsSystemFromPayload()
    {
        // Anthropic rejects "system": "" — so we must drop the field entirely
        // when the picker produced no directing context.
        var capturing = new StubHandler((req, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"content":[{"type":"text","text":"ok"}]}"""),
                RequestMessage = req,
            });
        });
        var client = NewClient(capturing,
            AnthropicResolver(apiKey: "k", baseUrl: "https://api.anthropic.com", model: "claude-opus-4-7"));

        var result = await client.SendAsync(Anthropic, systemPrompt: "", new[] { AgentMessage.User("x") });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task SendAsync_SystemPromptIncluded_WhenNonEmpty()
    {
        string? capturedBody = null;
        var handler = new StubHandler(async (req, _) =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"content":[{"type":"text","text":"ok"}]}"""),
            };
        });
        var client = NewClient(handler,
            AnthropicResolver(apiKey: "k", baseUrl: "https://api.anthropic.com", model: "claude-opus-4-7"));

        await client.SendAsync(Anthropic, systemPrompt: "you are helpful", new[] { AgentMessage.User("x") });

        Assert.NotNull(capturedBody);
        Assert.Contains("\"system\":\"you are helpful\"", capturedBody);
    }

    [Fact]
    public async Task SendAsync_NoMessages_FailsCleanly()
    {
        var client = NewClient(StubHandler.Throws(new InvalidOperationException("HTTP should not be called")),
            AnthropicResolver(apiKey: "k", baseUrl: "https://api.anthropic.com", model: "claude-opus-4-7"));

        var result = await client.SendAsync(Anthropic, "sys", Array.Empty<AgentMessage>());

        Assert.False(result.Success);
        Assert.Contains("at least one message", result.Error);
    }

    [Fact]
    public async Task SendAsync_401_MapsToInvalidApiKey()
    {
        var client = NewClient(StubHandler.Status(HttpStatusCode.Unauthorized,
            """{"error":{"message":"invalid x-api-key"}}"""),
            AnthropicResolver(apiKey: "bogus", baseUrl: "https://api.anthropic.com", model: "claude-opus-4-7"));

        var result = await client.SendAsync(Anthropic, "sys", new[] { AgentMessage.User("x") });

        Assert.False(result.Success);
        Assert.Equal(401, result.StatusCode);
        Assert.Contains("Invalid API key", result.Error);
    }

    [Fact]
    public async Task SendAsync_NetworkException_MapsToNetworkError()
    {
        var client = NewClient(StubHandler.Throws(new HttpRequestException("dns fail")),
            AnthropicResolver(apiKey: "k", baseUrl: "https://api.anthropic.com", model: "claude-opus-4-7"));

        var result = await client.SendAsync(Anthropic, "sys", new[] { AgentMessage.User("x") });

        Assert.False(result.Success);
        Assert.Contains("Network error", result.Error);
    }

    [Fact]
    public async Task SendAsync_ResponseWithNoTextContent_FailsCleanly()
    {
        var client = NewClient(StubHandler.Ok("""{"content":[{"type":"image","source":"..."}]}"""),
            AnthropicResolver(apiKey: "k", baseUrl: "https://api.anthropic.com", model: "claude-opus-4-7"));

        var result = await client.SendAsync(Anthropic, "sys", new[] { AgentMessage.User("x") });

        Assert.False(result.Success);
        Assert.Contains("no text content", result.Error);
    }

    [Fact]
    public async Task SendAsync_MissingKindField_FailsBeforeReachingProvider()
    {
        var client = NewClient(StubHandler.Throws(new InvalidOperationException("HTTP should not be called")),
            new StubResolver()
                .Set("X", "baseUrl", "https://api.anthropic.com")
                .Set("X", "model", "claude-opus-4-7")
                .Set("X", "apikey", "k"));

        var result = await client.SendAsync("X", "sys", new[] { AgentMessage.User("x") });

        Assert.False(result.Success);
        Assert.Contains("'kind' field", result.Error);
    }

    [Fact]
    public async Task SendAsync_InvalidMaxTokensField_FailsWithClearMessage()
    {
        // Silent fallback on bad input would hide typos like "8k" — a
        // common mistake that's hard to debug otherwise. Strict fail.
        var client = NewClient(StubHandler.Throws(new InvalidOperationException("HTTP should not be called")),
            new StubResolver()
                .Set("X", "kind",      "anthropic")
                .Set("X", "baseUrl",   "https://api.anthropic.com")
                .Set("X", "model",     "claude-opus-4-7")
                .Set("X", "apikey",    "k")
                .Set("X", "maxTokens", "8k"));

        var result = await client.SendAsync("X", "sys", new[] { AgentMessage.User("x") });

        Assert.False(result.Success);
        Assert.Contains("maxTokens", result.Error);
        Assert.Contains("positive integer", result.Error);
    }

    [Fact]
    public async Task SendAsync_NegativeMaxTokensField_FailsWithClearMessage()
    {
        var client = NewClient(StubHandler.Throws(new InvalidOperationException("HTTP should not be called")),
            new StubResolver()
                .Set("X", "kind",      "anthropic")
                .Set("X", "baseUrl",   "https://api.anthropic.com")
                .Set("X", "model",     "claude-opus-4-7")
                .Set("X", "apikey",    "k")
                .Set("X", "maxTokens", "-1"));

        var result = await client.SendAsync("X", "sys", new[] { AgentMessage.User("x") });

        Assert.False(result.Success);
        Assert.Contains("positive integer", result.Error);
    }

    [Fact]
    public async Task SendAsync_RespectsCustomMaxTokensField_WhenPresent()
    {
        string? capturedBody = null;
        var handler = new StubHandler(async (req, _) =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"content":[{"type":"text","text":"ok"}]}"""),
            };
        });
        var client = NewClient(handler,
            new StubResolver()
                .Set("X", "kind",      "anthropic")
                .Set("X", "baseUrl",   "https://api.anthropic.com")
                .Set("X", "model",     "claude-opus-4-7")
                .Set("X", "apikey",    "k")
                .Set("X", "maxTokens", "1024"));

        await client.SendAsync("X", "sys", new[] { AgentMessage.User("x") });

        Assert.NotNull(capturedBody);
        Assert.Contains("\"max_tokens\":1024", capturedBody);
    }

    // ───────────────────────── helpers ──────────────────────────────

    private static AgentClient NewClient(StubHandler handler, StubResolver resolver)
    {
        var http = new HttpClient(handler);
        return new AgentClient(http, new IAgentProvider[] { new AnthropicProvider() }, resolver.Delegate);
    }

    private static StubResolver AnthropicResolver(string apiKey, string baseUrl, string model)
        => new StubResolver()
            .Set(Anthropic, "kind",    "anthropic")
            .Set(Anthropic, "baseUrl", baseUrl)
            .Set(Anthropic, "model",   model)
            .Set(Anthropic, "apikey",  apiKey);

    /// <summary>Tiny in-memory <see cref="ConnectionFieldResolver"/> backed by a (connection, field) → value map.</summary>
    private sealed class StubResolver
    {
        private readonly Dictionary<(string conn, string field), string> _values =
            new(EqualityComparer<(string, string)>.Default);

        public StubResolver Set(string connectionName, string fieldName, string value)
        {
            _values[(connectionName, fieldName)] = value;
            return this;
        }

        public ConnectionFieldResolver Delegate => ResolveAsync;

        public Task<string> ResolveAsync(string connectionName, string fieldName, CancellationToken ct)
            => Task.FromResult(_values.TryGetValue((connectionName, fieldName), out var v) ? v : string.Empty);
    }

    /// <summary>HttpMessageHandler that lets a test inject any response or exception.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _onSend;

        public Uri? LastUrl { get; private set; }
        public string? LastApiKeyHeader { get; private set; }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> onSend) => _onSend = onSend;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            LastUrl = req.RequestUri;
            if (req.Headers.TryGetValues("x-api-key", out var keys))
                LastApiKeyHeader = keys.FirstOrDefault();
            return _onSend(req, ct);
        }

        public static StubHandler Ok(string body) => Status(HttpStatusCode.OK, body);

        public static StubHandler Status(HttpStatusCode code, string body)
            => new((_, _) => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) }));

        public static StubHandler Throws(Exception ex) => new((_, _) => throw ex);
    }
}
