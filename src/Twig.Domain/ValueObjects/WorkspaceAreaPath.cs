namespace Twig.Domain.ValueObjects;

/// <summary>
/// An area path configuration entry for workspace mode filtering.
/// </summary>
/// <param name="Path">The area path (e.g., "Project\Team").</param>
/// <param name="Semantics">Match semantics: "exact" (exact match) or "under" (includes children).</param>
public sealed record WorkspaceAreaPath(string Path, string Semantics);
