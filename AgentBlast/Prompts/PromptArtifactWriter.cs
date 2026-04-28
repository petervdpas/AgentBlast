using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AgentBlast.Knowledge;

namespace AgentBlast.Prompts;

/// <summary>
/// Persists what the picker + builder produced for one AI interaction
/// (preview today, real call tomorrow). The artifact is a single
/// markdown file with YAML-style frontmatter + readable body so the
/// user can open it in any editor and replay "what would the model
/// have seen?" later. Files live under <see cref="Folder"/>; filenames
/// sort chronologically.
///
/// <para>
/// The shape is deliberately symmetric with knowledge blocks (same
/// frontmatter convention) — same parser will read it later when we
/// build a "history" view.
/// </para>
/// </summary>
public sealed class PromptArtifactWriter
{
    /// <summary>The folder artifacts are written to.</summary>
    public string Folder { get; }

    /// <summary>Build a writer anchored on <paramref name="folder"/>. The folder is created on demand by <see cref="Write"/>; nothing happens at construction time.</summary>
    public PromptArtifactWriter(string folder)
    {
        Folder = folder ?? throw new ArgumentNullException(nameof(folder));
    }

    /// <summary>
    /// Write one artifact and return the full path. Creates
    /// <see cref="Folder"/> on demand. <paramref name="kind"/> distinguishes
    /// preview vs ai-call (and future kinds — the value is just suffixed
    /// onto the filename and surfaced in the frontmatter).
    /// </summary>
    public string Write(
        string kind,
        PickerContext context,
        IReadOnlyList<LoadedReference> references,
        IReadOnlyList<PickedBlock> picked,
        AssembledPrompt prompt,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("kind is required.", nameof(kind));
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (references is null) throw new ArgumentNullException(nameof(references));
        if (picked is null) throw new ArgumentNullException(nameof(picked));
        if (prompt is null) throw new ArgumentNullException(nameof(prompt));

        Directory.CreateDirectory(Folder);
        var stamp = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var fileStamp = stamp.ToString("yyyy-MM-ddTHHmmssZ");
        var path = Path.Combine(Folder, $"{fileStamp}-{SanitizeKind(kind)}.md");

        File.WriteAllText(path, Render(kind, stamp, context, references, picked, prompt));
        return path;
    }

    private static string Render(
        string kind,
        DateTimeOffset stamp,
        PickerContext context,
        IReadOnlyList<LoadedReference> references,
        IReadOnlyList<PickedBlock> picked,
        AssembledPrompt prompt)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("kind: ").Append(kind).Append('\n');
        sb.Append("generated: ").Append(stamp.ToString("yyyy-MM-ddTHH:mm:ssZ")).Append('\n');
        sb.Append("loaded-types: ").Append(context.LoadedTypeFqns.Count).Append('\n');
        sb.Append("loaded-namespaces: ").Append(context.LoadedNamespaces.Count).Append('\n');
        sb.Append("tags: ").Append(string.Join(", ", context.Tags)).Append('\n');
        AppendReferenceSummary(sb, "references-blast",    references, LoadedReferenceOrigin.Blast);
        AppendReferenceSummary(sb, "references-external", references, LoadedReferenceOrigin.External);
        sb.Append("picked: ").Append(string.Join(", ", picked.Select(p => p.Block.Id))).Append('\n');
        sb.Append("---\n\n");

        if (picked.Count > 0)
        {
            sb.Append("# Picked blocks\n\n");
            foreach (var p in picked)
            {
                sb.Append("- **").Append(p.Block.Id).Append("** — ").Append(p.Reason).Append('\n');
            }
            sb.Append('\n');
        }
        else
        {
            sb.Append("# Picked blocks\n\n_(none — no block's `when:` rule matched the current context)_\n\n");
        }

        sb.Append("# System message\n\n");
        if (prompt.SystemMessage.Length > 0)
        {
            sb.Append(prompt.SystemMessage).Append('\n');
        }
        else
        {
            sb.Append("_(empty)_\n");
        }
        sb.Append('\n');

        sb.Append("# User message\n\n");
        if (prompt.UserMessage.Length > 0)
        {
            sb.Append(prompt.UserMessage).Append('\n');
        }
        else
        {
            sb.Append("_(none — preview only)_\n");
        }

        return sb.ToString();
    }

    private static void AppendReferenceSummary(
        StringBuilder sb, string key,
        IReadOnlyList<LoadedReference> refs, LoadedReferenceOrigin origin)
    {
        var entries = refs
            .Where(r => r.Origin == origin)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => $"{r.Name} {r.Version}");
        sb.Append(key).Append(": ").Append(string.Join(", ", entries)).Append('\n');
    }

    private static string SanitizeKind(string kind)
    {
        // Filename-safe: lowercase, replace anything outside [a-z0-9-] with '-'.
        var sb = new StringBuilder(kind.Length);
        foreach (var c in kind.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '-');
        return sb.ToString();
    }
}
