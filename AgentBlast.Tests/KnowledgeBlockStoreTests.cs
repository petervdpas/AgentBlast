using System;
using Xunit;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentBlast.Knowledge;

namespace AgentBlast.Tests;

/// <summary>
/// Tests covering <see cref="KnowledgeBlockStore"/>: file scan, frontmatter
/// parse, body extraction, round-trip through Save, delete, and the
/// edge cases that come up when users hand-edit the markdown files.
/// </summary>
public sealed class KnowledgeBlockStoreTests : IDisposable
{
    private readonly string _temp;

    public KnowledgeBlockStoreTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "tb-knowledge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    [Fact]
    public void List_EmptyFolder_ReturnsEmpty()
    {
        var store = new KnowledgeBlockStore(_temp);
        Assert.Empty(store.List());
    }

    [Fact]
    public void List_NonexistentFolder_ReturnsEmpty()
    {
        var nonexistent = Path.Combine(_temp, "nope");
        var store = new KnowledgeBlockStore(nonexistent);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Reload_ReadsMarkdownFiles_AndParsesFrontmatter()
    {
        File.WriteAllText(Path.Combine(_temp, "mssql-rules.md"),
            "---\ntitle: Mssql do's and don'ts\nwhen: AzureBlast.MssqlDatabase\npriority: 5\n---\n\nAlways dispose.\n");

        var store = new KnowledgeBlockStore(_temp);
        var blocks = store.List();

        Assert.Single(blocks);
        var b = blocks[0];
        Assert.Equal("mssql-rules", b.Id);
        Assert.Equal("Mssql do's and don'ts", b.Title);
        Assert.Equal("AzureBlast.MssqlDatabase", b.Frontmatter["when"]);
        Assert.Equal(5, b.Priority);
        Assert.Equal("Always dispose.\n", b.Body);
    }

    [Fact]
    public void Reload_FileWithoutFrontmatter_HasHumanisedTitle_AndFullBody()
    {
        File.WriteAllText(Path.Combine(_temp, "secret-handling.md"),
            "Use the vault. Never log secrets.\n");

        var store = new KnowledgeBlockStore(_temp);
        var b = store.Get("secret-handling")!;

        Assert.Equal("Secret Handling", b.Title);
        Assert.Empty(b.Frontmatter);
        Assert.Equal("Use the vault. Never log secrets.\n", b.Body);
    }

    [Fact]
    public void Save_WritesFrontmatterAndBody_BackToDisk()
    {
        var store = new KnowledgeBlockStore(_temp);
        var block = new KnowledgeBlock(
            "logging",
            "Logging conventions",
            "Always log at the boundary, never inside loops.\n",
            Priority: 3,
            Array.Empty<string>(),
            Array.Empty<string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["when"] = "any script",
            });

        store.Save(block);

        var raw = File.ReadAllText(Path.Combine(_temp, "logging.md"));
        Assert.StartsWith("---\n", raw);
        Assert.Contains("title: Logging conventions", raw);
        Assert.Contains("when: any script", raw);
        Assert.Contains("priority: 3", raw);
        Assert.EndsWith("Always log at the boundary, never inside loops.\n", raw);
    }

    [Fact]
    public void Save_ThenReload_RoundTripsBlock()
    {
        var first = new KnowledgeBlockStore(_temp);
        first.Save(new KnowledgeBlock(
            "round-trip",
            "Round trip",
            "Body line 1\n\nBody line 2 with `code`.\n",
            Priority: null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["when"] = "always",
            }));

        var second = new KnowledgeBlockStore(_temp);
        var b = second.Get("round-trip")!;

        Assert.Equal("Round trip", b.Title);
        Assert.Equal("always", b.Frontmatter["when"]);
        Assert.Equal("Body line 1\n\nBody line 2 with `code`.\n", b.Body);
    }

    [Fact]
    public void Save_OmitsTitleFrontmatter_WhenTitleEqualsHumanisedId()
    {
        // If the user never customises the title, we don't pollute the file
        // with an auto-generated title line — keeps hand-authored files clean.
        var store = new KnowledgeBlockStore(_temp);
        store.Save(new KnowledgeBlock(
            "plain-block",
            "Plain Block",
            "Just a body.\n",
            Priority: null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

        var raw = File.ReadAllText(Path.Combine(_temp, "plain-block.md"));
        Assert.DoesNotContain("title:", raw);
        Assert.Equal("Just a body.\n", raw);
    }

    [Fact]
    public void Delete_RemovesFile_AndEntryFromList()
    {
        File.WriteAllText(Path.Combine(_temp, "doomed.md"), "content");
        var store = new KnowledgeBlockStore(_temp);
        Assert.NotNull(store.Get("doomed"));

        store.Delete("doomed");

        Assert.Null(store.Get("doomed"));
        Assert.False(File.Exists(Path.Combine(_temp, "doomed.md")));
    }

    [Fact]
    public void Delete_MissingId_DoesNotThrow()
    {
        var store = new KnowledgeBlockStore(_temp);
        store.Delete("never-existed"); // no throw
    }

    [Fact]
    public void Reload_UnterminatedFrontmatter_TreatsWholeFileAsBody()
    {
        // Hand-edited file with a stray "---" at the top but no closing
        // fence shouldn't lose the user's content — fall back to body-only.
        File.WriteAllText(Path.Combine(_temp, "broken.md"),
            "---\ntitle: never closed\nbody starts here\n");

        var store = new KnowledgeBlockStore(_temp);
        var b = store.Get("broken")!;

        Assert.Equal("Broken", b.Title);  // humanised fallback
        Assert.Empty(b.Frontmatter);
        Assert.Equal("---\ntitle: never closed\nbody starts here\n", b.Body);
    }

    [Fact]
    public void Reload_HandlesCrlfLineEndings()
    {
        // Windows-saved files end lines with \r\n; the parser must tolerate them.
        File.WriteAllText(Path.Combine(_temp, "crlf.md"),
            "---\r\ntitle: Windows file\r\n---\r\n\r\nBody.\r\n");

        var store = new KnowledgeBlockStore(_temp);
        var b = store.Get("crlf")!;

        Assert.Equal("Windows file", b.Title);
        Assert.Equal("Body.\r\n", b.Body);
    }

    [Fact]
    public void Parse_MalformedFrontmatterLine_IsSkipped_RestSurvives()
    {
        File.WriteAllText(Path.Combine(_temp, "mixed.md"),
            "---\ntitle: Good\nbroken-line-without-colon\nwhen: always\n---\nbody\n");

        var store = new KnowledgeBlockStore(_temp);
        var b = store.Get("mixed")!;

        Assert.Equal("Good", b.Title);
        Assert.Equal("always", b.Frontmatter["when"]);
        Assert.False(b.Frontmatter.ContainsKey("broken-line-without-colon"));
    }

    [Fact]
    public void Save_NormalisesFrontmatterKeyOrdering_TitleWhenPriorityFirst()
    {
        var store = new KnowledgeBlockStore(_temp);
        store.Save(new KnowledgeBlock(
            "ordered",
            "Ordered Block",
            "x\n",
            Priority: 1,
            Array.Empty<string>(),
            Array.Empty<string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["zeta"] = "z",
                ["alpha"] = "a",
                ["when"] = "always",
            }));

        var raw = File.ReadAllText(Path.Combine(_temp, "ordered.md"));
        var fenceBody = raw.Split("---", StringSplitOptions.None);
        // Frontmatter is between the first and second fence (index 1).
        var fmLines = fenceBody[1]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(':', 2)[0].Trim())
            .ToList();

        Assert.Equal(new[] { "title", "when", "priority", "alpha", "zeta" }, fmLines);
    }

    [Fact]
    public void List_OrdersByTitle_CaseInsensitive()
    {
        File.WriteAllText(Path.Combine(_temp, "b.md"), "---\ntitle: banana\n---\n");
        File.WriteAllText(Path.Combine(_temp, "a.md"), "---\ntitle: Apple\n---\n");
        File.WriteAllText(Path.Combine(_temp, "c.md"), "---\ntitle: cherry\n---\n");

        var store = new KnowledgeBlockStore(_temp);
        var titles = store.List().Select(b => b.Title).ToArray();

        Assert.Equal(new[] { "Apple", "banana", "cherry" }, titles);
    }

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        var store = new KnowledgeBlockStore(_temp);
        store.Save(new KnowledgeBlock("overw", "First", "v1\n",
            Priority: null, Array.Empty<string>(), Array.Empty<string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        store.Save(new KnowledgeBlock("overw", "Second", "v2\n",
            Priority: null, Array.Empty<string>(), Array.Empty<string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

        var b = store.Get("overw")!;
        Assert.Equal("Second", b.Title);
        Assert.Equal("v2\n", b.Body);
    }

    [Fact]
    public void Save_CreatesFolderIfMissing()
    {
        // Settings change can move the knowledge folder underneath us;
        // first Save after a fresh folder shouldn't fail because of it.
        var subfolder = Path.Combine(_temp, "fresh");
        var store = new KnowledgeBlockStore(subfolder);
        store.Save(new KnowledgeBlock("seed", "Seed", "hi\n",
            Priority: null, Array.Empty<string>(), Array.Empty<string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

        Assert.True(File.Exists(Path.Combine(subfolder, "seed.md")));
    }

    [Fact]
    public void Reload_ParsesTagsAndIncludesAsLists()
    {
        File.WriteAllText(Path.Combine(_temp, "withlists.md"),
            "---\ntitle: Lists\ntags: mssql, db, IO\nincludes: base-conventions, sql-shared\n---\nbody\n");

        var b = new KnowledgeBlockStore(_temp).Get("withlists")!;

        Assert.Equal(new[] { "mssql", "db", "io" }, b.Tags);
        Assert.Equal(new[] { "base-conventions", "sql-shared" }, b.Includes);
    }

    [Fact]
    public void Reload_AbsentTagsAndIncludes_AreEmptyLists()
    {
        File.WriteAllText(Path.Combine(_temp, "no-lists.md"),
            "---\ntitle: Plain\n---\nbody\n");

        var b = new KnowledgeBlockStore(_temp).Get("no-lists")!;

        Assert.Empty(b.Tags);
        Assert.Empty(b.Includes);
    }

    [Fact]
    public void Reload_TagsList_DropsBlanksAndDeduplicates()
    {
        // Hand-edited files often have stray commas / repeated tokens; keeping
        // the picker honest means the parser cleans them up.
        File.WriteAllText(Path.Combine(_temp, "messy.md"),
            "---\ntags: db, , db, mssql,  \n---\n");

        var b = new KnowledgeBlockStore(_temp).Get("messy")!;

        Assert.Equal(new[] { "db", "mssql" }, b.Tags);
    }

    [Fact]
    public void Save_WritesTagsAndIncludes_AsCommaSeparatedFrontmatter()
    {
        var store = new KnowledgeBlockStore(_temp);
        store.Save(new KnowledgeBlock(
            "writes-lists",
            "Writes lists",
            "body\n",
            Priority: null,
            new[] { "mssql", "db" },
            new[] { "base", "sql-shared" },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

        var raw = File.ReadAllText(Path.Combine(_temp, "writes-lists.md"));
        Assert.Contains("tags: mssql, db", raw);
        Assert.Contains("includes: base, sql-shared", raw);
    }

    [Fact]
    public void Save_EmptyTagsOrIncludes_OmitsTheKeyEntirely()
    {
        // Even when prior frontmatter had a tags/includes line, Save with
        // empty lists must drop the key — otherwise we leave dangling
        // "tags: " entries that confuse round-trips.
        File.WriteAllText(Path.Combine(_temp, "drop.md"),
            "---\ntags: a, b\nincludes: x\n---\nbody\n");
        var store = new KnowledgeBlockStore(_temp);
        var existing = store.Get("drop")!;

        store.Save(existing with { Tags = Array.Empty<string>(), Includes = Array.Empty<string>() });

        var raw = File.ReadAllText(Path.Combine(_temp, "drop.md"));
        Assert.DoesNotContain("tags:", raw);
        Assert.DoesNotContain("includes:", raw);
    }

    [Fact]
    public void Save_TagsAndIncludes_AppearInPreferredOrder_AfterPriority()
    {
        var store = new KnowledgeBlockStore(_temp);
        store.Save(new KnowledgeBlock(
            "ordered2",
            "A custom title",
            "x\n",
            Priority: 1,
            new[] { "t1" },
            new[] { "i1" },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["zeta"]     = "z",
                ["alpha"]    = "a",
                ["when"]     = "always",
            }));

        var raw = File.ReadAllText(Path.Combine(_temp, "ordered2.md"));
        var fmLines = raw.Split("---", StringSplitOptions.None)[1]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(':', 2)[0].Trim())
            .ToList();

        Assert.Equal(new[] { "title", "when", "priority", "tags", "includes", "alpha", "zeta" }, fmLines);
    }

    [Fact]
    public void Save_ThenReload_RoundTripsTagsAndIncludes()
    {
        var first = new KnowledgeBlockStore(_temp);
        first.Save(new KnowledgeBlock(
            "rt-lists",
            "Round trip lists",
            "body\n",
            Priority: null,
            new[] { "mssql", "db" },
            new[] { "shared" },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

        var b = new KnowledgeBlockStore(_temp).Get("rt-lists")!;

        Assert.Equal(new[] { "mssql", "db" }, b.Tags);
        Assert.Equal(new[] { "shared" }, b.Includes);
    }

    [Fact]
    public void ParseList_TrimsAndLowercases_DefaultMode()
    {
        Assert.Equal(new[] { "a", "b", "c" }, KnowledgeBlockStore.ParseList(" A , b ,C "));
    }

    [Fact]
    public void ParseList_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(KnowledgeBlockStore.ParseList(null));
        Assert.Empty(KnowledgeBlockStore.ParseList(""));
        Assert.Empty(KnowledgeBlockStore.ParseList("   "));
    }

    [Fact]
    public void Reload_ParsesPriorityAsInt()
    {
        File.WriteAllText(Path.Combine(_temp, "p.md"), "---\npriority: 5\n---\n");
        var b = new KnowledgeBlockStore(_temp).Get("p")!;
        Assert.Equal(5, b.Priority);
    }

    [Fact]
    public void Reload_PriorityAbsent_IsNull()
    {
        File.WriteAllText(Path.Combine(_temp, "noprio.md"), "---\ntitle: x\n---\n");
        var b = new KnowledgeBlockStore(_temp).Get("noprio")!;
        Assert.Null(b.Priority);
    }

    [Fact]
    public void Reload_PriorityNonNumeric_IsNull_ButPreservesRawString()
    {
        // Hand-edited "priority: high" shouldn't crash — the typed property
        // is null, and the raw string stays accessible via Frontmatter for
        // anyone who wants to inspect / migrate.
        File.WriteAllText(Path.Combine(_temp, "bad.md"), "---\npriority: high\n---\n");
        var b = new KnowledgeBlockStore(_temp).Get("bad")!;
        Assert.Null(b.Priority);
        Assert.Equal("high", b.Frontmatter["priority"]);
    }

    [Fact]
    public void Save_WritesPriorityAsInt_OmitsWhenNull()
    {
        var store = new KnowledgeBlockStore(_temp);
        store.Save(new KnowledgeBlock("withp", "With p", "x\n",
            Priority: 7, Array.Empty<string>(), Array.Empty<string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        store.Save(new KnowledgeBlock("nop", "No p", "x\n",
            Priority: null, Array.Empty<string>(), Array.Empty<string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

        Assert.Contains("priority: 7", File.ReadAllText(Path.Combine(_temp, "withp.md")));
        Assert.DoesNotContain("priority", File.ReadAllText(Path.Combine(_temp, "nop.md")));
    }

    [Fact]
    public void Save_ThenReload_RoundTripsPriority()
    {
        new KnowledgeBlockStore(_temp).Save(new KnowledgeBlock(
            "rt-prio", "Round trip", "x\n",
            Priority: 42, Array.Empty<string>(), Array.Empty<string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

        var b = new KnowledgeBlockStore(_temp).Get("rt-prio")!;
        Assert.Equal(42, b.Priority);
    }
}
