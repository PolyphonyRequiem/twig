using Twig.Domain.Enums;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// A single state within a work item type's workflow, including its resolved category and optional color.
/// Color is raw 6-char hex without <c>#</c> prefix (e.g. "009CCC").
/// </summary>
public readonly record struct StateEntry(string Name, StateCategory Category, string? Color);
