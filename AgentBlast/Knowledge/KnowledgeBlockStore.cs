using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AgentBlast.Interfaces;

namespace AgentBlast.Knowledge;

/// <summary>
/// File-backed <see cref="IKnowledgeBlockStore"/>. Reads every <c>*.md</c>
/// file in <see cref="Folder"/> as a knowledge block; the file basename
/// is the id. Frontmatter (YAML-style <c>key: value</c> lines fenced by
/// two <c>---</c> markers at the top of the file) is parsed into a
/// case-insensitive string map; <c>tags:</c> and <c>includes:</c> are
/// additionally parsed as comma-separated lists for first-class use by
/// the picker. Writes round-trip the frontmatter back, putting the
/// well-known keys (title, when, priority, tags, includes) first when
/// present so the on-disk shape stays predictable for hand-editing.
/// </summary>
public sealed class KnowledgeBlockStore : IKnowledgeBlockStore
{
    private const string FenceLine = "---";
    private static readonly string[] PreferredOrder = { "title", "when", "priority", "tags", "includes" };

    private readonly Dictionary<string, KnowledgeBlock> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Build a store anchored on <paramref name="folder"/>; the constructor calls <see cref="Reload"/> immediately so callers see the on-disk state without an extra step.</summary>
    public KnowledgeBlockStore(string folder)
    {
        Folder = folder ?? throw new ArgumentNullException(nameof(folder));
        Reload();
    }

    /// <inheritdoc />
    public string Folder { get; }

    /// <inheritdoc />
    public IReadOnlyList<KnowledgeBlock> List() =>
        _byId.Values
            .OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <inheritdoc />
    public KnowledgeBlock? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return _byId.TryGetValue(id, out var b) ? b : null;
    }

    /// <inheritdoc />
    public void Save(KnowledgeBlock block)
    {
        if (block is null) throw new ArgumentNullException(nameof(block));
        if (string.IsNullOrWhiteSpace(block.Id))
            throw new ArgumentException("Block id is required.", nameof(block));

        Directory.CreateDirectory(Folder);
        var path = PathFor(block.Id);
        File.WriteAllText(path, Serialize(block));
        _byId[block.Id] = block;
    }

    /// <inheritdoc />
    public void Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var path = PathFor(id);
        if (File.Exists(path)) File.Delete(path);
        _byId.Remove(id);
    }

    /// <inheritdoc />
    public void Reload()
    {
        _byId.Clear();
        if (!Directory.Exists(Folder)) return;

        foreach (var path in Directory.EnumerateFiles(Folder, "*.md"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(id)) continue;
            try
            {
                var raw = File.ReadAllText(path);
                _byId[id] = Parse(id, raw);
            }
            catch (IOException)
            {
                // Skip files we can't read (e.g. transient lock); next Reload picks them up.
            }
        }
    }

    private string PathFor(string id) => Path.Combine(Folder, id + ".md");

    /// <summary>Parse a single markdown file into a <see cref="KnowledgeBlock"/>.</summary>
    public static KnowledgeBlock Parse(string id, string raw)
    {
        var (frontmatter, body) = SplitFrontmatter(raw);
        var title = frontmatter.TryGetValue("title", out var t) && !string.IsNullOrWhiteSpace(t)
            ? t.Trim()
            : Humanise(id);
        int? priority = null;
        if (frontmatter.TryGetValue("priority", out var pRaw)
            && int.TryParse(pRaw?.Trim(), System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var pVal))
        {
            priority = pVal;
        }
        var tags = frontmatter.TryGetValue("tags", out var tagRaw)
            ? ParseList(tagRaw, lowercase: true)
            : Array.Empty<string>();
        var includes = frontmatter.TryGetValue("includes", out var incRaw)
            ? ParseList(incRaw, lowercase: true)
            : Array.Empty<string>();
        return new KnowledgeBlock(id, title, body, priority, tags, includes, frontmatter);
    }

    /// <summary>
    /// Split a comma-separated frontmatter value into a normalised list.
    /// Empty / whitespace tokens are dropped. When <paramref name="lowercase"/>
    /// is true (the default for tags + includes), tokens are folded to
    /// lowercase so "Mssql" and "mssql" don't double-count.
    /// </summary>
    public static IReadOnlyList<string> ParseList(string? raw, bool lowercase = true)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var part in raw.Split(','))
        {
            var token = part.Trim();
            if (token.Length == 0) continue;
            if (lowercase) token = token.ToLowerInvariant();
            if (seen.Add(token)) result.Add(token);
        }
        return result;
    }

    /// <summary>Render a block back to the on-disk markdown form (frontmatter + body).</summary>
    public static string Serialize(KnowledgeBlock block)
    {
        var fm = MergedFrontmatter(block);
        var sb = new StringBuilder();
        if (fm.Count > 0)
        {
            sb.Append(FenceLine).Append('\n');
            foreach (var key in OrderedKeys(fm))
            {
                sb.Append(key).Append(": ").Append(fm[key]).Append('\n');
            }
            sb.Append(FenceLine).Append('\n');
            // Blank line between fence and body for readability when the body has content.
            if (!string.IsNullOrEmpty(block.Body)) sb.Append('\n');
        }
        sb.Append(block.Body ?? string.Empty);
        return sb.ToString();
    }

    private static Dictionary<string, string> MergedFrontmatter(KnowledgeBlock block)
    {
        // Promote Title / Tags / Includes back into the frontmatter so they
        // round-trip. Title is omitted when it's just the auto-humanised id
        // (otherwise we'd add a title line the user never wrote); empty
        // lists are removed so we never write "tags: " with no value.
        var merged = new Dictionary<string, string>(block.Frontmatter, StringComparer.OrdinalIgnoreCase);
        var humanised = Humanise(block.Id);
        if (!string.IsNullOrWhiteSpace(block.Title)
            && !string.Equals(block.Title, humanised, StringComparison.Ordinal))
        {
            merged["title"] = block.Title;
        }

        if (block.Priority.HasValue)
            merged["priority"] = block.Priority.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        else
            merged.Remove("priority");

        if (block.Tags.Count > 0) merged["tags"] = string.Join(", ", block.Tags);
        else merged.Remove("tags");

        if (block.Includes.Count > 0) merged["includes"] = string.Join(", ", block.Includes);
        else merged.Remove("includes");

        return merged;
    }

    private static IEnumerable<string> OrderedKeys(IDictionary<string, string> fm)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in PreferredOrder)
        {
            if (fm.ContainsKey(key))
            {
                seen.Add(key);
                yield return ResolveKeyCasing(fm, key);
            }
        }
        foreach (var key in fm.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(key)) yield return key;
        }
    }

    private static string ResolveKeyCasing(IDictionary<string, string> fm, string preferred)
    {
        // The dictionary is case-insensitive; return the actual stored
        // casing so hand-edited capitalisation survives a round-trip.
        foreach (var k in fm.Keys)
            if (string.Equals(k, preferred, StringComparison.OrdinalIgnoreCase))
                return k;
        return preferred;
    }

    private static (Dictionary<string, string> Frontmatter, string Body) SplitFrontmatter(string raw)
    {
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(raw)) return (empty, string.Empty);

        // Normalise the leading line ending only — the body's interior is preserved verbatim.
        var lines = raw.Split('\n');
        if (lines.Length == 0 || TrimEol(lines[0]) != FenceLine)
            return (empty, raw);

        var fm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 1;
        var foundClose = false;
        for (; i < lines.Length; i++)
        {
            var line = TrimEol(lines[i]);
            if (line == FenceLine) { foundClose = true; i++; break; }
            if (string.IsNullOrWhiteSpace(line)) continue;

            var colon = line.IndexOf(':');
            if (colon <= 0) continue; // malformed line — skip silently
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Length == 0) continue;
            fm[key] = value;
        }

        if (!foundClose) return (empty, raw); // unterminated frontmatter — treat as body

        // Body = remaining lines, with one leading blank line trimmed (the
        // separator we add on serialize) so round-trips are stable.
        if (i < lines.Length && string.IsNullOrEmpty(TrimEol(lines[i]))) i++;
        var body = string.Join('\n', lines[i..]);
        return (fm, body);
    }

    private static string TrimEol(string s) => s.EndsWith('\r') ? s[..^1] : s;

    /// <summary>Turn a kebab/snake id into Title Case for display fallback.</summary>
    public static string Humanise(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return string.Empty;
        var parts = id.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            var p = parts[i];
            sb.Append(char.ToUpperInvariant(p[0]));
            if (p.Length > 1) sb.Append(p[1..]);
        }
        return sb.ToString();
    }
}
