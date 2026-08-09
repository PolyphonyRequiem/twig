using Twig.Domain.Projections;
using Twig.Domain.ValueObjects;

namespace Twig.DetailHost;

/// <summary>
/// The external-host probe for wayfinder-detail-projection ticket 0003.
/// </summary>
/// <remarks>
/// <para>
/// The whole call chain from a consumer's point of view is three lines — construct nothing,
/// authenticate to nothing, open nothing:
/// </para>
/// <code>
/// var document = WorkItemDetailProjector.Project(layout, snapshot, fieldDefinitions);
/// pane.Load(document, appearance);
/// Console.WriteLine(pane.Render());
/// </code>
/// <para>
/// This program also ASSERTS the acceptance floor and exits non-zero if the probe ever
/// stops proving what it claims. A sample that only prints can rot into a demo.
/// </para>
/// </remarks>
internal static class Program
{
    private static int Main()
    {
        // === The entire consumer contract ===
        WorkItemDetailDocument document =
            WorkItemDetailProjector.Project(Fixture.Layout, Fixture.Snapshot, Fixture.FieldDefinitions);
        WorkItemTypeAppearance appearance = Fixture.Appearance; // asked for separately
        // =====================================

        var pane = new HostPane(width: 76, height: 22);
        pane.Load(document, appearance);

        Console.WriteLine("=== caller-owned pane, as first painted ===");
        Console.WriteLine(pane.Render());

        // Scrolling and selection are the host's, so the host can just do them.
        for (var i = 0; i < 6; i++) pane.MoveSelection(1);

        Console.WriteLine();
        Console.WriteLine("=== caller-owned selection moved onto the long value ===");
        Console.WriteLine(pane.Render());

        var expanded = pane.SelectedFullValue;
        if (expanded is null)
        {
            Console.WriteLine();
            Console.WriteLine("PROBE FAILED: the selected long value had no full source value to expand.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("=== expanded view of the selected row (full source value, never truncated) ===");
        Console.WriteLine(expanded);

        pane.Scroll(12);
        Console.WriteLine();
        Console.WriteLine("=== after caller-owned scroll, revealing the server-rendered pages ===");
        Console.WriteLine(pane.Render());

        var failures = CheckAcceptanceFloor(document);
        Console.WriteLine();
        if (failures.Count > 0)
        {
            Console.WriteLine("PROBE FAILED:");
            foreach (var failure in failures) Console.WriteLine("  - " + failure);
            return 1;
        }

        Console.WriteLine("PROBE OK: all three field states, a non-custom page, and a contribution control exercised.");
        return 0;
    }

    /// <summary>
    /// Ticket 0002 §11: the fixture must exercise all three field states, at least one
    /// non-<c>custom</c> page, and one contribution control. Anything less proves only the
    /// happy path.
    /// </summary>
    private static List<string> CheckAcceptanceFloor(WorkItemDetailDocument document)
    {
        var failures = new List<string>();

        var controls = document.Pages
            .SelectMany(page => page.AllGroups)
            .SelectMany(group => group.Controls)
            .ToList();

        var states = controls
            .Where(control => control.Value is not null)
            .Select(control => control.Value!.State)
            .ToHashSet();

        foreach (var required in Enum.GetValues<DetailFieldState>())
        {
            if (!states.Contains(required)) failures.Add($"no control resolved to {required}");
        }

        if (!document.Pages.Any(page => !page.CarriesFieldControls))
        {
            failures.Add("no non-custom page carried");
        }

        if (!controls.Any(control => control.IsContribution))
        {
            failures.Add("no contribution control carried");
        }

        // The core-field hole ticket 0002 exists to close: System.Title is absent from
        // WorkItemSnapshot.Fields entirely, so a naive host blanks it.
        var title = controls.FirstOrDefault(control =>
            control.Id.Equals("System.Title", StringComparison.OrdinalIgnoreCase));
        if (title?.Value?.State != DetailFieldState.HasValue)
        {
            failures.Add("System.Title did not resolve to HasValue — the core-field hole is open");
        }

        // A long value must carry BOTH forms.
        var description = controls.FirstOrDefault(control =>
            control.Id.Equals("System.Description", StringComparison.OrdinalIgnoreCase));
        if (description?.Value is not { State: DetailFieldState.HasValue, Short: not null, Full: not null })
        {
            failures.Add("System.Description did not carry both a full value and a short form");
        }
        else if (!description.Value.Full!.Contains("never a replacement", StringComparison.Ordinal))
        {
            failures.Add("System.Description's full value was truncated by the projection");
        }

        return failures;
    }
}
