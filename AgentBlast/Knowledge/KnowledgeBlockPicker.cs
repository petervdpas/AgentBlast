using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentBlast.Knowledge;

/// <summary>
/// Pure function: given a knowledge library and a <see cref="PickerContext"/>,
/// return the subset of blocks that apply, in the order the prompt
/// builder should emit them.
///
/// Algorithm:
/// <list type="number">
///   <item>Find entry points — blocks whose <c>when:</c> rule matches the context.</item>
///   <item>Expand transitively via <c>includes:</c> with cycle detection.</item>
///   <item>Sort: <see cref="KnowledgeBlock.Priority"/> descending (null = least),
///         then <see cref="KnowledgeBlock.Title"/> ascending for stable ordering.</item>
/// </list>
///
/// <para>
/// <b>When-rule grammar (v1):</b> <c>when:</c> is a comma-separated list of
/// rules; ANY rule matching triggers the block. Each rule is one of:
/// <list type="bullet">
///   <item><c>always</c> — always matches.</item>
///   <item><c>tag:foo</c> — matches when <see cref="PickerContext.Tags"/> contains <c>foo</c> (case-insensitive).</item>
///   <item><c>Namespace.Type</c> — matches when an exact loaded type FQN equals the rule.</item>
///   <item><c>Namespace</c> (no dot, or dotted) — matches when a loaded namespace equals the rule, or any loaded FQN starts with the rule plus a dot.</item>
/// </list>
/// Blocks with no <c>when:</c> are never picked as entry points; they only
/// appear in the result via another block's <c>includes:</c>. That gives
/// the user an explicit "off-by-default, only pulled in when referenced"
/// shape for shared/base blocks.
/// </para>
/// </summary>
public static class KnowledgeBlockPicker
{
    /// <summary>Pick the relevant blocks for the given context (block-only view).</summary>
    public static IReadOnlyList<KnowledgeBlock> Pick(
        IEnumerable<KnowledgeBlock> blocks,
        PickerContext context)
        => PickWithReasons(blocks, context).Select(p => p.Block).ToList();

    /// <summary>
    /// Pick the relevant blocks plus a human-readable reason per block
    /// (the rule that fired, or the parent block that pulled it in) and
    /// the <see cref="InclusionMode"/> that drives prompt rendering.
    /// Used by the Preview button and the future audit log; the
    /// non-reason <see cref="Pick"/> is a thin wrapper.
    /// </summary>
    public static IReadOnlyList<PickedBlock> PickWithReasons(
        IEnumerable<KnowledgeBlock> blocks,
        PickerContext context)
    {
        if (blocks is null) throw new ArgumentNullException(nameof(blocks));
        if (context is null) throw new ArgumentNullException(nameof(context));

        var byId = new Dictionary<string, KnowledgeBlock>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in blocks)
        {
            // Last-write-wins on duplicate ids; the store wouldn't produce
            // duplicates today, but defending against pathological input
            // keeps the picker safe to call from anywhere.
            byId[b.Id] = b;
        }

        var pickedReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pickedBlocks = new Dictionary<string, KnowledgeBlock>(StringComparer.OrdinalIgnoreCase);
        var triggeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: seed directly-triggered entries. Done first (and without
        // recursion) so direct matches always win over transitive includes —
        // pass 2 uses TryAdd, so an already-seeded triggered block won't
        // have its reason or mode clobbered by a later "included via" walk.
        foreach (var entry in byId.Values)
        {
            var entryMatch = MatchReason(entry, context);
            if (entryMatch is null) continue;
            triggeredIds.Add(entry.Id);
            pickedBlocks[entry.Id] = entry;
            pickedReasons[entry.Id] = $"matched {entryMatch}";
        }

        // Pass 2: expand each triggered entry's includes graph. Cycle
        // safety still comes from TryAdd in ExpandIncludes.
        foreach (var entryId in triggeredIds)
        {
            var entry = byId[entryId];
            foreach (var includeId in entry.Includes)
            {
                if (string.IsNullOrWhiteSpace(includeId)) continue;
                if (byId.TryGetValue(includeId, out var sub))
                    ExpandIncludes(sub, parentReason: $"included via '{entry.Id}'", byId, pickedBlocks, pickedReasons);
            }
        }

        return pickedBlocks.Values
            .OrderByDescending(b => b.Priority ?? int.MinValue)
            .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
            .Select(b => new PickedBlock(
                b,
                pickedReasons[b.Id],
                triggeredIds.Contains(b.Id) ? InclusionMode.Triggered : InclusionMode.Available))
            .ToList();
    }

    private static void ExpandIncludes(
        KnowledgeBlock block,
        string parentReason,
        IReadOnlyDictionary<string, KnowledgeBlock> byId,
        Dictionary<string, KnowledgeBlock> pickedBlocks,
        Dictionary<string, string> pickedReasons)
    {
        // TryAdd doubles as the visited set: a block already in `pickedBlocks`
        // is either a real prior pick or a node in the current DFS — either
        // way we stop, which makes cycles safe (A includes B includes A).
        if (!pickedBlocks.TryAdd(block.Id, block)) return;
        pickedReasons[block.Id] = parentReason;

        foreach (var includeId in block.Includes)
        {
            if (string.IsNullOrWhiteSpace(includeId)) continue;
            if (byId.TryGetValue(includeId, out var sub))
                ExpandIncludes(sub, parentReason: $"included via '{block.Id}'", byId, pickedBlocks, pickedReasons);
            // Dangling include id — silently dropped for now. A later
            // diagnostic pass could surface these in the audit panel.
        }
    }

    /// <summary>
    /// Returns the rule string that triggered the match (e.g. <c>"always"</c>,
    /// <c>"tag:db"</c>, <c>"AzureBlast.MssqlDatabase"</c>) or null if no
    /// rule on this block matched. Used to author the reason text.
    /// </summary>
    private static string? MatchReason(KnowledgeBlock block, PickerContext context)
    {
        if (!block.Frontmatter.TryGetValue("when", out var whenRaw)) return null;
        if (string.IsNullOrWhiteSpace(whenRaw)) return null;

        foreach (var ruleRaw in whenRaw.Split(','))
        {
            var rule = ruleRaw.Trim();
            if (rule.Length == 0) continue;
            if (RuleMatches(rule, context)) return $"'{rule}'";
        }
        return null;
    }

    private static bool RuleMatches(string rule, PickerContext context)
    {
        if (string.Equals(rule, "always", StringComparison.OrdinalIgnoreCase)) return true;

        if (rule.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
        {
            var tag = rule[4..].Trim();
            if (tag.Length == 0) return false;
            foreach (var t in context.Tags)
                if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Type-name / namespace match. Comparison is case-insensitive even
        // though .NET names aren't — users hand-write these, typos are the
        // common case, and false positives here are cheap (a slightly-wrong
        // block is just unhelpful, not dangerous).
        if (Contains(context.LoadedTypeFqns, rule)) return true;
        if (Contains(context.LoadedNamespaces, rule)) return true;
        foreach (var fqn in context.LoadedTypeFqns)
            if (fqn.StartsWith(rule + ".", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool Contains(IReadOnlySet<string> set, string value)
    {
        foreach (var s in set)
            if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
