using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentBlast.Interfaces;

namespace AgentBlast;

/// <summary>
/// Anthropic provider. Posts to <c>{baseUrl}/v1/messages</c>;
/// <see cref="PingAsync"/> sends a 5-token test message and discards the
/// body — we only care that the round-trip works. <see cref="SendAsync"/>
/// sends the real conversation and extracts every <c>{"type":"text"}</c>
/// block from the response.
/// </summary>
public sealed class AnthropicProvider : IAgentProvider
{
    /// <summary>Anthropic's API version header value.</summary>
    public const string AnthropicVersion = "2023-06-01";

    /// <inheritdoc />
    public string Kind => "anthropic";

    /// <inheritdoc />
    public string DisplayName => "Anthropic";

    /// <inheritdoc />
    public IReadOnlyList<AgentModelInfo> KnownModels { get; } = new[]
    {
        new AgentModelInfo(
            Id:          "claude-opus-4-7",
            DisplayName: "Claude Opus 4.7",
            Notes:       "Highest capability, slowest, most expensive. Use for hard reasoning."),
        new AgentModelInfo(
            Id:          "claude-sonnet-4-6",
            DisplayName: "Claude Sonnet 4.6",
            Notes:       "Balanced; good default for most use."),
        new AgentModelInfo(
            Id:          "claude-haiku-4-5-20251001",
            DisplayName: "Claude Haiku 4.5",
            Notes:       "Fastest, cheapest. Use for high-volume / simple operations."),
    };

    /// <inheritdoc />
    public async Task<AgentPingResult> PingAsync(
        string connectionName,
        ConnectionFieldResolver resolver,
        HttpClient http,
        CancellationToken ct)
    {
        try
        {
            var apiKey  = await resolver(connectionName, "apikey",  ct).ConfigureAwait(false);
            var baseUrl = await resolver(connectionName, "baseUrl", ct).ConfigureAwait(false);
            var model   = await resolver(connectionName, "model",   ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(apiKey))  return AgentPingResult.Fail("Connection has no apikey field (or it resolved empty).");
            if (string.IsNullOrEmpty(baseUrl)) return AgentPingResult.Fail("Connection has no baseUrl field.");
            if (string.IsNullOrEmpty(model))   return AgentPingResult.Fail("Connection has no model field.");

            // Tolerate baseUrl with or without /v1/messages — both shapes
            // exist in user setups and we don't want to make them re-edit.
            var url = baseUrl.TrimEnd('/');
            if (!url.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
                url += "/v1/messages";

            var body = JsonSerializer.Serialize(new
            {
                model,
                max_tokens = 5,
                messages = new[] { new { role = "user", content = "ping" } },
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", AnthropicVersion);

            var sw = Stopwatch.StartNew();
            using var response = await http.SendAsync(req, ct).ConfigureAwait(false);
            sw.Stop();

            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                return AgentPingResult.Ok($"Connected. Model responded in {sw.ElapsedMilliseconds} ms.", sw.Elapsed, status);

            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return AgentPingResult.Fail(MapErrorMessage(response.StatusCode, errorBody), sw.Elapsed, status);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            return AgentPingResult.Fail("Test cancelled.");
        }
        catch (HttpRequestException ex)
        {
            return AgentPingResult.Fail($"Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return AgentPingResult.Fail($"Unexpected error: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AgentCompletionResult> SendAsync(
        string connectionName,
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        ConnectionFieldResolver resolver,
        HttpClient http,
        CancellationToken ct)
    {
        try
        {
            var apiKey  = await resolver(connectionName, "apikey",  ct).ConfigureAwait(false);
            var baseUrl = await resolver(connectionName, "baseUrl", ct).ConfigureAwait(false);
            var model   = await resolver(connectionName, "model",   ct).ConfigureAwait(false);
            var maxTokensRaw = await resolver(connectionName, "maxTokens", ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(apiKey))  return AgentCompletionResult.Fail("Connection has no apikey field (or it resolved empty).");
            if (string.IsNullOrEmpty(baseUrl)) return AgentCompletionResult.Fail("Connection has no baseUrl field.");
            if (string.IsNullOrEmpty(model))   return AgentCompletionResult.Fail("Connection has no model field.");
            if (messages is null || messages.Count == 0)
                return AgentCompletionResult.Fail("Send requires at least one message.");

            // Per-provider default when the connection omits maxTokens.
            // When the field IS present we validate strictly — silently
            // falling back on bad input would mask typos like "8k" or
            // accidental whitespace, and the user would never know why
            // their cap wasn't being honoured.
            var maxTokens = 8192;
            if (!string.IsNullOrEmpty(maxTokensRaw))
            {
                if (!int.TryParse(maxTokensRaw.Trim(), System.Globalization.NumberStyles.Integer,
                                  System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                    || parsed <= 0)
                {
                    return AgentCompletionResult.Fail(
                        $"Connection 'maxTokens' must be a positive integer; got '{maxTokensRaw}'.");
                }
                maxTokens = parsed;
            }

            var url = baseUrl.TrimEnd('/');
            if (!url.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
                url += "/v1/messages";

            // Anthropic accepts only "user" / "assistant" roles in the
            // messages array; the system prompt is a separate top-level
            // field and is omitted entirely when empty (the API rejects
            // an empty string).
            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["max_tokens"] = maxTokens,
                ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            };
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                payload["system"] = systemPrompt;

            var body = JsonSerializer.Serialize(payload);

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", AnthropicVersion);

            var sw = Stopwatch.StartNew();
            using var response = await http.SendAsync(req, ct).ConfigureAwait(false);
            sw.Stop();

            var status = (int)response.StatusCode;
            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return AgentCompletionResult.Fail(MapErrorMessage(response.StatusCode, responseBody), sw.Elapsed, status);

            var (text, stopReason) = ExtractTextAndStop(responseBody);
            return text is null
                ? AgentCompletionResult.Fail("Response had no text content.", sw.Elapsed, status)
                : AgentCompletionResult.Ok(text, sw.Elapsed, status, stopReason);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            return AgentCompletionResult.Fail("Request cancelled.");
        }
        catch (HttpRequestException ex)
        {
            return AgentCompletionResult.Fail($"Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return AgentCompletionResult.Fail($"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Concatenate every <c>{"type":"text","text":"..."}</c> block in the
    /// response and pull the top-level <c>stop_reason</c>. Anthropic returns
    /// content as an array (so future tool-use / image blocks slot
    /// alongside text) — we only care about text for now. <c>stop_reason</c>
    /// values we expect: <c>end_turn</c> (natural finish), <c>max_tokens</c>
    /// (response was truncated, caller should raise the budget),
    /// <c>stop_sequence</c>, <c>tool_use</c>.
    /// </summary>
    private static (string? Text, string? StopReason) ExtractTextAndStop(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            string? stopReason = null;
            if (doc.RootElement.TryGetProperty("stop_reason", out var stopEl)
                && stopEl.ValueKind == JsonValueKind.String)
            {
                stopReason = stopEl.GetString();
            }

            if (!doc.RootElement.TryGetProperty("content", out var contentArr)) return (null, stopReason);
            if (contentArr.ValueKind != JsonValueKind.Array) return (null, stopReason);

            var sb = new StringBuilder();
            foreach (var part in contentArr.EnumerateArray())
            {
                if (!part.TryGetProperty("type", out var typeEl)) continue;
                if (!string.Equals(typeEl.GetString(), "text", StringComparison.Ordinal)) continue;
                if (part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(textEl.GetString());
                }
            }
            return (sb.Length > 0 ? sb.ToString() : null, stopReason);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string MapErrorMessage(HttpStatusCode status, string errorBody) => (int)status switch
    {
        401 => "Invalid API key.",
        403 => "API key forbidden — check workspace / scope on the Anthropic console.",
        404 => "Endpoint or model not found — check baseUrl and the model name.",
        429 => "Rate-limited. Try again in a moment.",
        500 or 502 or 503 or 504 => "Anthropic server error. Try again in a moment.",
        _   => $"HTTP {(int)status}: {Truncate(errorBody, 200)}",
    };

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
