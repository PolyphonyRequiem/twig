using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Reads the server-defined work item form layout — the tabs, boxes, and ordered fields
/// that the web editor draws for a work item type.
/// </summary>
/// <remarks>
/// Production seam for the server-driven 1.0 editor (wayfinder-1.0 ticket 1003), not
/// scaffolding for the export command that currently consumes it.
/// </remarks>
internal interface IFormLayoutProvider
{
    /// <summary>
    /// Gets the form layout for <paramref name="workItemTypeName"/> under the process the
    /// current project uses.
    /// </summary>
    /// <param name="workItemTypeName">
    /// 🔴 The type's DISPLAY name (<c>Task</c>) or its process REFERENCE name
    /// (<c>Niflheim.Task</c>). Both are accepted, and both resolve against the PROCESS's
    /// own type roster — see the implementation's remarks for why the project's roster is
    /// the wrong one to resolve against (AB#247).
    /// </param>
    /// <returns>
    /// <see cref="FormLayoutResult.Served"/>, <see cref="FormLayoutResult.Locked"/>, or
    /// <see cref="FormLayoutResult.Unavailable"/>. Callers must handle all three:
    /// collapsing <c>Locked</c> into <c>Unavailable</c> loses the fact that the process
    /// answered, and collapsing either into an empty layout asserts the form has no
    /// controls. Whether stock (non-inherited) processes serve a layout at all is an open
    /// question on ticket 1004, answered by observing <c>Unavailable</c>.
    /// </returns>
    Task<FormLayoutResult> GetFormLayoutAsync(
        string workItemTypeName,
        CancellationToken ct = default);
}
