using System;
using Xunit;
using System.Collections.Generic;
using System.Linq;
using AgentBlast.Knowledge;

namespace AgentBlast.Tests;

/// <summary>
/// Tests for the picker: which blocks get included for a given context,
/// in what order, and how the includes graph behaves under cycles /
/// dangling refs.
/// </summary>
public sealed class KnowledgeBlockPickerTests
{
    // ─────────────────────────── helpers ───────────────────────────

    private static KnowledgeBlock Block(
        string id,
        string? when = null,
        int? priority = null,
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<string>? includes = null,
        string? title = null)
    {
        var fm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (when is not null) fm["when"] = when;
        return new KnowledgeBlock(
            id,
            title ?? KnowledgeBlockStore.Humanise(id),
            $"body of {id}\n",
            priority,
            tags ?? Array.Empty<string>(),
            includes ?? Array.Empty<string>(),
            fm);
    }

    private static PickerContext Ctx(
        IEnumerable<string>? types = null,
        IEnumerable<string>? namespaces = null,
        IEnumerable<string>? tags = null) => new(
            new HashSet<string>(types ?? Array.Empty<string>(), StringComparer.Ordinal),
            new HashSet<string>(namespaces ?? Array.Empty<string>(), StringComparer.Ordinal),
            (tags ?? Array.Empty<string>()).ToList());

    // ─────────────────────────── basics ────────────────────────────

    [Fact]
    public void NoBlocks_ReturnsEmpty()
    {
        var picked = KnowledgeBlockPicker.Pick(Array.Empty<KnowledgeBlock>(), PickerContext.Empty);
        Assert.Empty(picked);
    }

    [Fact]
    public void NoWhen_NeverPickedAsEntryPoint()
    {
        var picked = KnowledgeBlockPicker.Pick(new[] { Block("nowhen") }, PickerContext.Empty);
        Assert.Empty(picked);
    }

    [Fact]
    public void EmptyWhen_NeverPickedAsEntryPoint()
    {
        var picked = KnowledgeBlockPicker.Pick(new[] { Block("empty", when: "   ") }, PickerContext.Empty);
        Assert.Empty(picked);
    }

    [Fact]
    public void WhenAlways_PickedRegardlessOfContext()
    {
        var picked = KnowledgeBlockPicker.Pick(new[] { Block("base", when: "always") }, PickerContext.Empty);
        Assert.Single(picked);
        Assert.Equal("base", picked[0].Id);
    }

    [Fact]
    public void WhenAlways_IsCaseInsensitive()
    {
        var picked = KnowledgeBlockPicker.Pick(new[] { Block("b", when: "Always") }, PickerContext.Empty);
        Assert.Single(picked);
    }

    // ─────────────────────────── tag rules ─────────────────────────

    [Fact]
    public void TagRule_MatchesContextTag()
    {
        var picked = KnowledgeBlockPicker.Pick(
            new[] { Block("db-block", when: "tag:db") },
            Ctx(tags: new[] { "db" }));
        Assert.Single(picked);
    }

    [Fact]
    public void TagRule_DoesNotMatchAbsentTag()
    {
        var picked = KnowledgeBlockPicker.Pick(
            new[] { Block("db-block", when: "tag:db") },
            Ctx(tags: new[] { "ui" }));
        Assert.Empty(picked);
    }

    [Fact]
    public void TagRule_IsCaseInsensitive()
    {
        var picked = KnowledgeBlockPicker.Pick(
            new[] { Block("b", when: "tag:DB") },
            Ctx(tags: new[] { "db" }));
        Assert.Single(picked);
    }

    [Fact]
    public void TagRule_EmptyTagAfterColon_DoesNotMatch()
    {
        var picked = KnowledgeBlockPicker.Pick(
            new[] { Block("b", when: "tag:") },
            Ctx(tags: new[] { "db" }));
        Assert.Empty(picked);
    }

    // ─────────────────────── type / namespace rules ────────────────

    [Fact]
    public void NamespaceRule_MatchesLoadedNamespaceExact()
    {
        var picked = KnowledgeBlockPicker.Pick(
            new[] { Block("a", when: "Acme.Domain") },
            Ctx(namespaces: new[] { "Acme.Domain" }));
        Assert.Single(picked);
    }

    [Fact]
    public void NamespaceRule_MatchesAsPrefixOfLoadedFqn()
    {
        // "AzureBlast" should match because loaded type "AzureBlast.MssqlDatabase" starts with "AzureBlast."
        var picked = KnowledgeBlockPicker.Pick(
            new[] { Block("a", when: "AzureBlast") },
            Ctx(types: new[] { "AzureBlast.MssqlDatabase" }));
        Assert.Single(picked);
    }

    [Fact]
    public void FqnRule_MatchesLoadedTypeExact()
    {
        var picked = KnowledgeBlockPicker.Pick(
            new[] { Block("a", when: "AzureBlast.MssqlDatabase") },
            Ctx(types: new[] { "AzureBlast.MssqlDatabase" }));
        Assert.Single(picked);
    }

    [Fact]
    public void FqnRule_DoesNotMatchSiblingType()
    {
        var picked = KnowledgeBlockPicker.Pick(
            new[] { Block("a", when: "AzureBlast.MssqlDatabase") },
            Ctx(types: new[] { "AzureBlast.AzureServiceBus" }));
        Assert.Empty(picked);
    }

    [Fact]
    public void FqnRule_PrefixMatchOnlyAtSegmentBoundary()
    {
        // Rule "Acme" must NOT match "AcmeOther.Thing" — segment boundary required.
        var picked = KnowledgeBlockPicker.Pick(
            new[] { Block("a", when: "Acme") },
            Ctx(types: new[] { "AcmeOther.Thing" }));
        Assert.Empty(picked);
    }

    [Fact]
    public void TypeMatching_IsCaseInsensitive()
    {
        var picked = KnowledgeBlockPicker.Pick(
            new[] { Block("a", when: "azureblast.MSSQLDATABASE") },
            Ctx(types: new[] { "AzureBlast.MssqlDatabase" }));
        Assert.Single(picked);
    }

    // ─────────────────────── multiple rules per block ──────────────

    [Fact]
    public void CommaSeparatedRules_AnyMatchTriggers()
    {
        var picked = KnowledgeBlockPicker.Pick(
            new[] { Block("a", when: "AzureBlast.MssqlDatabase, tag:db") },
            Ctx(tags: new[] { "db" }));
        Assert.Single(picked);
    }

    // ─────────────────────── includes graph ────────────────────────

    [Fact]
    public void Includes_PullInTransitiveBlock()
    {
        var entry = Block("entry", when: "always", includes: new[] { "base" });
        var basis = Block("base"); // no when:, only pulled in transitively

        var picked = KnowledgeBlockPicker.Pick(new[] { entry, basis }, PickerContext.Empty);

        Assert.Equal(2, picked.Count);
        Assert.Contains(picked, b => b.Id == "entry");
        Assert.Contains(picked, b => b.Id == "base");
    }

    [Fact]
    public void Includes_AreTransitive()
    {
        var a = Block("a", when: "always", includes: new[] { "b" });
        var b = Block("b", includes: new[] { "c" });
        var c = Block("c");

        var ids = KnowledgeBlockPicker.Pick(new[] { a, b, c }, PickerContext.Empty)
            .Select(x => x.Id).ToHashSet();

        Assert.Contains("a", ids);
        Assert.Contains("b", ids);
        Assert.Contains("c", ids);
    }

    [Fact]
    public void Includes_CyclesAreSafe()
    {
        // a -> b -> a cycle: must terminate, must include both once.
        var a = Block("a", when: "always", includes: new[] { "b" });
        var b = Block("b", includes: new[] { "a" });

        var picked = KnowledgeBlockPicker.Pick(new[] { a, b }, PickerContext.Empty);
        Assert.Equal(2, picked.Count);
    }

    [Fact]
    public void Includes_DanglingIdSilentlyDropped()
    {
        var a = Block("a", when: "always", includes: new[] { "ghost" });

        var picked = KnowledgeBlockPicker.Pick(new[] { a }, PickerContext.Empty);
        Assert.Single(picked);
        Assert.Equal("a", picked[0].Id);
    }

    [Fact]
    public void Includes_BlockNotPulledInWhenItsParentIsnt()
    {
        // base only ever gets in via "entry", but entry's when: doesn't match.
        var entry = Block("entry", when: "tag:nope", includes: new[] { "base" });
        var basis = Block("base");

        var picked = KnowledgeBlockPicker.Pick(new[] { entry, basis }, PickerContext.Empty);
        Assert.Empty(picked);
    }

    [Fact]
    public void DuplicateIncludeAcrossEntries_AppearsOnce()
    {
        var a = Block("a", when: "always", includes: new[] { "shared" });
        var b = Block("b", when: "always", includes: new[] { "shared" });
        var shared = Block("shared");

        var picked = KnowledgeBlockPicker.Pick(new[] { a, b, shared }, PickerContext.Empty);
        Assert.Equal(3, picked.Count);
    }

    // ─────────────────────── ordering ──────────────────────────────

    [Fact]
    public void Output_OrderedByPriorityDescending()
    {
        var lo = Block("lo",  when: "always", priority: 1);
        var hi = Block("hi",  when: "always", priority: 9);
        var mid = Block("mid", when: "always", priority: 5);

        var ids = KnowledgeBlockPicker.Pick(new[] { lo, hi, mid }, PickerContext.Empty)
            .Select(b => b.Id).ToList();

        Assert.Equal(new[] { "hi", "mid", "lo" }, ids);
    }

    [Fact]
    public void NullPriority_OrderedAfterAnyExplicitPriority()
    {
        var p = Block("priored", when: "always", priority: 1);
        var n = Block("nullp",   when: "always");

        var ids = KnowledgeBlockPicker.Pick(new[] { p, n }, PickerContext.Empty)
            .Select(b => b.Id).ToList();

        Assert.Equal(new[] { "priored", "nullp" }, ids);
    }

    [Fact]
    public void TiePriority_FallsBackToTitleAscending()
    {
        var z = Block("z", when: "always", priority: 5, title: "Zebra");
        var a = Block("a", when: "always", priority: 5, title: "Apple");

        var ids = KnowledgeBlockPicker.Pick(new[] { z, a }, PickerContext.Empty)
            .Select(b => b.Id).ToList();

        Assert.Equal(new[] { "a", "z" }, ids);
    }

    // ─────────────────────── argument validation ───────────────────

    [Fact]
    public void Pick_NullBlocks_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            KnowledgeBlockPicker.Pick(null!, PickerContext.Empty));
    }

    [Fact]
    public void Pick_NullContext_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            KnowledgeBlockPicker.Pick(Array.Empty<KnowledgeBlock>(), null!));
    }

    // ─────────────────────── reasons ───────────────────────────────

    [Fact]
    public void PickWithReasons_DirectMatch_HasMatchedReason()
    {
        var picked = KnowledgeBlockPicker.PickWithReasons(
            new[] { Block("a", when: "always") },
            PickerContext.Empty);

        Assert.Single(picked);
        Assert.Equal("a", picked[0].Block.Id);
        Assert.Contains("matched", picked[0].Reason);
        Assert.Contains("always", picked[0].Reason);
    }

    [Fact]
    public void PickWithReasons_TransitiveInclude_NamesParent()
    {
        var entry = Block("entry", when: "always", includes: new[] { "base" });
        var basis = Block("base");

        var picked = KnowledgeBlockPicker.PickWithReasons(new[] { entry, basis }, PickerContext.Empty);
        var baseReason = picked.Single(p => p.Block.Id == "base").Reason;

        Assert.Contains("included via", baseReason);
        Assert.Contains("entry", baseReason);
    }

    [Fact]
    public void PickWithReasons_FqnMatch_QuotesTheRule()
    {
        var picked = KnowledgeBlockPicker.PickWithReasons(
            new[] { Block("a", when: "AzureBlast.MssqlDatabase") },
            Ctx(types: new[] { "AzureBlast.MssqlDatabase" }));

        Assert.Contains("AzureBlast.MssqlDatabase", picked[0].Reason);
    }

    // ─────────────────────── inclusion mode ────────────────────────

    [Fact]
    public void Mode_DirectEntry_IsTriggered()
    {
        var picked = KnowledgeBlockPicker.PickWithReasons(
            new[] { Block("a", when: "always") },
            PickerContext.Empty);

        Assert.Single(picked);
        Assert.Equal(InclusionMode.Triggered, picked[0].Mode);
    }

    [Fact]
    public void Mode_TransitiveInclude_IsAvailable()
    {
        var entry = Block("entry", when: "always", includes: new[] { "base" });
        var basis = Block("base"); // no when:, only pulled in transitively

        var picked = KnowledgeBlockPicker.PickWithReasons(new[] { entry, basis }, PickerContext.Empty);

        Assert.Equal(InclusionMode.Triggered, picked.Single(p => p.Block.Id == "entry").Mode);
        Assert.Equal(InclusionMode.Available, picked.Single(p => p.Block.Id == "base").Mode);
    }

    [Fact]
    public void Mode_BothDirectAndTransitive_DirectWins()
    {
        // 'shared' has its own when: rule (matches) AND is included by 'entry'
        // (which also matches). Direct match must win regardless of iteration
        // order — ordering by id ensures we hit 'entry' first if iteration is
        // alphabetical, but the result must still be Triggered.
        var entry  = Block("entry",  when: "always", includes: new[] { "shared" });
        var shared = Block("shared", when: "always");

        var picked = KnowledgeBlockPicker.PickWithReasons(new[] { entry, shared }, PickerContext.Empty);

        Assert.Equal(InclusionMode.Triggered, picked.Single(p => p.Block.Id == "shared").Mode);
        Assert.Contains("matched", picked.Single(p => p.Block.Id == "shared").Reason);
    }

    [Fact]
    public void Mode_BothDirectAndTransitive_DirectWins_RegardlessOfInputOrder()
    {
        // Same as above but with reversed input order — direct match must
        // still win even if the picker would otherwise have hit the include
        // path first via 'entry'.
        var entry  = Block("entry",  when: "always", includes: new[] { "shared" });
        var shared = Block("shared", when: "always");

        var picked = KnowledgeBlockPicker.PickWithReasons(new[] { shared, entry }, PickerContext.Empty);

        Assert.Equal(InclusionMode.Triggered, picked.Single(p => p.Block.Id == "shared").Mode);
    }

    [Fact]
    public void Mode_IncludedFromMultipleEntries_StaysAvailable()
    {
        // 'shared' has no when:, gets pulled in by both 'a' and 'b'. It's
        // never directly triggered, so Mode is Available.
        var a      = Block("a",      when: "always", includes: new[] { "shared" });
        var b      = Block("b",      when: "always", includes: new[] { "shared" });
        var shared = Block("shared");

        var picked = KnowledgeBlockPicker.PickWithReasons(new[] { a, b, shared }, PickerContext.Empty);

        Assert.Equal(InclusionMode.Triggered, picked.Single(p => p.Block.Id == "a").Mode);
        Assert.Equal(InclusionMode.Triggered, picked.Single(p => p.Block.Id == "b").Mode);
        Assert.Equal(InclusionMode.Available, picked.Single(p => p.Block.Id == "shared").Mode);
    }

    [Fact]
    public void Mode_TransitivelyIncludedThroughChain_AllAvailable()
    {
        // Only 'a' is directly triggered. b and c come along via the chain
        // a -> b -> c; both should be Available, not just the leaf.
        var a = Block("a", when: "always", includes: new[] { "b" });
        var b = Block("b", includes: new[] { "c" });
        var c = Block("c");

        var picked = KnowledgeBlockPicker.PickWithReasons(new[] { a, b, c }, PickerContext.Empty);

        Assert.Equal(InclusionMode.Triggered, picked.Single(p => p.Block.Id == "a").Mode);
        Assert.Equal(InclusionMode.Available, picked.Single(p => p.Block.Id == "b").Mode);
        Assert.Equal(InclusionMode.Available, picked.Single(p => p.Block.Id == "c").Mode);
    }
}
