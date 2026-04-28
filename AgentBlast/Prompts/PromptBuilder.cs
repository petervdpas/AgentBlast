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
    /// Build the prompt from raw <see cref="KnowledgeBlock"/>s. Every
    /// block is treated as <see cref="InclusionMode.Triggered"/>, i.e.
    /// rendered fully — back-compat shim for callers that don't go
    /// through <see cref="KnowledgeBlockPicker.PickWithReasons"/> and
    /// therefore have no per-block mode information. Prefer the
    /// <see cref="PickedBlock"/> overload so transitively-included
    /// blocks render compactly.
    /// </summary>
    public static AssembledPrompt Build(
        IReadOnlyList<KnowledgeBlock> blocks,
        IReadOnlyList<LoadedReference> references,
        string userMessage)
    {
        if (blocks is null) throw new ArgumentNullException(nameof(blocks));
        var picked = blocks
            .Select(b => new PickedBlock(b, string.Empty, InclusionMode.Triggered))
            .ToList();
        return Build(picked, references, userMessage);
    }

    /// <summary>
    /// Build the prompt. <paramref name="blocks"/> is expected to come from
    /// <see cref="KnowledgeBlockPicker.PickWithReasons"/> (already filtered
    /// + ordered, with per-block <see cref="InclusionMode"/>).
    /// <paramref name="references"/> is expected to come from the host's
    /// AppDomain walker (or any other source that produces the
    /// <see cref="LoadedReference"/> shape) — the builder applies its own
    /// origin filter so callers can hand the full snapshot without
    /// pre-trimming.
    ///
    /// <para>
    /// <see cref="InclusionMode.Triggered"/> blocks render fully (heading
    /// + body); <see cref="InclusionMode.Available"/> blocks render in a
    /// trailing "Also available (reference-only)" section as one-liners,
    /// so the agent knows they exist without paying the token cost of
    /// the full body.
    /// </para>
    /// </summary>
    public static AssembledPrompt Build(
        IReadOnlyList<PickedBlock> blocks,
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

    private static void AppendKnowledgeSection(StringBuilder sb, IReadOnlyList<PickedBlock> blocks)
    {
        if (blocks.Count == 0) return;

        // Split by mode while preserving the picker's input order within
        // each group. The picker already sorts by Priority desc / Title
        // asc, so each list comes out in that order.
        var triggered = new List<KnowledgeBlock>();
        var available = new List<PickedBlock>();
        foreach (var p in blocks)
        {
            if (p.Mode == InclusionMode.Triggered) triggered.Add(p.Block);
            else available.Add(p);
        }

        sb.Append("# Directing context\n\n");
        sb.Append("The user has authored the following knowledge blocks. They've been picked\n");
        sb.Append("because their `when:` rule matches the current scope. Treat them as\n");
        sb.Append("authoritative project conventions; they override your defaults when in conflict.\n\n");

        for (var i = 0; i < triggered.Count; i++)
        {
            var b = triggered[i];
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
            if (i < triggered.Count - 1) sb.Append("\n---\n\n");
        }

        if (available.Count > 0)
        {
            // Separator between the Triggered tail and the Available
            // section, only when there's a Triggered tail to separate from.
            if (triggered.Count > 0) sb.Append("\n---\n\n");

            sb.Append("### Also available (reference-only)\n\n");
            sb.Append("These blocks were pulled in transitively but their own `when:` rule didn't\n");
            sb.Append("match. Their bodies are omitted to save tokens; ask if you need the content.\n\n");

            foreach (var p in available)
            {
                sb.Append("- ").Append(p.Block.Title)
                  .Append(" (id=").Append(p.Block.Id).Append(')');

                if (!string.IsNullOrWhiteSpace(p.Reason))
                    sb.Append(" — ").Append(p.Reason);

                sb.Append('\n');
            }
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
