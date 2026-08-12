using System.Net;
using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
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
        // 🔴 The PROCESS roster is what the layout fetch resolves against (AB#247), so it is
        // the one that must be stubbed. The project roster above is left in place because
        // sibling fetches still use it — and because the two disagreeing is the real shape.
        //
        // 🔴 This fixture gives BOTH rosters the same reference name, so the parse tests
        // below cannot detect wrong-roster resolution. That discrimination lives entirely in
        // HandlerWithDivergentRosters — do not delete that fixture as redundant.
        handler.SetProcessWorkItemTypesResponse((typeName, $"System.{typeName.Replace(" ", "")}"));
        handler.SetRawResponse("/layout", layoutJson);
        return handler;
    }

    /// <summary>
    /// Unwraps the <see cref="FormLayoutResult.Served"/> arm, failing the test with a
    /// readable message on any other arm. Keeps the existing assertions about parsing
    /// focused on the layout rather than on the arm-matching.
    /// </summary>
    private static async Task<FormLayout> ServedAsync(
        AdoIterationService service,
        string typeName)
    {
        var result = await service.GetFormLayoutAsync(typeName);
        result.ShouldBeOfType<FormLayoutResult.Served>();
        return ((FormLayoutResult.Served)result).Layout;
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

        var layout = await ServedAsync(service, "Bug");

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

        var layout = await ServedAsync(service, "Bug");

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

        var layout = await ServedAsync(service, "Bug");

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

        var layout = await ServedAsync(service, "Bug");

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

        var layout = await ServedAsync(service, "Bug");

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

        var layout = await ServedAsync(service, "Bug");

        layout.WorkItemTypeReferenceName.ShouldBe("System.Bug");
        layout.ProcessId.ShouldBe(ProcessId);
    }

    /// <summary>
    /// An unknown type must report <see cref="FormLayoutResult.Unavailable"/> rather than an
    /// empty layout. Ticket 1004 carries an open question — whether stock processes serve a
    /// layout at all — and collapsing "no layout" into "layout with no pages" would make
    /// that unanswerable from the command's output.
    /// </summary>
    [Fact]
    public async Task GetFormLayoutAsync_ReturnsUnavailableForUnknownType()
    {
        var service = FakeHandler.CreateService(HandlerWithProcess("""{"pages":[]}"""));

        var result = await service.GetFormLayoutAsync("NoSuchType");

        result.ShouldBeOfType<FormLayoutResult.Unavailable>();
    }

    /// <summary>
    /// An empty page list is a real, different answer from no layout at all — it must
    /// survive as an empty layout rather than collapsing to null.
    /// </summary>
    [Fact]
    public async Task GetFormLayoutAsync_DistinguishesEmptyLayoutFromNoLayout()
    {
        var service = FakeHandler.CreateService(HandlerWithProcess("""{"pages":[]}"""));

        var layout = await ServedAsync(service, "Bug");

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

    // ═══════════════════════════════════════════════════════════════
    //  AB#247 — resolution against the PROCESS roster, both name forms
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds the roster shape that made this defect real: the PROJECT lists the type under
    /// its stock parent's reference name, while the PROCESS lists the same display name
    /// under its own. Only one of them is the right answer.
    /// </summary>
    private static FakeHandler HandlerWithDivergentRosters(string layoutJson)
    {
        var handler = new FakeHandler();
        handler.SetRawResponse(
            "/_apis/projects/",
            "{\"capabilities\":{\"processTemplate\":{\"templateName\":\"Niflheim\",\"templateTypeId\":\""
                + ProcessId + "\"}}}");
        // PROJECT roster: reports the STOCK parent's reference name for 'Task'.
        handler.SetRawResponse(
            "/_apis/wit/workitemtypes",
            """
            {"count":1,"value":[{"name":"Task","description":"",
              "referenceName":"Microsoft.VSTS.WorkItemTypes.Task",
              "color":"AABBCC","icon":{"id":"i","url":"https://example.com"},"isDisabled":false}]}
            """);
        // PROCESS roster: reports the process's OWN reference name for the same display name.
        handler.SetProcessWorkItemTypesResponse(("Task", "Niflheim.Task"));
        handler.SetRawResponse("/layout", layoutJson);
        return handler;
    }

    /// <summary>
    /// 🔴 The display name must resolve through the PROCESS roster, not the project's.
    /// </summary>
    /// <remarks>
    /// Verified live: the project route reports <c>Task</c> as
    /// <c>Microsoft.VSTS.WorkItemTypes.Task</c> — the stock PARENT — while the process route
    /// reports <c>Niflheim.Task</c>. Resolving through the project therefore fetched the
    /// parent type's form and labelled it with the parent's identity. The two forms are
    /// identical today, so the defect was invisible in the output; the moment a child form
    /// is customized it would silently serve the wrong one.
    /// <para>
    /// Asserts the reference name that came back, not merely that something did — asserting
    /// non-null would pass against the unfixed code, which also returned a layout.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetFormLayoutAsync_ResolvesDisplayNameAgainstTheProcessRosterNotTheProjects()
    {
        var service = FakeHandler.CreateService(
            HandlerWithDivergentRosters("""{"pages":[]}"""));

        var layout = await ServedAsync(service, "Task");

        layout.WorkItemTypeReferenceName.ShouldBe("Niflheim.Task");
        layout.WorkItemTypeReferenceName.ShouldNotBe("Microsoft.VSTS.WorkItemTypes.Task");
    }

    /// <summary>
    /// 🔴 A process REFERENCE name is accepted too — the inconsistency AB#247 names first.
    /// Before the fix, <c>process layout Niflheim.Task</c> failed while
    /// <c>process description Niflheim.Task</c> succeeded.
    /// </summary>
    [Fact]
    public async Task GetFormLayoutAsync_AcceptsAProcessReferenceName()
    {
        var service = FakeHandler.CreateService(
            HandlerWithDivergentRosters("""{"pages":[]}"""));

        var layout = await ServedAsync(service, "Niflheim.Task");

        layout.WorkItemTypeReferenceName.ShouldBe("Niflheim.Task");
    }

    /// <summary>
    /// Both spellings reach the SAME type. This is the acceptance criterion stated as one
    /// assertion: the two sibling verbs agree on what a type is.
    /// </summary>
    [Fact]
    public async Task GetFormLayoutAsync_DisplayNameAndReferenceNameResolveToTheSameType()
    {
        var byDisplay = await ServedAsync(
            FakeHandler.CreateService(HandlerWithDivergentRosters("""{"pages":[]}""")), "Task");
        var byReference = await ServedAsync(
            FakeHandler.CreateService(HandlerWithDivergentRosters("""{"pages":[]}""")), "Niflheim.Task");

        byDisplay.WorkItemTypeReferenceName.ShouldBe(byReference.WorkItemTypeReferenceName);
    }

    /// <summary>
    /// 🔴 The stock PARENT type is NOT reachable, even named in full. Only process-roster
    /// rows are matched, and the roster's <c>inherits</c> field is not consulted.
    /// </summary>
    /// <remarks>
    /// This is a real capability loss the process-roster ruling accepted (AB#247, ticket
    /// 1004), and it is asserted rather than left implicit so that a future change which
    /// quietly starts following <c>inherits</c> has to come here and argue with this test
    /// instead of slipping past.
    /// </remarks>
    [Fact]
    public async Task GetFormLayoutAsync_StockParentReferenceName_IsUnavailable()
    {
        var service = FakeHandler.CreateService(
            HandlerWithDivergentRosters("""{"pages":[]}"""));

        var result = await service.GetFormLayoutAsync("Microsoft.VSTS.WorkItemTypes.Task");

        result.ShouldBeOfType<FormLayoutResult.Unavailable>();
    }

    /// <summary>
    /// 🔴 The process-roster request must carry the PINNED api-version. The production
    /// remarks call it load-bearing: at the neighbouring preview version the same URL
    /// returns id and class instead of referenceName and customization, so a version slip
    /// loses the identity this whole resolution depends on — and it does so WITHOUT
    /// changing the row count, which is what makes it invisible without this assertion.
    /// </summary>
    /// <remarks>
    /// 🔴 Asserts the LITERAL version string, not <c>AdoApiVersions.ProcessWorkItemTypes</c>.
    /// Comparing the URL against the same constant that built it is a constant-versus-itself
    /// tautology: editing the constant to a slipped version would keep this test green, which
    /// is precisely the defect it claims to guard. The literal makes a version change fail
    /// here and demand a deliberate update.
    /// </remarks>
    [Fact]
    public async Task GetFormLayoutAsync_RequestsTheProcessRosterAtThePinnedApiVersion()
    {
        var handler = new RecordingHandler();
        handler.SetRawResponse(
            "/_apis/projects/",
            "{\"capabilities\":{\"processTemplate\":{\"templateName\":\"Niflheim\",\"templateTypeId\":\""
                + ProcessId + "\"}}}");
        handler.SetProcessWorkItemTypesResponse(("Task", "Niflheim.Task"));
        handler.SetRawResponse("/layout", """{"pages":[]}""");

        await FakeHandler.CreateService(handler).GetFormLayoutAsync("Task");

        var rosterUrl = handler.RequestedUrls.SingleOrDefault(url =>
            url.Contains("/work/processes/", StringComparison.OrdinalIgnoreCase) &&
            url.Contains("/workItemTypes?", StringComparison.OrdinalIgnoreCase));

        rosterUrl.ShouldNotBeNull();
        rosterUrl.ShouldContain("api-version=7.1-preview.2");
    }

    /// <summary>Records every URL requested, so a test can assert on the request itself.</summary>
    private sealed class RecordingHandler : FakeHandler
    {
        private readonly List<string> _urls = [];

        public IReadOnlyList<string> RequestedUrls => _urls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _urls.Add(request.RequestUri!.ToString());
            return base.SendAsync(request, cancellationToken);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  AB#247 — locked types, and the narrowness of the catch
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 A LOCKED type answers the layout route with <b>400 VS403115</b>, not 404, and must
    /// come back as <see cref="FormLayoutResult.Locked"/> rather than propagating.
    /// </summary>
    /// <remarks>
    /// This is the case no seam test could previously reach: the fake handler only served
    /// 200 and 404, and a scripted source never returns a real 400. That gap is precisely
    /// how this failure reached production in the description path, found by running the
    /// command live rather than by the suite.
    /// </remarks>
    [Fact]
    public async Task GetFormLayoutAsync_LockedType_ReportsLockedRatherThanThrowing()
    {
        var handler = HandlerWithProcess("""{"pages":[]}""", "Test Case");
        // 🔴 One line. A literal newline inside a JSON string value makes the body invalid,
        // the server message fails to parse, and the exception arrives with a generic text
        // that does not carry the marker — which looks exactly like the catch being too
        // narrow. Cost one red run before it was spotted.
        handler.SetStatusResponse(
            "/layout",
            HttpStatusCode.BadRequest,
            """{"message":"VS403115: You cannot modify form layout information for work item types Microsoft.VSTS.WorkItemTypes.TestCase in process 7f98 as these work item types are locked."}""");
        var service = FakeHandler.CreateService(handler);

        var result = await service.GetFormLayoutAsync("Test Case");

        var locked = result.ShouldBeOfType<FormLayoutResult.Locked>();
        locked.TypeReferenceName.ShouldBe("System.TestCase");
    }

    /// <summary>
    /// 🔴 The locked catch is NARROWED to the VS403115 marker and must not swallow every
    /// 400. A malformed api-version or a bad escape is a genuine failure, and turning it
    /// into a quiet degraded answer would be the exception-swallow-too-broad regression —
    /// the same discipline the description's fetch already documents.
    /// </summary>
    /// <remarks>
    /// This test is the guard on that narrowness: it fails the moment someone widens the
    /// catch to <c>catch (AdoBadRequestException)</c>, which would otherwise look like a
    /// simplification.
    /// </remarks>
    [Fact]
    public async Task GetFormLayoutAsync_OtherBadRequest_PropagatesRatherThanDegrading()
    {
        var handler = HandlerWithProcess("""{"pages":[]}""");
        handler.SetStatusResponse(
            "/layout",
            HttpStatusCode.BadRequest,
            """{"message":"VS402337: The requested api-version is not supported."}""");
        var service = FakeHandler.CreateService(handler);

        await Should.ThrowAsync<AdoBadRequestException>(
            () => service.GetFormLayoutAsync("Bug"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  AB#247 — system controls are carried, not discarded
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 <c>systemControls</c> arrives in the SAME response as <c>pages</c> and was being
    /// deserialized and then thrown away, so the layout surface reported zero of them while
    /// the description reported nine per type.
    /// </summary>
    [Fact]
    public async Task GetFormLayoutAsync_CarriesTheServersSystemControls()
    {
        const string json = """
        {
          "pages": [],
          "systemControls": [
            { "id": "System.State", "label": "State", "controlType": "FieldControl",
              "visible": true, "order": 2 },
            { "id": "System.AssignedTo", "label": "Assigned To", "controlType": "FieldControl",
              "visible": true, "order": 1 }
          ]
        }
        """;

        var layout = await ServedAsync(
            FakeHandler.CreateService(HandlerWithProcess(json)), "Bug");

        // Ordered by the server's own `order` key, not array position — the same authority
        // the page controls are ordered by.
        layout.SystemControls.Select(c => c.Id)
            .ShouldBe(["System.AssignedTo", "System.State"]);
        layout.SystemControls[0].Label.ShouldBe("Assigned To");
        layout.SystemControls[0].ControlType.ShouldBe("FieldControl");
    }

    /// <summary>
    /// A layout whose response carries no <c>systemControls</c> key reports an empty list,
    /// not a null one — the caller must not need a null check for a fact the server simply
    /// did not state.
    /// </summary>
    [Fact]
    public async Task GetFormLayoutAsync_AbsentSystemControls_IsEmptyNotNull()
    {
        var layout = await ServedAsync(
            FakeHandler.CreateService(HandlerWithProcess("""{"pages":[]}""")), "Bug");

        layout.SystemControls.ShouldBeEmpty();
    }
}
