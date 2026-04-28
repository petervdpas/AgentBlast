# AgentBlast 🤖

[![NuGet](https://img.shields.io/nuget/v/AgentBlast.svg)](https://www.nuget.org/packages/AgentBlast)
[![NuGet Downloads](https://img.shields.io/nuget/dt/AgentBlast.svg)](https://www.nuget.org/packages/AgentBlast)
[![License](https://img.shields.io/github/license/petervdpas/AgentBlast.svg)](https://opensource.org/licenses/MIT)

![AgentBlast](https://raw.githubusercontent.com/petervdpas/AgentBlast/master/assets/icon.png)

**AgentBlast** is a programmable LLM client for .NET — a sibling to
[NetworkBlast](https://www.nuget.org/packages/NetworkBlast) and
[AzureBlast](https://www.nuget.org/packages/AzureBlast) in the Blast family.
One front door (`AgentClient`) dispatches to provider-specific implementations
based on a connection's `kind` field, and every secret (API key, base URL,
model id, token cap) is pulled through the same `Func<category, key, ct, Task<string>>`
resolver delegate the rest of the Blast family already speaks.

---

> ✅ **Status:** 1.0 — Anthropic provider in the box (Claude Opus 4.7, Sonnet 4.6,
> Haiku 4.5). OpenAI and Ollama planned. Vault-agnostic, host-agnostic, no
> SDK dependency on the Anthropic side (raw `HttpClient` + `System.Text.Json`).

---

## ✨ Why AgentBlast

* 🔹 **One client, many providers** — `AgentClient.SendAsync(connectionName, system, messages)` dispatches to the registered `IAgentProvider` matching the connection's `kind` field. Adding a provider = adding an `IAgentProvider` implementation.
* 🔹 **Vault-agnostic** — secrets are pulled through a `Func<category, key, ct, Task<string>>` delegate, not a hard reference to SecretBlast or anything else. Wire any resolver you like.
* 🔹 **Connection-driven config** — model, base URL, max-tokens, API key all live in a connection bag (the same shape NetworkBlast and AzureBlast already consume). Change provider by changing one connection field; the calling code stays unchanged.
* 🔹 **Strict input validation** — no silent fallbacks. `maxTokens: "8k"` is a clean error, not a typo that gets quietly clamped.
* 🔹 **No SDK lock-in** — Anthropic is talked to via raw HTTP + `System.Text.Json`. No `Anthropic.SDK` reference, no version churn when Anthropic ships a new client library.
* 🔹 **Provider-agnostic types** — `AgentMessage`, `AgentCompletionResult`, `AgentPingResult`, `AgentModelInfo`. The same shapes carry across providers.
* 🔹 **Built for hosts that already speak the Blast convention** — drop into TaskBlaster, a console tool, a service. Anything that can produce a resolver delegate can host AgentBlast.

---

## 📦 Installation

```bash
dotnet add package AgentBlast
```

---

## 🚀 Quick start

### Minimal — hardcoded API key

```csharp
using System.Net.Http;
using AgentBlast;
using AgentBlast.Interfaces;

// Connection bag the resolver will satisfy. Plaintext only here for brevity;
// in real life apikey would be vault-backed (see "Vault-backed" below).
var fields = new Dictionary<(string, string), string>
{
    [("anthropic", "kind")]    = "anthropic",
    [("anthropic", "baseUrl")] = "https://api.anthropic.com",
    [("anthropic", "model")]   = "claude-sonnet-4-6",
    [("anthropic", "apikey")]  = "sk-ant-api03-…",
};

ConnectionFieldResolver resolver = (conn, key, _) =>
    Task.FromResult(fields.TryGetValue((conn, key), out var v) ? v : string.Empty);

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
var client = new AgentClient(http, new IAgentProvider[] { new AnthropicProvider() }, resolver);

var result = await client.SendAsync(
    connectionName: "anthropic",
    systemPrompt:   "You are a helpful assistant.",
    messages:       new[] { AgentMessage.User("What's 2 + 2?") });

Console.WriteLine(result.Success ? result.Text : $"failed: {result.Error}");
```

### Vault-backed — wire a resolver from your vault

The host owns secret resolution. AgentBlast just calls the delegate; how it
resolves is up to you. Typical shape with a connection-store overlay:

```csharp
ConnectionFieldResolver resolver = async (conn, key, ct) =>
{
    // 1. Look up the connection bag in connections.json
    // 2. If field is plaintext, return its literal
    // 3. If field is a vault ref, resolve it through your vault service
    // ...
};

var client = new AgentClient(http, providers, resolver);
```

In practice, hosts like TaskBlaster wrap an existing
[NetworkBlast](https://www.nuget.org/packages/NetworkBlast)-style
`ConnectionsResolver` and feed it directly into AgentBlast — same
delegate, same convention.

### Ping a configured connection

`PingAsync` posts a 5-token test message and discards the body. Use it for
"Test" buttons in settings UIs:

```csharp
var ping = await client.PingAsync("anthropic");
Console.WriteLine(ping.Success
    ? $"✓ {ping.Message}"
    : $"✗ {ping.Message}");
```

---

## 🧠 Connection schema

A connection bag is a string → string map keyed by field name. AgentBlast
reads these well-known fields:

| Field       | Required | Meaning |
|-------------|----------|---------|
| `kind`      | ✅       | Selects the provider. `anthropic` today; `openai` / `ollama` planned. |
| `baseUrl`   | ✅       | Provider endpoint. The Anthropic provider tolerates `/v1/messages` either appended or omitted. |
| `model`     | ✅       | Wire model id (e.g. `claude-opus-4-7`, `claude-sonnet-4-6`, `claude-haiku-4-5-20251001`). |
| `apikey`    | ✅       | API key. Vault-backed in production; plaintext only for examples. |
| `maxTokens` |          | Per-response output cap. Optional; defaults to 8192. Validated strictly when present (positive integer). |

Unknown fields are ignored. Adding a provider that needs more fields = adding
its own implementation; the AgentClient itself doesn't care.

---

## 🧩 Adding a provider

```csharp
public sealed class OpenAiProvider : IAgentProvider
{
    public string Kind => "openai";
    public string DisplayName => "OpenAI";
    public IReadOnlyList<AgentModelInfo> KnownModels { get; } = new[]
    {
        new AgentModelInfo("gpt-5.2", "GPT-5.2", "Flagship reasoning model."),
        // …
    };

    public async Task<AgentPingResult> PingAsync(
        string connectionName,
        ConnectionFieldResolver resolver,
        HttpClient http,
        CancellationToken ct)
    {
        var apiKey = await resolver(connectionName, "apikey", ct);
        // … call /v1/models or similar; return AgentPingResult.Ok / Fail
    }

    public async Task<AgentCompletionResult> SendAsync(
        string connectionName,
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        ConnectionFieldResolver resolver,
        HttpClient http,
        CancellationToken ct)
    {
        // … call /v1/chat/completions; parse response; return AgentCompletionResult
    }
}

// Register alongside Anthropic:
var client = new AgentClient(http,
    new IAgentProvider[] { new AnthropicProvider(), new OpenAiProvider() },
    resolver);
```

---

## 🤖 AI assistants — Blast-family convention

AgentBlast's main assembly stamps the Blast-family `Blast.PrimaryFacade`
attribute, naming `AgentClient` as its canonical front-door type. AI
assistants (e.g. TaskBlaster's Directed-AI layer) read this attribute via
reflection to identify entry points without scanning every public type:

```csharp
typeof(AgentClient).Assembly
    .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
    .Where(a => a.Key == "Blast.PrimaryFacade")
    .Select(a => a.Value)
    .ToList();
// → ["AgentBlast.AgentClient"]
```

| Package    | Front doors        |
|------------|--------------------|
| AgentBlast | `AgentBlast.AgentClient` |

---

## 📜 License

MIT — see [LICENSE.txt](assets/LICENSE.txt).
