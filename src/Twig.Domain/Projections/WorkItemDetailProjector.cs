using Twig.Domain.Services.Field;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Projections;

/// <summary>
/// Builds a <see cref="WorkItemDetailDocument"/> from an already-materialized
/// <see cref="FormLayout"/> and <see cref="WorkItemSnapshot"/>.
/// </summary>
/// <remarks>
/// <para>
/// A pure function over values. It takes no provider, opens no connection, and requires
/// no persistence store — read-only construction must never need one. Acquiring the
/// layout (<c>IFormLayoutProvider</c> → ADO REST → cache) stays behind Infrastructure and
/// is not part of this contract.
/// </para>
/// <para>
/// <b>Nothing is filtered.</b> Invisible controls, non-<c>custom</c> pages, and
/// contribution slots all survive into the document, flagged for what they are. Deciding
/// which of those to draw is the host's information architecture, not Twig's.
/// </para>
/// </remarks>
public static class WorkItemDetailProjector
{
    /// <summary>
    /// Length at or below which a value is carried without a separate short form.
    /// </summary>
    public const int ShortFormLength = 80;

    /// <summary>
    /// Projects <paramref name="layout"/> against <paramref name="snapshot"/>.
    /// </summary>
    /// <param name="layout">The server-authored form structure for the item's type.</param>
    /// <param name="snapshot">The item's values.</param>
    /// <param name="fieldDefinitions">
    /// Optional field metadata, keyed by reference name. Supplying it lets the projection
    /// distinguish <see cref="DetailFieldState.EmptyOnServer"/> from
    /// <see cref="DetailFieldState.NotCarriedByTwig"/> for fields absent from
    /// <see cref="WorkItemSnapshot.Fields"/>: a field Twig <i>would</i> have imported but
    /// did not receive is empty on the server, whereas one <c>FieldImportFilter</c>
    /// excludes is not carried at all. Without it, an absent non-core field is reported as
    /// not carried, because Twig cannot honestly claim the server said blank.
    /// </param>
    public static WorkItemDetailDocument Project(
        FormLayout layout,
        WorkItemSnapshot snapshot,
        IReadOnlyDictionary<string, FieldDefinition>? fieldDefinitions = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(snapshot);

        var pages = new List<DetailPage>(layout.Pages.Count);
        foreach (var page in layout.Pages)
        {
            var sections = new List<DetailSection>(page.Sections.Count);
            foreach (var section in page.Sections)
            {
                var groups = new List<DetailGroup>(section.Groups.Count);
                foreach (var group in section.Groups)
                {
                    var controls = new List<DetailControl>(group.Controls.Count);
                    foreach (var control in group.Controls)
                    {
                        controls.Add(new DetailControl(
                            control.Id,
                            control.Label,
                            control.ControlType,
                            control.ReadOnly,
                            control.Visible,
                            control.IsContribution,
                            control.IsContribution
                                ? null
                                : ResolveValue(control.Id, snapshot, fieldDefinitions)));
                    }

                    groups.Add(new DetailGroup(
                        group.Id, group.Label, group.Visible, group.IsContribution, controls));
                }

                sections.Add(new DetailSection(section.Id, groups));
            }

            pages.Add(new DetailPage(
                page.Id, page.Label, page.PageType, page.Visible, page.IsContribution, sections));
        }

        return new WorkItemDetailDocument(
            snapshot.Id,
            snapshot.Revision,
            layout.WorkItemTypeReferenceName,
            layout.ProcessId,
            pages);
    }

    /// <summary>
    /// Resolves one field reference name to one of the three states.
    /// </summary>
    public static DetailFieldValue ResolveValue(
        string fieldReferenceName,
        WorkItemSnapshot snapshot,
        IReadOnlyDictionary<string, FieldDefinition>? fieldDefinitions = null)
    {
        ArgumentNullException.ThrowIfNull(fieldReferenceName);
        ArgumentNullException.ThrowIfNull(snapshot);

        // The eight core fields are the resolvable sub-case, not a hole: FieldImportFilter
        // excludes them from Fields because they were promoted to snapshot properties.
        if (TryResolveCoreField(fieldReferenceName, snapshot, out var coreValue))
        {
            return Value(coreValue);
        }

        if (snapshot.Fields.TryGetValue(fieldReferenceName, out var carried))
        {
            return Value(carried);
        }

        // Absent. If metadata proves Twig would have imported it, the server had nothing.
        if (fieldDefinitions is not null
            && fieldDefinitions.TryGetValue(fieldReferenceName, out var definition)
            && FieldImportFilter.ShouldImport(fieldReferenceName, definition))
        {
            return new DetailFieldValue(DetailFieldState.EmptyOnServer, null, null);
        }

        return new DetailFieldValue(DetailFieldState.NotCarriedByTwig, null, null);

        static DetailFieldValue Value(string? raw) =>
            string.IsNullOrEmpty(raw)
                ? new DetailFieldValue(DetailFieldState.EmptyOnServer, null, null)
                : new DetailFieldValue(DetailFieldState.HasValue, raw, ShortFormOf(raw));
    }

    private static bool TryResolveCoreField(
        string refName, WorkItemSnapshot snapshot, out string? value)
    {
        switch (refName.ToLowerInvariant())
        {
            case "system.id": value = snapshot.Id.ToString(); return true;
            case "system.rev": value = snapshot.Revision.ToString(); return true;
            case "system.workitemtype": value = snapshot.TypeName; return true;
            case "system.title": value = snapshot.Title; return true;
            case "system.state": value = snapshot.State; return true;
            case "system.assignedto": value = snapshot.AssignedTo; return true;
            case "system.iterationpath": value = snapshot.IterationPath; return true;
            case "system.areapath": value = snapshot.AreaPath; return true;
            default: value = null; return false;
        }
    }

    /// <summary>
    /// Computes the one-line short form, or <c>null</c> when the value is already short
    /// and single-line. The full value is always carried alongside — this never replaces it.
    /// </summary>
    private static string? ShortFormOf(string full)
    {
        var flattened = full;
        var firstBreak = flattened.AsSpan().IndexOfAny('\r', '\n');
        var multiline = firstBreak >= 0;
        if (multiline) flattened = flattened[..firstBreak].TrimEnd();

        if (!multiline && flattened.Length <= ShortFormLength) return null;
        if (flattened.Length <= ShortFormLength) return flattened + "…";
        return string.Concat(flattened.AsSpan(0, ShortFormLength).TrimEnd(), "…");
    }
}
