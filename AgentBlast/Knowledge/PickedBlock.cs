namespace AgentBlast.Knowledge;

/// <summary>
/// One block selected by <see cref="KnowledgeBlockPicker.PickWithReasons"/>,
/// paired with the human-readable reason it ended up in the result. Used
/// for audit trails ("why did the AI see this?") and the Assistant tab's
/// Preview button. The reason is best-effort prose, not a parseable
/// structure — its job is to make the picker's choice legible to the user.
/// </summary>
/// <param name="Block">The picked block itself.</param>
/// <param name="Reason">Why this block was included (matched a rule, or pulled in transitively).</param>
public sealed record PickedBlock(KnowledgeBlock Block, string Reason);
