using System.Collections.Generic;

namespace AgentBlast.Prompts;

/// <summary>
/// What kind of code <see cref="LoadedReference"/> represents. Used by
/// <see cref="PromptBuilder"/> to filter the noisy AppDomain (which has
/// 100+ framework assemblies on a typical run) down to "the things a
/// script can actually use" before describing them to the model.
/// </summary>
public enum LoadedReferenceOrigin
{
    /// <summary>Runtime BCL — <c>System.*</c>, <c>Microsoft.*</c> shipping with .NET.</summary>
    Framework,

    /// <summary>The host application's own assemblies and the deps it ships with.</summary>
    Application,

    /// <summary>One of the Blast-family nugets (carries the <c>Blast.PrimaryFacade</c> attribute).</summary>
    Blast,

    /// <summary>User-imported via the host's external-references mechanism (loose DLL or unpacked nupkg).</summary>
    External,

    /// <summary>Anything else loaded into the AppDomain that doesn't match the above.</summary>
    Other,
}

/// <summary>
/// Structured snapshot of one loaded assembly. Designed as the unit the
/// host hands to AgentBlast as "what's available in scope right now"; the
/// host is responsible for producing this snapshot (typically by walking
/// <c>AppDomain.CurrentDomain</c> and classifying each assembly), and
/// AgentBlast consumes it through <see cref="PromptBuilder.Build"/>.
/// </summary>
/// <param name="Name">Assembly simple name.</param>
/// <param name="Version">Assembly version string.</param>
/// <param name="Location">Disk path of the loaded assembly, or null when in-memory only.</param>
/// <param name="Origin">Classification used to filter the snapshot before describing it to the model.</param>
/// <param name="PrimaryFacades">Fully-qualified canonical front-door type names declared via the <c>Blast.PrimaryFacade</c> assembly metadata attribute. Empty for non-Blast assemblies.</param>
/// <param name="Namespaces">Public namespaces exported by the assembly.</param>
public sealed record LoadedReference(
    string Name,
    string Version,
    string? Location,
    LoadedReferenceOrigin Origin,
    IReadOnlyList<string> PrimaryFacades,
    IReadOnlyList<string> Namespaces);
