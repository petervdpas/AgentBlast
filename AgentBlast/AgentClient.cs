using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentBlast.Interfaces;

namespace AgentBlast;

/// <summary>
/// Outcome of a one-shot ping against a configured AI provider.
/// Designed to be rendered inline in a host UI — short user-facing
/// message in <see cref="Message"/>, structured fields for tooling.
/// </summary>
public sealed record AgentPingResult(
    bool Success,
    string Message,
    TimeSpan? Latency,
    int? StatusCode)
{
    /// <summary>Build a success result.</summary>
    public static AgentPingResult Ok(string message, TimeSpan latency, int? statusCode = 200)
        => new(true, message, latency, statusCode);

    /// <summary>Build a failure result.</summary>
    public static AgentPingResult Fail(string message, TimeSpan? latency = null, int? statusCode = null)
        => new(false, message, latency, statusCode);
}

/// <summary>
/// One known model offered by an <see cref="IAgentProvider"/>. The id is
/// the wire string the provider's API accepts; the display name is for
/// UI dropdowns. Notes carry a one-line capability hint ("highest
/// capability, slowest" / "balanced default" / etc.).
/// </summary>
public sealed record AgentModelInfo(string Id, string DisplayName, string? Notes = null);

/// <summary>
/// One turn in a chat. Role is <c>"user"</c> or <c>"assistant"</c>;
/// the system prompt lives outside this collection on
/// <see cref="IAgentProvider.SendAsync"/>.
/// </summary>
public sealed record AgentMessage(string Role, string Content)
{
    /// <summary>Convenience: a user turn.</summary>
    public static AgentMessage User(string content) => new("user", content);

    /// <summary>Convenience: an assistant turn.</summary>
    public static AgentMessage Assistant(string content) => new("assistant", content);
}

/// <summary>
/// Outcome of one chat-completion call. <see cref="Text"/> is the
/// model's response when <see cref="Success"/> is true; <see cref="Error"/>
/// carries a user-facing failure message otherwise. <see cref="StopReason"/>
/// is the provider's hint about how the response ended — particularly
/// <c>"max_tokens"</c>, which means the model ran out of budget mid-thought
/// and the caller probably wants to raise the limit.
/// </summary>
public sealed record AgentCompletionResult(
    bool Success,
    string? Text,
    string? Error,
    int? StatusCode,
    TimeSpan? Latency,
    string? StopReason = null)
{
    /// <summary>Build a success result.</summary>
    public static AgentCompletionResult Ok(string text, TimeSpan latency, int? status = 200, string? stopReason = null)
        => new(true, text, null, status, latency, stopReason);

    /// <summary>Build a failure result.</summary>
    public static AgentCompletionResult Fail(string error, TimeSpan? latency = null, int? status = null)
        => new(false, null, error, status, latency, null);
}

/// <summary>
/// Connection-aware resolver delegate. Same shape Blast libraries already
/// consume (NetworkBlast, AzureBlast): the first arg is the connection
/// name (== resolver category in the Blast convention), the second is
/// the field name. Plaintext fields return their literal; vault-backed
/// fields go through whatever vault machinery the host wires in. A field
/// that doesn't exist or resolves empty MUST come back as
/// <c>string.Empty</c> — providers treat absent and empty the same way.
/// </summary>
public delegate Task<string> ConnectionFieldResolver(
    string connectionName,
    string fieldName,
    CancellationToken ct);

/// <summary>
/// Consumer-facing entry point for AgentBlast. Holds the registered
/// <see cref="IAgentProvider"/>s and dispatches to the right one based
/// on the connection's <c>kind</c> field. Adding a new provider = adding
/// a new <see cref="IAgentProvider"/> implementation; this class stays
/// untouched.
/// </summary>
public sealed class AgentClient
{
    private readonly HttpClient _http;
    private readonly ConnectionFieldResolver _resolver;
    private readonly IReadOnlyDictionary<string, IAgentProvider> _providersByKind;

    /// <summary>
    /// Build a client.
    /// </summary>
    /// <param name="http">HTTP client used by every provider. Configure the timeout to suit the slowest call you expect (chat completions for full code rewrites can legitimately take 30-60+ seconds).</param>
    /// <param name="providers">Provider implementations to register, keyed by <see cref="IAgentProvider.Kind"/>.</param>
    /// <param name="resolver">Connection field resolver. The host owns this; the client never touches a vault directly.</param>
    public AgentClient(
        HttpClient http,
        IEnumerable<IAgentProvider> providers,
        ConnectionFieldResolver resolver)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        if (providers is null) throw new ArgumentNullException(nameof(providers));
        _providersByKind = providers.ToDictionary(
            p => p.Kind,
            p => p,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Provider kinds the client can dispatch to (for diagnostics or dropdowns).</summary>
    public IReadOnlyCollection<string> RegisteredKinds => _providersByKind.Keys.ToArray();

    /// <summary>Find a registered provider by kind, or null if none matches.</summary>
    public IAgentProvider? FindProvider(string kind)
        => _providersByKind.TryGetValue(kind, out var p) ? p : null;

    /// <summary>Every registered provider (for "configure provider" UIs).</summary>
    public IReadOnlyList<IAgentProvider> AllProviders => _providersByKind.Values.ToArray();

    /// <summary>
    /// Ping the configured provider. The connection (looked up by name
    /// through the wired resolver) must carry a plaintext <c>kind</c>
    /// field whose value matches a registered provider.
    /// </summary>
    public async Task<AgentPingResult> PingAsync(
        string connectionName,
        CancellationToken ct = default)
    {
        var kind = await _resolver(connectionName, "kind", ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(kind))
            return AgentPingResult.Fail(
                "Connection has no 'kind' field. Add a plaintext field 'kind' with one of: "
                + string.Join(", ", _providersByKind.Keys));

        if (!_providersByKind.TryGetValue(kind, out var provider))
            return AgentPingResult.Fail(
                $"No provider registered for kind '{kind}'. Known kinds: "
                + string.Join(", ", _providersByKind.Keys));

        return await provider.PingAsync(connectionName, _resolver, _http, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Send a chat completion via the configured provider. Same dispatch
    /// rules as <see cref="PingAsync"/>: the connection's <c>kind</c>
    /// field selects the provider.
    /// </summary>
    public async Task<AgentCompletionResult> SendAsync(
        string connectionName,
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken ct = default)
    {
        var kind = await _resolver(connectionName, "kind", ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(kind))
            return AgentCompletionResult.Fail(
                "Connection has no 'kind' field. Add a plaintext field 'kind' with one of: "
                + string.Join(", ", _providersByKind.Keys));

        if (!_providersByKind.TryGetValue(kind, out var provider))
            return AgentCompletionResult.Fail(
                $"No provider registered for kind '{kind}'. Known kinds: "
                + string.Join(", ", _providersByKind.Keys));

        return await provider.SendAsync(connectionName, systemPrompt, messages, _resolver, _http, ct).ConfigureAwait(false);
    }
}
