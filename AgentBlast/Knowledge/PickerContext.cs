using System;
using System.Collections.Generic;

namespace AgentBlast.Knowledge;

/// <summary>
/// What's "in scope" for one AI call. The picker matches each block's
/// <c>when:</c> rule against this context to decide whether the block
/// is relevant to inject. Constructed by the caller (typically the
/// script editor or whatever named operation is running) at the moment
/// the AI button is clicked.
/// </summary>
/// <param name="LoadedTypeFqns">Fully-qualified names of types currently loaded (e.g. <c>AzureBlast.MssqlDatabase</c>, <c>Acme.Domain.Customer</c>).</param>
/// <param name="LoadedNamespaces">Namespaces present among the loaded types (e.g. <c>AzureBlast</c>, <c>Acme.Domain</c>).</param>
/// <param name="Tags">Free-form tags the caller wants to expose to <c>when: tag:foo</c> rules (operation name, mode, user-pinned labels, …).</param>
public sealed record PickerContext(
    IReadOnlySet<string> LoadedTypeFqns,
    IReadOnlySet<string> LoadedNamespaces,
    IReadOnlyList<string> Tags)
{
    /// <summary>Empty context — matches only <c>when: always</c> blocks.</summary>
    public static PickerContext Empty { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        Array.Empty<string>());
}
