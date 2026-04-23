namespace Twig.Domain.ValueObjects;

/// <summary>
/// A sprint iteration configuration entry for workspace mode.
/// </summary>
/// <param name="Expression">The iteration path expression (e.g., "@CurrentIteration" or a literal path).</param>
/// <param name="Type">Entry type: "relative" (macro-based) or "absolute" (literal path).</param>
public sealed record SprintIterationEntry(string Expression, string Type);
