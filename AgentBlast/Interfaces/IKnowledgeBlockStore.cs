using System.Collections.Generic;
using AgentBlast.Knowledge;

namespace AgentBlast.Interfaces;

/// <summary>
/// Reads and writes the user's directing-context blocks. Each block is a
/// single markdown file under the host-chosen knowledge folder; frontmatter
/// lives at the top of the file as YAML-style <c>key: value</c> lines
/// between two <c>---</c> fences. The on-disk markdown is the source of
/// truth — users can edit the files outside the host application and the
/// store will pick the changes up on the next <see cref="Reload"/>.
/// </summary>
public interface IKnowledgeBlockStore
{
    /// <summary>The folder the store reads from and writes to.</summary>
    string Folder { get; }

    /// <summary>All known blocks, title-sorted (case-insensitive).</summary>
    IReadOnlyList<KnowledgeBlock> List();

    /// <summary>Look up a block by id (file basename, case-insensitive). Null if absent.</summary>
    KnowledgeBlock? Get(string id);

    /// <summary>Persist (or overwrite) one block. The id maps directly to the file name.</summary>
    void Save(KnowledgeBlock block);

    /// <summary>Delete a block. No-op when the file is already gone.</summary>
    void Delete(string id);

    /// <summary>Re-scan the folder and rebuild the in-memory list.</summary>
    void Reload();
}
