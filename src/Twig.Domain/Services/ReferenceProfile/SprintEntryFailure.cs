namespace Twig.Domain.Services.ReferenceProfile;

/// <summary>
/// Named identifiers the sprint-entry gate raises. Kept as string constants for
/// the same reason as <c>AttachmentStorageFailure</c>: the value survives
/// verbatim through <see cref="Twig.Domain.Common.Result"/> and surface tests
/// assert the literal.
/// </summary>
internal static class SprintEntryFailure
{
    /// <summary>
    /// A work item of a type other than the reference profile's sprint-tier
    /// binding was being committed directly to a sprint iteration.
    /// </summary>
    public const string NotSprintTier = "sprint-entry-not-sprint-tier";
}
