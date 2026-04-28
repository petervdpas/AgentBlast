namespace AgentBlast.Prompts;

/// <summary>
/// The output of <see cref="PromptBuilder"/>: the two halves of an
/// AI request, ready to hand to <see cref="AgentBlast.AgentClient"/>. The system half
/// carries stable directing context (knowledge blocks + library reference)
/// that doesn't change between calls within a session; the user half
/// carries the actual ask + any per-call payload (script text, selection,
/// operation parameters).
/// </summary>
/// <param name="SystemMessage">Stable directing context. Empty when no knowledge blocks were picked and no relevant libraries are loaded.</param>
/// <param name="UserMessage">The per-call payload: the user's question plus any script/file content the operation wants the model to see.</param>
public sealed record AssembledPrompt(string SystemMessage, string UserMessage);
