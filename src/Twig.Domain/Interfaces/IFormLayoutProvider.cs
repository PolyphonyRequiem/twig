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
    /// <returns>
    /// The layout, or <c>null</c> when it cannot be determined — an unknown or disabled
    /// work item type, an undetectable process, or a server that does not serve a layout
    /// for this process. Callers must handle <c>null</c> rather than assume a layout
    /// exists; whether stock (non-inherited) processes serve one is an open question on
    /// ticket 1004.
    /// </returns>
    Task<FormLayout?> GetFormLayoutAsync(
        string workItemTypeName,
        CancellationToken ct = default);
}
