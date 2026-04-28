using System;
using Xunit;
using System.Collections.Generic;
using AgentBlast.Prompts;
using AgentBlast.Knowledge;

namespace AgentBlast.Tests;

/// <summary>
/// Tests for <see cref="PromptBuilder.Build"/>: section composition,
/// origin filtering on references, ordering, and the empty-context
/// degenerate cases.
/// </summary>
public sealed class PromptBuilderTests
{
    // ─────────────────────────── helpers ───────────────────────────

    private static KnowledgeBlock Block(string id, string title, string body, int? priority = null)
        => new(
            id,
            title,
            body,
            priority,
            Array.Empty<string>(),
            Array.Empty<string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static LoadedReference Ref(
        string name,
        string version,
        LoadedReferenceOrigin origin,
        IReadOnlyList<string>? facades = null,
        IReadOnlyList<string>? namespaces = null)
        => new(
            name, version, Location: $"/fake/{name}.dll", origin,
            PrimaryFacades: facades ?? Array.Empty<string>(),
            Namespaces:     namespaces ?? Array.Empty<string>());

    // ─────────────────────────── empty context ─────────────────────

    [Fact]
    public void NoBlocks_NoReferences_SystemMessageStillCarriesBaseInstructions()
    {
        // Even with no directing context, the response-format header is
        // always present so providers that don't default to Markdown
        // (Ollama / OpenAI) get the same shape as Anthropic.
        var p = PromptBuilder.Build(
            Array.Empty<KnowledgeBlock>(),
            Array.Empty<LoadedReference>(),
            "do the thing");

        Assert.Contains("# Response format", p.SystemMessage);
        Assert.Contains("Respond in Markdown", p.SystemMessage);
        Assert.DoesNotContain("# Directing context", p.SystemMessage);
        Assert.DoesNotContain("# Available libraries", p.SystemMessage);
        Assert.Equal("do the thing", p.UserMessage);
    }

    [Fact]
    public void UserMessage_PassedThroughVerbatim()
    {
        var msg = "Convert these prompts to a form.\n\n```csharp\nvar x = Prompts.Input(\"Name\");\n```";
        var p = PromptBuilder.Build(Array.Empty<KnowledgeBlock>(), Array.Empty<LoadedReference>(), msg);
        Assert.Equal(msg, p.UserMessage);
    }

    // ─────────────────────────── knowledge section ─────────────────

    [Fact]
    public void OneBlock_SystemMessageContainsHeading_TitleAndBody()
    {
        var p = PromptBuilder.Build(
            new[] { Block("house", "House rules", "Always dispose. Never log secrets.\n", priority: 100) },
            Array.Empty<LoadedReference>(),
            "ask");

        Assert.Contains("# Directing context", p.SystemMessage);
        Assert.Contains("## House rules", p.SystemMessage);
        Assert.Contains("(id=house, priority=100)", p.SystemMessage);
        Assert.Contains("Always dispose. Never log secrets.", p.SystemMessage);
    }

    [Fact]
    public void Block_WithoutPriority_OmitsPriorityField()
    {
        var p = PromptBuilder.Build(
            new[] { Block("a", "A", "body\n") },
            Array.Empty<LoadedReference>(),
            "x");

        Assert.Contains("(id=a)", p.SystemMessage);
        Assert.DoesNotContain("priority=", p.SystemMessage);
    }

    [Fact]
    public void MultipleBlocks_PreservePickerOrder_AndAreSeparated()
    {
        var p = PromptBuilder.Build(
            new[]
            {
                Block("first",  "First",  "first body\n"),
                Block("second", "Second", "second body\n"),
                Block("third",  "Third",  "third body\n"),
            },
            Array.Empty<LoadedReference>(),
            "x");

        var idxFirst  = p.SystemMessage.IndexOf("First");
        var idxSecond = p.SystemMessage.IndexOf("Second");
        var idxThird  = p.SystemMessage.IndexOf("Third");

        Assert.True(idxFirst < idxSecond && idxSecond < idxThird,
            "Block order should match the input (which is the picker's chosen order).");
        // Two separators between three blocks, none trailing.
        Assert.Equal(2, CountOccurrences(p.SystemMessage, "\n---\n"));
    }

    // ─────────────────────────── library section ───────────────────

    [Fact]
    public void BlastReference_RendersFacadesAndNamespaces()
    {
        var p = PromptBuilder.Build(
            Array.Empty<KnowledgeBlock>(),
            new[]
            {
                Ref("AzureBlast", "2.1.1", LoadedReferenceOrigin.Blast,
                    facades:    new[] { "AzureBlast.MssqlDatabase", "AzureBlast.AzureServiceBus" },
                    namespaces: new[] { "AzureBlast", "AzureBlast.Mssql" }),
            },
            "x");

        Assert.Contains("# Available libraries", p.SystemMessage);
        Assert.Contains("## AzureBlast 2.1.1", p.SystemMessage);
        Assert.Contains("PrimaryFacades: AzureBlast.MssqlDatabase, AzureBlast.AzureServiceBus", p.SystemMessage);
        Assert.Contains("Namespaces: AzureBlast, AzureBlast.Mssql", p.SystemMessage);
    }

    [Fact]
    public void ExternalReference_RendersWithoutFacadeLine_WhenNoneStamped()
    {
        var p = PromptBuilder.Build(
            Array.Empty<KnowledgeBlock>(),
            new[]
            {
                Ref("Acme.Domain", "1.0.0", LoadedReferenceOrigin.External,
                    namespaces: new[] { "Acme.Domain" }),
            },
            "x");

        Assert.Contains("## Acme.Domain 1.0.0", p.SystemMessage);
        Assert.Contains("Namespaces: Acme.Domain", p.SystemMessage);
        Assert.DoesNotContain("PrimaryFacades:", p.SystemMessage);
    }

    [Fact]
    public void FrameworkAndApplication_References_AreFilteredOut()
    {
        var p = PromptBuilder.Build(
            Array.Empty<KnowledgeBlock>(),
            new[]
            {
                Ref("System.Runtime", "10.0.0", LoadedReferenceOrigin.Framework, namespaces: new[] { "System" }),
                Ref("TaskBlaster",    "1.2.0",  LoadedReferenceOrigin.Application,
                    namespaces: new[] { "TaskBlaster" }),
                Ref("Random.Other",   "0.0.1",  LoadedReferenceOrigin.Other,    namespaces: new[] { "Random" }),
                Ref("AzureBlast",     "2.1.1",  LoadedReferenceOrigin.Blast,    namespaces: new[] { "AzureBlast" }),
            },
            "x");

        Assert.DoesNotContain("System.Runtime", p.SystemMessage);
        Assert.DoesNotContain("TaskBlaster", p.SystemMessage);
        Assert.DoesNotContain("Random.Other", p.SystemMessage);
        Assert.Contains("AzureBlast", p.SystemMessage);
    }

    [Fact]
    public void OnlyFilteredOutReferences_NoLibrarySectionEmitted()
    {
        var p = PromptBuilder.Build(
            Array.Empty<KnowledgeBlock>(),
            new[]
            {
                Ref("System.Runtime", "10.0.0", LoadedReferenceOrigin.Framework),
                Ref("TaskBlaster",    "1.2.0",  LoadedReferenceOrigin.Application),
            },
            "x");

        // Only the base "response format" instruction survives — no
        // directing context, no library section.
        Assert.DoesNotContain("# Available libraries", p.SystemMessage);
        Assert.DoesNotContain("# Directing context", p.SystemMessage);
    }

    [Fact]
    public void LibraryOrder_BlastBeforeExternal_AlphabeticalWithinOrigin()
    {
        var p = PromptBuilder.Build(
            Array.Empty<KnowledgeBlock>(),
            new[]
            {
                Ref("Acme.Domain",  "1.0.0", LoadedReferenceOrigin.External, namespaces: new[] { "Acme.Domain" }),
                Ref("UtilBlast",    "1.2.1", LoadedReferenceOrigin.Blast,    namespaces: new[] { "UtilBlast" }),
                Ref("AzureBlast",   "2.1.1", LoadedReferenceOrigin.Blast,    namespaces: new[] { "AzureBlast" }),
                Ref("Other.Models", "0.5.0", LoadedReferenceOrigin.External, namespaces: new[] { "Other.Models" }),
            },
            "x");

        var idxAzure  = p.SystemMessage.IndexOf("## AzureBlast");
        var idxUtil   = p.SystemMessage.IndexOf("## UtilBlast");
        var idxAcme   = p.SystemMessage.IndexOf("## Acme.Domain");
        var idxOther  = p.SystemMessage.IndexOf("## Other.Models");

        Assert.True(idxAzure < idxUtil,  "Blast entries should be alphabetical");
        Assert.True(idxUtil  < idxAcme,  "Blast section should precede External");
        Assert.True(idxAcme  < idxOther, "External entries should be alphabetical");
    }

    // ─────────────────────────── combined ──────────────────────────

    [Fact]
    public void BothSections_AppearInOrder_KnowledgeBeforeLibrary()
    {
        var p = PromptBuilder.Build(
            new[] { Block("a", "A", "body\n") },
            new[] { Ref("AzureBlast", "2.1.1", LoadedReferenceOrigin.Blast, namespaces: new[] { "AzureBlast" }) },
            "x");

        var idxKnow = p.SystemMessage.IndexOf("# Directing context");
        var idxLibs = p.SystemMessage.IndexOf("# Available libraries");
        Assert.True(idxKnow >= 0 && idxLibs > idxKnow);
    }

    // ─────────────────────────── argument validation ───────────────

    [Fact]
    public void NullBlocks_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PromptBuilder.Build(null!, Array.Empty<LoadedReference>(), "x"));
    }

    [Fact]
    public void NullReferences_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PromptBuilder.Build(Array.Empty<KnowledgeBlock>(), null!, "x"));
    }

    [Fact]
    public void NullUserMessage_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PromptBuilder.Build(Array.Empty<KnowledgeBlock>(), Array.Empty<LoadedReference>(), null!));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
