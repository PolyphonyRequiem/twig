using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Projections;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Tui.Tests;

/// <summary>
/// Pins the TUI's single acquisition seam (wayfinder-detail-projection ticket 0004):
/// the one place that decides which layout an item's form uses.
/// </summary>
public class DetailDocumentSourceTests
{
    private static FormLayout ServedLayout(params string[] fieldRefs) =>
        new("User Story", "real-process-guid",
        [
            new LayoutPage("p1", "Details", "custom", true, false,
            [
                new LayoutSection("s1",
                [
                    new LayoutGroup("g1", "Group", true, false,
                        fieldRefs.Select(r =>
                            new LayoutControl(r, r, "FieldControl", false, true, false)).ToList()),
                ]),
            ]),
        ]);

    private static Twig.Domain.Aggregates.WorkItem Item() =>
        new WorkItemBuilder(42, "Story")
            .AsUserStory()
            .InState("Active")
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project\\Team")
            .WithField("Microsoft.VSTS.Common.Priority", "2")
            .Build();

    private static IReadOnlyList<string> FieldRefsOf(WorkItemDetailDocument document) =>
        document.Pages.SelectMany(p => p.AllGroups).SelectMany(g => g.Controls)
            .Select(c => c.Id).ToList();

    [Fact]
    public async Task GetAsync_ServedLayout_ProjectsIt()
    {
        var provider = Substitute.For<IFormLayoutProvider>();
        provider.GetFormLayoutAsync("User Story", Arg.Any<CancellationToken>())
            .Returns(new FormLayoutResult.Served(ServedLayout("System.Title", "Contoso.Compliance.ReviewTicket")));

        var document = await new DetailDocumentSource(provider).GetAsync(Item());

        FieldRefsOf(document).ShouldBe(["System.Title", "Contoso.Compliance.ReviewTicket"]);
    }

    [Fact]
    public async Task GetAsync_NoLayoutServed_FallsBackToATwigAuthoredLayout()
    {
        var provider = Substitute.For<IFormLayoutProvider>();
        provider.GetFormLayoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FormLayoutResult.Unavailable());

        var document = await new DetailDocumentSource(provider).GetAsync(Item());

        // Core fields present — a fallback that enumerated Fields alone would have lost them.
        FieldRefsOf(document).ShouldContain("System.Title");
        FieldRefsOf(document).ShouldContain("Microsoft.VSTS.Common.Priority");
    }

    [Fact]
    public async Task GetAsync_EmptyServedLayout_IsNotTreatedAsNoLayout()
    {
        // 🔴 The two absent-layout cases stay distinct. An empty SERVED layout means the
        // server says there are no controls; collapsing it into the fallback would make an
        // empty server form silently sprout Twig-authored rows.
        var provider = Substitute.For<IFormLayoutProvider>();
        provider.GetFormLayoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FormLayoutResult.Served(new FormLayout("User Story", "real-process-guid", [])));

        var document = await new DetailDocumentSource(provider).GetAsync(Item());

        document.Pages.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAsync_ProviderThrows_DegradesToTheFallback()
    {
        var provider = Substitute.For<IFormLayoutProvider>();
        provider.GetFormLayoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<FormLayoutResult>>(_ => throw new HttpRequestException("unreachable"));

        var document = await new DetailDocumentSource(provider).GetAsync(Item());

        FieldRefsOf(document).ShouldContain("System.Title");
    }

    [Fact]
    public async Task GetAsync_LockedType_DegradesToTheFallbackLikeAnAbsentLayout()
    {
        // 🔴 A LOCKED type (AB#247) is a THIRD provider answer, and this surface has nowhere
        // to show the distinction — but it must still paint the pane rather than blank it.
        // The production switch maps Locked and Unavailable to the fallback explicitly, so a
        // future FOURTH arm crashes there instead of silently inheriting this behaviour.
        var provider = Substitute.For<IFormLayoutProvider>();
        provider.GetFormLayoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FormLayoutResult.Locked("Microsoft.VSTS.WorkItemTypes.TestCase"));

        var document = await new DetailDocumentSource(provider).GetAsync(Item());

        FieldRefsOf(document).ShouldContain("System.Title");
    }

    [Fact]
    public async Task GetAsync_NoProviderAtAll_StillProducesADocument()
    {
        var document = await new DetailDocumentSource(layoutProvider: null).GetAsync(Item());

        FieldRefsOf(document).ShouldContain("System.Title");
    }

    [Fact]
    public async Task GetAsync_CachesTheLayoutPerType()
    {
        // The tree fires a selection on every keypress; the provider hits ADO REST.
        var provider = Substitute.For<IFormLayoutProvider>();
        provider.GetFormLayoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FormLayoutResult.Served(ServedLayout("System.Title")));

        var source = new DetailDocumentSource(provider);
        await source.GetAsync(Item());
        await source.GetAsync(Item());
        await source.GetAsync(Item());

        await provider.Received(1).GetFormLayoutAsync("User Story", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_BothBranchesReturnTheSameDocumentType()
    {
        // The migration's load-bearing property: a host downstream cannot tell the served
        // path from the fallback path, so it cannot special-case either.
        var served = Substitute.For<IFormLayoutProvider>();
        served.GetFormLayoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FormLayoutResult.Served(ServedLayout("System.Title")));

        var absent = Substitute.For<IFormLayoutProvider>();
        absent.GetFormLayoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FormLayoutResult.Unavailable());

        var a = await new DetailDocumentSource(served).GetAsync(Item());
        var b = await new DetailDocumentSource(absent).GetAsync(Item());

        a.ShouldBeOfType<WorkItemDetailDocument>();
        b.ShouldBeOfType<WorkItemDetailDocument>();
    }
}
