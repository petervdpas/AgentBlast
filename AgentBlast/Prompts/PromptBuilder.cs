using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AgentBlast.Knowledge;

namespace AgentBlast.Prompts;

/// <summary>
/// Pure function: composes a structured prompt from already-picked
/// knowledge blocks and a snapshot of loaded references.
///
/// <para>
/// The output is split system / user the way the modern chat APIs expect:
/// directing context (which doesn't change call-to-call within a session)
/// goes in <see cref="AssembledPrompt.SystemMessage"/> so the provider
/// can cache it; the per-call ask goes in <see cref="AssembledPrompt.UserMessage"/>.
/// </para>
///
/// <para>
/// References are filtered to <see cref="LoadedReferenceOrigin.Blast"/>
/// and <see cref="LoadedReferenceOrigin.External"/> only. The framework
/// BCL and the host application's own assemblies would dwarf the
/// actually-useful content in tokens and offer no directing value —
/// the model already knows about <c>System.*</c>.
/// </para>
/// </summary>
public static class PromptBuilder
{
    /// <summary>
    /// Build the prompt. <paramref name="blocks"/> is expected to come from
    /// <see cref="KnowledgeBlockPicker"/> (already filtered + ordered).
    /// <paramref name="references"/> is expected to come from the host's
    /// AppDomain walker (or any other source that produces the
    /// <see cref="LoadedReference"/> shape) — the builder applies its own
    /// origin filter so callers can hand the full snapshot without
    /// pre-trimming.
    /// </summary>
    public static AssembledPrompt Build(
        IReadOnlyList<KnowledgeBlock> blocks,
        IReadOnlyList<LoadedReference> references,
        string userMessage)
    {
        if (blocks is null) throw new ArgumentNullException(nameof(blocks));
        if (references is null) throw new ArgumentNullException(nameof(references));
        if (userMessage is null) throw new ArgumentNullException(nameof(userMessage));

        var sb = new StringBuilder();
        AppendBaseInstructions(sb);
        AppendKnowledgeSection(sb, blocks);
        AppendLibrarySection(sb, references);

        // Normalise trailing whitespace so downstream comparisons /
        // hashing stay deterministic across edits.
        var system = sb.ToString().TrimEnd('\n', '\r', ' ', '\t');
        return new AssembledPrompt(system, userMessage);
    }

    private static void AppendBaseInstructions(StringBuilder sb)
    {
        // Always-on instructions that frame the response shape. The chat
        // panel renders Markdown; asking explicitly keeps responses
        // consistent across providers (Claude defaults to Markdown but
        // Ollama / OpenAI may not).
        sb.Append("# Response format\n\n");
        sb.Append("Respond in Markdown. Use fenced code blocks with a language tag for any code (```csharp, ```json, ```bash). ");
        sb.Append("Use headings, bullet lists, and tables where they help readability; keep prose tight.\n\n");
    }

    private static void AppendKnowledgeSection(StringBuilder sb, IReadOnlyList<KnowledgeBlock> blocks)
    {
        if (blocks.Count == 0) return;

        sb.Append("# Directing context\n\n");
        sb.Append("The user has authored the following knowledge blocks. They've been picked\n");
        sb.Append("because their `when:` rule matches the current scope. Treat them as\n");
        sb.Append("authoritative project conventions; they override your defaults when in conflict.\n\n");

        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            sb.Append("## ").Append(b.Title);
            sb.Append(" (id=").Append(b.Id);
            if (b.Priority.HasValue) sb.Append(", priority=").Append(b.Priority.Value);
            sb.Append(")\n\n");

            var body = (b.Body ?? string.Empty).TrimEnd('\n', '\r', ' ', '\t');
            if (body.Length > 0)
            {
                sb.Append(body).Append('\n');
            }

            // Separator between blocks, not after the last one.
            if (i < blocks.Count - 1) sb.Append("\n---\n\n");
        }

        sb.Append("\n\n");
    }

    private static void AppendLibrarySection(StringBuilder sb, IReadOnlyList<LoadedReference> references)
    {
        var relevant = references
            .Where(r => r.Origin is LoadedReferenceOrigin.Blast or LoadedReferenceOrigin.External)
            .OrderBy(r => r.Origin)            // Blast first (enum order), then External
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (relevant.Count == 0) return;

        sb.Append("# Available libraries\n\n");
        sb.Append("Loaded assemblies the script can use. Prefer the named PrimaryFacade\n");
        sb.Append("entry points when one fits — they're the canonical front doors.\n\n");

        foreach (var r in relevant)
        {
            sb.Append("## ").Append(r.Name).Append(' ').Append(r.Version).Append('\n');
            if (r.PrimaryFacades.Count > 0)
            {
                sb.Append("- PrimaryFacades: ")
                  .Append(string.Join(", ", r.PrimaryFacades))
                  .Append('\n');
            }
            if (r.Namespaces.Count > 0)
            {
                sb.Append("- Namespaces: ")
                  .Append(string.Join(", ", r.Namespaces))
                  .Append('\n');
            }
            sb.Append('\n');
        }
    }
}
