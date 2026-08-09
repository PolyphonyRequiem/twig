using System.Reflection;
using Shouldly;
using Twig.Domain.Projections;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Projections;

/// <summary>
/// Pins the fallback used when the server serves no layout at all —
/// <c>IFormLayoutProvider.GetFormLayoutAsync</c> returning <c>null</c>
/// (wayfinder-detail-projection ticket 0004).
/// </summary>
public class FallbackFormLayoutTests
{
    private static WorkItemSnapshot Snapshot(
        Dictionary<string, string?>? fields = null) => new()
        {
            Id = 42,
            Revision = 7,
            TypeName = "User Story",
            Title = "Expose a hostable work-item detail projection",
            State = "Active",
            AssignedTo = "Alice",
            IterationPath = "Project\\Sprint 1",
            AreaPath = "Project\\Team",
            Fields = fields ?? new Dictionary<string, string?>(),
        };

    [Fact]
    public void For_ProducesALayout_NotAFieldList()
    {
        // The whole point: the degraded path yields the SAME type the server path yields,
        // so a host keeps one walk rather than two field-selection implementations.
        FallbackFormLayout.For(Snapshot()).ShouldBeOfType<FormLayout>();
    }

    [Fact]
    public void For_CarriesTheEightCoreFields_WhichFieldsAloneWouldHaveLost()
    {
        // A fallback that enumerated WorkItemSnapshot.Fields would have produced a form with
        // no Title, State, or Assigned To — FieldImportFilter.CoreFieldRefs excludes all
        // eight. This is the specific defect 0002 §3 documents.
        var layout = FallbackFormLayout.For(Snapshot());

        var refNames = layout.Pages
            .SelectMany(p => p.AllGroups)
            .SelectMany(g => g.Controls)
            .Select(c => c.Id)
            .ToList();

        refNames.ShouldContain("System.Title");
        refNames.ShouldContain("System.State");
        refNames.ShouldContain("System.AssignedTo");
        refNames.ShouldContain("System.IterationPath");
        refNames.ShouldContain("System.AreaPath");
        refNames.ShouldContain("System.Id");
        refNames.ShouldContain("System.Rev");
        refNames.ShouldContain("System.WorkItemType");
    }

    [Fact]
    public void For_PutsCoreFieldsBeforeCarriedFields()
    {
        var layout = FallbackFormLayout.For(Snapshot(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "2",
        }));

        var refNames = layout.Pages
            .SelectMany(p => p.AllGroups)
            .SelectMany(g => g.Controls)
            .Select(c => c.Id)
            .ToList();

        refNames.IndexOf("System.Title")
            .ShouldBeLessThan(refNames.IndexOf("Microsoft.VSTS.Common.Priority"));
    }

    [Fact]
    public void For_CarriesEveryFieldTheSnapshotHas()
    {
        var layout = FallbackFormLayout.For(Snapshot(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "2",
            ["System.Tags"] = "urgent",
            ["System.Description"] = "<p>Body</p>",
        }));

        var refNames = layout.Pages
            .SelectMany(p => p.AllGroups)
            .SelectMany(g => g.Controls)
            .Select(c => c.Id)
            .ToList();

        refNames.ShouldContain("Microsoft.VSTS.Common.Priority");
        refNames.ShouldContain("System.Tags");
        refNames.ShouldContain("System.Description");
    }

    [Fact]
    public void ProjectedFallback_NeverReportsNotCarriedByTwig()
    {
        // The honest shape: with no server layout Twig does not know which fields the form
        // OUGHT to have, so it names only fields it demonstrably carries and never invents
        // an absent row. Every control must therefore resolve.
        var snapshot = Snapshot(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "2",
            ["System.Tags"] = "",
        });

        var document = WorkItemDetailProjector.Project(FallbackFormLayout.For(snapshot), snapshot);

        var states = document.Pages
            .SelectMany(p => p.AllGroups)
            .SelectMany(g => g.Controls)
            .Select(c => c.Value!.State)
            .ToList();

        states.ShouldNotBeEmpty();
        states.ShouldNotContain(DetailFieldState.NotCarriedByTwig);
    }

    [Fact]
    public void ProjectedFallback_ResolvesCoreFieldsFromSnapshotProperties()
    {
        var snapshot = Snapshot();
        var document = WorkItemDetailProjector.Project(FallbackFormLayout.For(snapshot), snapshot);

        var title = document.Pages
            .SelectMany(p => p.AllGroups)
            .SelectMany(g => g.Controls)
            .Single(c => c.Id == "System.Title");

        title.Value!.State.ShouldBe(DetailFieldState.HasValue);
        title.Value.Full.ShouldBe("Expose a hostable work-item detail projection");
    }

    [Fact]
    public void For_IsDistinguishableFromAServedLayout()
    {
        var fallback = FallbackFormLayout.For(Snapshot());

        fallback.ProcessId.ShouldBe(FallbackFormLayout.FallbackProcessId);
        FallbackFormLayout.IsFallback(fallback).ShouldBeTrue();

        var served = new FormLayout("User Story", "real-process-guid", []);
        FallbackFormLayout.IsFallback(served).ShouldBeFalse();
    }

    [Fact]
    public void AnEmptyServedLayout_IsNotTheSameFactAsNoLayout()
    {
        // The parse already distinguishes "no layout served" (null) from "an empty layout".
        // An empty served layout means the server says there are no controls; projecting it
        // must NOT sprout Twig-authored rows.
        var snapshot = Snapshot();
        var emptyServed = new FormLayout("User Story", "real-process-guid", []);

        var document = WorkItemDetailProjector.Project(emptyServed, snapshot);

        document.Pages.ShouldBeEmpty();
    }

    [Fact]
    public void For_DerivesLabelsFromReferenceNames_WithoutInventingNames()
    {
        var layout = FallbackFormLayout.For(Snapshot(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Scheduling.StoryPoints"] = "8",
        }));

        var control = layout.Pages
            .SelectMany(p => p.AllGroups)
            .SelectMany(g => g.Controls)
            .Single(c => c.Id == "Microsoft.VSTS.Scheduling.StoryPoints");

        control.Label.ShouldBe("Story Points");
    }

    [Fact]
    public void For_MarksEveryControlVisibleAndNonContribution()
    {
        var layout = FallbackFormLayout.For(Snapshot());

        foreach (var control in layout.Pages.SelectMany(p => p.AllGroups).SelectMany(g => g.Controls))
        {
            control.Visible.ShouldBeTrue();
            control.IsContribution.ShouldBeFalse();
        }
    }

    [Fact]
    public void For_EmitsASingleCustomPage_SoHostsWalkItLikeAnyOther()
    {
        var layout = FallbackFormLayout.For(Snapshot());

        var page = layout.Pages.ShouldHaveSingleItem();
        page.PageType.ShouldBe("custom");
        page.Id.ShouldBe(FallbackFormLayout.FallbackPageId);
    }

    [Fact]
    public void For_UsesTheSnapshotTypeName_AndFallsBackWhenBlank()
    {
        FallbackFormLayout.For(Snapshot()).WorkItemTypeReferenceName.ShouldBe("User Story");

        var typeless = Snapshot() with { TypeName = string.Empty };
        FallbackFormLayout.For(typeless).WorkItemTypeReferenceName.ShouldBe("Unknown");
    }

    [Fact]
    public void For_RejectsNull() =>
        Should.Throw<ArgumentNullException>(() => FallbackFormLayout.For(null!));

    [Fact]
    public void FallbackFormLayout_TakesNoProviderStoreOrRendererDependency()
    {
        // 0002 §10 / 0003: read-only construction must never require a store, and the
        // shared module must not acquire framework or lifecycle types.
        var referenced = typeof(FallbackFormLayout).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToList();

        referenced.ShouldNotContain("Terminal.Gui");
        referenced.ShouldNotContain("Spectre.Console");
        referenced.ShouldNotContain("Twig.Infrastructure");

        typeof(FallbackFormLayout)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(m => m.GetParameters())
            .Select(p => p.ParameterType.Name)
            .ShouldNotContain("IPendingChangeStore");
    }
}
