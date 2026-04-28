using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AgentBlast.Interfaces;

/// <summary>
/// One AI provider. Implementations are stateless and registered against
/// an <see cref="AgentClient"/>; the client dispatches to whichever
/// implementation matches the connection's <c>kind</c> field. The
/// interface is deliberately vault-free and connection-store-free: the
/// provider never sees a connection object or a vault service, only a
/// <see cref="ConnectionFieldResolver"/> delegate. That keeps providers
/// host-agnostic — any caller that can produce a resolver delegate can
/// host AgentBlast (TaskBlaster, a console tool, a web service, a unit
/// test).
/// </summary>
public interface IAgentProvider
{
    /// <summary>The connection-<c>kind</c> value this provider answers to (e.g. "anthropic").</summary>
    string Kind { get; }

    /// <summary>Display name for UI ("Anthropic" / "OpenAI" / "Ollama").</summary>
    string DisplayName { get; }

    /// <summary>
    /// Models this provider knows about, ordered by capability (highest
    /// first by convention). Used by model-picker UIs and as a hint for
    /// the ping flow when the connection's model field is blank or invalid.
    /// </summary>
    IReadOnlyList<AgentModelInfo> KnownModels { get; }

    /// <summary>Send a minimal request and classify the response into success or friendly failure.</summary>
    Task<AgentPingResult> PingAsync(
        string connectionName,
        ConnectionFieldResolver resolver,
        HttpClient http,
        CancellationToken ct);

    /// <summary>
    /// Send a chat completion. <paramref name="systemPrompt"/> is the
    /// stable directing context; <paramref name="messages"/> is the
    /// turn-by-turn conversation history (oldest first), with the latest
    /// user message as the final entry.
    /// </summary>
    Task<AgentCompletionResult> SendAsync(
        string connectionName,
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        ConnectionFieldResolver resolver,
        HttpClient http,
        CancellationToken ct);
}
