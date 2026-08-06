using Shouldly;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Covers <c>AdoIterationService.GetFormLayoutAsync</c> — the fetch-and-parse half of
/// wayfinder-1.0 ticket 1004, and the production input to the server-driven 1.0 editor.
/// </summary>
/// <remarks>
/// The behaviours worth pinning are the ones a naive implementation gets wrong:
/// section flattening, order honouring, the absent-<c>visible</c> default, and the
/// distinction between "no layout served" and "an empty layout".
/// </remarks>
public sealed class AdoIterationServiceFormLayoutTests
{
    private const string ProcessId = "adcc42ab-9882-485e-a3ed-7678f01f66bc";

    private static FakeHandler HandlerWithProcess(string layoutJson, string typeName = "Bug")
    {
        var handler = new FakeHandler();
        handler.SetRawResponse(
            "/_apis/projects/",
            "{\"capabilities\":{\"processTemplate\":{\"templateName\":\"Agile\",\"templateTypeId\":\""
                + ProcessId + "\"}}}");
        handler.SetWorkItemTypesResponse(typeName);
        handler.SetRawResponse("/layout", layoutJson);
        return handler;
    }

    /// <summary>
    /// Two sections each holding groups. Sections are ADO's COLUMNS, and the parse must
    /// preserve them — merging columns is a rendering decision, and a parse that discards
    /// them leaves no way back for a renderer that wants side-by-side placement.
    /// </summary>
    [Fact]
    public async Task GetFormLayoutAsync_PreservesSectionsAsColumns()
    {
        const string json = """
        {
          "pages": [
            {
              "id": "Agile.Bug.Bug",
              "label": "Details",
              "pageType": "custom",
              "visible": true,
              "sections": [
                { "id": "Section1", "groups": [
                    { "id": "g.repro", "label": "Repro Steps", "visible": true, "controls": [] } ] },
                { "id": "Section2", "groups": [
                    { "id": "g.planning", "label": "Planning", "visible": true, "controls": [] },
                    { "id": "g.effort", "label": "Effort", "visible": true, "controls": [] } ] }
              ]
            }
          ]
        }
        """;

        var service = FakeHandler.CreateService(HandlerWithProcess(json));

        var layout = await service.GetFormLayoutAsync("Bug");

        layout.ShouldNotBeNull();
        layout.Pages.Count.ShouldBe(1);

        var page = layout.Pages[0];
        page.Label.ShouldBe("Details");
        page.PageType.ShouldBe("custom");

        // Both columns survive, with their own groups, in server order.
        page.Sections.Count.ShouldBe(2);
        page.Sections[0].Id.ShouldBe("Section1");
        page.Sections[0].Groups.Select(g => g.Label).ShouldBe(["Repro Steps"]);
        page.Sections[1].Id.ShouldBe("Section2");
        page.Sections[1].Groups.Select(g => g.Label).ShouldBe(["Planning", "Effort"]);
    }

    /// <summary>
    /// The single-column projection is available for renderers that cannot place columns
    /// side by side — column-major, then group order. It is a convenience over the
    /// preserved structure, not a replacement for it.
    /// </summary>
    [Fact]
    public async Task GetFormLayoutAsync_AllGroupsMergesColumnsInOrder()
    {
        const string json = """
        {
          "pages": [
            {
              "id": "p1", "label": "Details", "pageType": "custom", "visible": true,
              "sections": [
                { "id": "Section1", "groups": [
                    { "id": "g.repro", "label": "Repro Steps", "controls": [] } ] },
                { "id": "Section2", "groups": [
                    { "id": "g.planning", "label": "Planning", "controls": [] },
                    { "id": "g.effort", "label": "Effort", "controls": [] } ] }
              ]
            }
          ]
        }
        """;

        var service = FakeHandler.CreateService(HandlerWithProcess(json));

        var layout = await service.GetFormLayoutAsync("Bug");

        layout.ShouldNotBeNull();
        layout.Pages[0].AllGroups.Select(g => g.Label)
            .ShouldBe(["Repro Steps", "Planning", "Effort"]);
    }

    /// <summary>
    /// ADO supplies an explicit <c>order</c> that need not match array position. Honouring
    /// it is the whole point of reading the layout — the editor's field order is the
    /// server's, not the JSON's.
    /// </summary>
    [Fact]
    public async Task GetFormLayoutAsync_OrdersControlsByOrderNotArrayPosition()
    {
        const string json = """
        {
          "pages": [
            {
              "id": "p1", "label": "Details", "pageType": "custom", "visible": true,
              "sections": [
                { "id": "Section1", "groups": [
                    { "id": "g1", "label": "Planning", "visible": true, "controls": [
                        { "id": "F.Third",  "label": "Third",  "controlType": "FieldControl", "order": 30 },
                        { "id": "F.First",  "label": "First",  "controlType": "FieldControl", "order": 10 },
                        { "id": "F.Second", "label": "Second", "controlType": "FieldControl", "order": 20 }
                    ] } ] }
              ]
            }
          ]
        }
        """;

        var service = FakeHandler.CreateService(HandlerWithProcess(json));

        var layout = await service.GetFormLayoutAsync("Bug");

        layout.ShouldNotBeNull();
        layout.Pages[0].Sections[0].Groups[0].Controls.Select(c => c.Label)
            .ShouldBe(["First", "Second", "Third"]);
    }

    /// <summary>
    /// ADO omits <c>visible</c> on the common case. Defaulting a missing flag to false
    /// would hide every ordinary field — the failure would look like an empty form rather
    /// than a parse bug, so it is pinned explicitly.
    /// </summary>
    [Fact]
    public async Task GetFormLayoutAsync_TreatsAbsentVisibleAsVisible()
    {
        const string json = """
        {
          "pages": [
            {
              "id": "p1", "label": "Details", "pageType": "custom",
              "sections": [
                { "id": "Section1", "groups": [
                    { "id": "g1", "label": "Planning", "controls": [
                        { "id": "F.Shown",  "label": "Shown",  "controlType": "FieldControl" },
                        { "id": "F.Hidden", "label": "Hidden", "controlType": "FieldControl", "visible": false }
                    ] } ] }
              ]
            }
          ]
        }
        """;

        var service = FakeHandler.CreateService(HandlerWithProcess(json));

        var layout = await service.GetFormLayoutAsync("Bug");

        layout.ShouldNotBeNull();
        var page = layout.Pages[0];
        page.Visible.ShouldBeTrue();

        var group = page.Sections[0].Groups[0];
        group.Visible.ShouldBeTrue();
        group.Controls.Single(c => c.Label == "Shown").Visible.ShouldBeTrue();
        group.Controls.Single(c => c.Label == "Hidden").Visible.ShouldBeFalse();
    }

    /// <summary>
    /// The control kind must survive the parse. The renderer decides later which kinds
    /// have a terminal form (rich text, links grids and attachments may not), and it
    /// cannot decide that if the kind was discarded here.
    /// </summary>
    [Fact]
    public async Task GetFormLayoutAsync_PreservesControlTypeAndFieldReferenceName()
    {
        const string json = """
        {
          "pages": [
            {
              "id": "p1", "label": "Details", "pageType": "custom",
              "sections": [
                { "id": "Section1", "groups": [
                    { "id": "g1", "label": "Repro", "controls": [
                        { "id": "Microsoft.VSTS.TCM.ReproSteps", "label": "Repro Steps",
                          "controlType": "HtmlFieldControl", "readOnly": true }
                    ] } ] }
              ]
            }
          ]
        }
        """;

        var service = FakeHandler.CreateService(HandlerWithProcess(json));

        var layout = await service.GetFormLayoutAsync("Bug");

        layout.ShouldNotBeNull();
        var control = layout.Pages[0].Sections[0].Groups[0].Controls.Single();
        control.Id.ShouldBe("Microsoft.VSTS.TCM.ReproSteps");
        control.ControlType.ShouldBe("HtmlFieldControl");
        control.ReadOnly.ShouldBeTrue();
    }

    /// <summary>
    /// The layout endpoint is keyed by the type's REFERENCE name, not its display name.
    /// Sending the display name yields a 404 against a real server, so the resolution
    /// step is pinned.
    /// </summary>
    [Fact]
    public async Task GetFormLayoutAsync_ReportsReferenceNameAndProcessId()
    {
        const string json = """{"pages":[]}""";

        var service = FakeHandler.CreateService(HandlerWithProcess(json));

        var layout = await service.GetFormLayoutAsync("Bug");

        layout.ShouldNotBeNull();
        layout.WorkItemTypeReferenceName.ShouldBe("System.Bug");
        layout.ProcessId.ShouldBe(ProcessId);
    }

    /// <summary>
    /// An unknown type must return null rather than an empty layout. Ticket 1004 carries
    /// an open question — whether stock processes serve a layout at all — and collapsing
    /// "no layout" into "layout with no pages" would make that unanswerable from the
    /// command's output.
    /// </summary>
    [Fact]
    public async Task GetFormLayoutAsync_ReturnsNullForUnknownType()
    {
        var service = FakeHandler.CreateService(HandlerWithProcess("""{"pages":[]}"""));

        var layout = await service.GetFormLayoutAsync("NoSuchType");

        layout.ShouldBeNull();
    }

    /// <summary>
    /// An empty page list is a real, different answer from no layout at all — it must
    /// survive as an empty layout rather than collapsing to null.
    /// </summary>
    [Fact]
    public async Task GetFormLayoutAsync_DistinguishesEmptyLayoutFromNoLayout()
    {
        var service = FakeHandler.CreateService(HandlerWithProcess("""{"pages":[]}"""));

        var layout = await service.GetFormLayoutAsync("Bug");

        layout.ShouldNotBeNull();
        layout.Pages.ShouldBeEmpty();
    }

    /// <summary>
    /// Layout retrieval is per-type and cached, matching the process-rules pair it sits
    /// beside. Refetching per call would multiply requests across an editor session.
    /// </summary>
    [Fact]
    public async Task GetFormLayoutAsync_CachesPerType()
    {
        var handler = HandlerWithProcess("""{"pages":[]}""");
        var service = FakeHandler.CreateService(handler);

        var first = await service.GetFormLayoutAsync("Bug");
        var second = await service.GetFormLayoutAsync("Bug");

        first.ShouldBeSameAs(second);
    }
}
