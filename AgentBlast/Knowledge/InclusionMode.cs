namespace AgentBlast.Knowledge;

/// <summary>
/// Why a <see cref="PickedBlock"/> ended up in the picker's result, and
/// — by extension — how <see cref="Prompts.PromptBuilder"/> should render it.
///
/// <para>
/// Direct matches always win over transitive includes: if a block matches
/// a <c>when:</c> rule directly <i>and</i> is also pulled in via another
/// block's <c>includes:</c>, it is reported as <see cref="Triggered"/>.
/// </para>
/// </summary>
public enum InclusionMode
{
    /// <summary>
    /// The block's own <c>when:</c> rule matched the picker context.
    /// The prompt builder renders these blocks fully (heading + body).
    /// </summary>
    Triggered,

    /// <summary>
    /// The block was pulled in transitively via another block's
    /// <c>includes:</c> but its own <c>when:</c> rule (if any) did not
    /// match the context. The prompt builder renders these compactly
    /// (title + id only) so the agent knows the block exists without
    /// paying the full token cost of its body.
    /// </summary>
    Available,
}
