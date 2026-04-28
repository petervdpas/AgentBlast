using System;
using Xunit;
using System.Collections.Generic;
using System.IO;
using AgentBlast.Prompts;
using AgentBlast.Knowledge;

namespace AgentBlast.Tests;

/// <summary>
/// Tests for <see cref="PromptArtifactWriter"/>: file naming, frontmatter
/// shape, picked-block list, and the empty-context path.
/// </summary>
public sealed class PromptArtifactWriterTests : IDisposable
{
    private readonly string _temp;

    public PromptArtifactWriterTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "tb-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    private static KnowledgeBlock Block(string id, string title, string body, int? priority = null)
        => new(id, title, body, priority,
            Array.Empty<string>(), Array.Empty<string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Write_CreatesFile_WithKindAndTimestampInName()
    {
        var w = new PromptArtifactWriter(_temp);
        var path = w.Write(
            "preview",
            PickerContext.Empty,
            Array.Empty<LoadedReference>(),
            Array.Empty<PickedBlock>(),
            new AssembledPrompt(string.Empty, "ask"),
            now: new DateTimeOffset(2026, 4, 27, 14, 23, 0, TimeSpan.Zero));

        Assert.True(File.Exists(path));
        Assert.EndsWith("2026-04-27T142300Z-preview.md", path);
    }

    [Fact]
    public void Write_FrontmatterCarriesKindGeneratedAndCounts()
    {
        var w = new PromptArtifactWriter(_temp);
        var ctx = new PickerContext(
            new HashSet<string>(new[] { "AzureBlast.MssqlDatabase" }, StringComparer.Ordinal),
            new HashSet<string>(new[] { "AzureBlast" }, StringComparer.Ordinal),
            new[] { "db" });

        var path = w.Write("preview", ctx, Array.Empty<LoadedReference>(),
            Array.Empty<PickedBlock>(), new AssembledPrompt(string.Empty, "ask"));

        var raw = File.ReadAllText(path);
        Assert.StartsWith("---\n", raw);
        Assert.Contains("kind: preview", raw);
        Assert.Contains("loaded-types: 1", raw);
        Assert.Contains("loaded-namespaces: 1", raw);
        Assert.Contains("tags: db", raw);
    }

    [Fact]
    public void Write_PickedSection_ListsBlocksAndReasons()
    {
        var picked = new[]
        {
            new PickedBlock(Block("a", "Alpha", "x\n"), "matched 'always'"),
            new PickedBlock(Block("b", "Beta",  "y\n"), "included via 'a'"),
        };

        var path = new PromptArtifactWriter(_temp).Write(
            "preview", PickerContext.Empty, Array.Empty<LoadedReference>(),
            picked, new AssembledPrompt("sys", "user"));

        var raw = File.ReadAllText(path);
        Assert.Contains("# Picked blocks", raw);
        Assert.Contains("- **a** — matched 'always'", raw);
        Assert.Contains("- **b** — included via 'a'", raw);
        Assert.Contains("picked: a, b", raw);
    }

    [Fact]
    public void Write_NoPickedBlocks_EmitsExplicitEmptyMarker()
    {
        var path = new PromptArtifactWriter(_temp).Write(
            "preview", PickerContext.Empty, Array.Empty<LoadedReference>(),
            Array.Empty<PickedBlock>(), new AssembledPrompt(string.Empty, ""));

        var raw = File.ReadAllText(path);
        Assert.Contains("_(none — no block's `when:` rule matched", raw);
    }

    [Fact]
    public void Write_EmptyPrompt_RendersEmptyMarkers()
    {
        var path = new PromptArtifactWriter(_temp).Write(
            "preview", PickerContext.Empty, Array.Empty<LoadedReference>(),
            Array.Empty<PickedBlock>(), new AssembledPrompt(string.Empty, ""));

        var raw = File.ReadAllText(path);
        Assert.Contains("# System message\n\n_(empty)_", raw);
        Assert.Contains("# User message\n\n_(none — preview only)_", raw);
    }

    [Fact]
    public void Write_ReferencesSummary_GroupsBlastAndExternalSeparately()
    {
        var refs = new[]
        {
            new LoadedReference("AzureBlast", "2.1.1", "/x.dll", LoadedReferenceOrigin.Blast,
                Array.Empty<string>(), Array.Empty<string>()),
            new LoadedReference("Acme.Domain", "1.0.0", "/y.dll", LoadedReferenceOrigin.External,
                Array.Empty<string>(), Array.Empty<string>()),
            new LoadedReference("System.Runtime", "10.0.0", "/z.dll", LoadedReferenceOrigin.Framework,
                Array.Empty<string>(), Array.Empty<string>()),
        };

        var path = new PromptArtifactWriter(_temp).Write(
            "preview", PickerContext.Empty, refs,
            Array.Empty<PickedBlock>(), new AssembledPrompt(string.Empty, ""));

        var raw = File.ReadAllText(path);
        Assert.Contains("references-blast: AzureBlast 2.1.1", raw);
        Assert.Contains("references-external: Acme.Domain 1.0.0", raw);
        // Framework + Application + Other should not appear in the summary.
        Assert.DoesNotContain("System.Runtime", raw);
    }

    [Fact]
    public void Write_CreatesFolder_OnDemand()
    {
        var nested = Path.Combine(_temp, "fresh");
        var path = new PromptArtifactWriter(nested).Write(
            "preview", PickerContext.Empty, Array.Empty<LoadedReference>(),
            Array.Empty<PickedBlock>(), new AssembledPrompt("", ""));

        Assert.True(File.Exists(path));
    }
}
