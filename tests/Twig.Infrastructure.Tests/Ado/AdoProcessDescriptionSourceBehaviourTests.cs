using System.Net;
using System.Text;
using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Wire-level tests for the three fetches AB#238 adds to
/// <c>AdoProcessDescriptionSource</c>: rules with their customization tag, per-type behaviour
/// membership, the behaviour catalogue, and the form layout.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>These exist because the seam tests cannot see this layer.</b> Every assembler test
/// drives a scripted <c>IProcessDescriptionSource</c>, which is the right shape for asserting
/// document behaviour and is structurally blind to deserialization. Independent review of
/// AB#237 found three defects living in exactly that blind spot, all of the same class the
/// feature exists to prevent — a failed fetch rendering as a confident claim — with the suite
/// green throughout. AB#238 adds three new fetches, so it adds three new instances of that
/// blind spot.
/// </para>
/// <para>
/// 🔴 <b>The count-shaped body is the hazard these are pointed at.</b> On this route family a
/// 404 arrives as <c>{"count":1,"value":{"Message":…}}</c> — the exact shape of a thin success.
/// The LIST fetches survive it by accident (their <c>value</c> is a <c>List&lt;T&gt;</c>, so an
/// object there throws); the LAYOUT response is a bare object whose only array-shaped member is
/// <c>pages</c>, so its guard has to be deliberate — and therefore has to be tested. The
/// behaviour membership ROWS are bare objects inside a list envelope, which is a third shape
/// again.
/// </para>
/// <para>
/// 🔴 <b>The membership route segment is <c>workItemTypesBehaviors</c>.</b> The obvious
/// <c>workItemTypes/{ref}/behaviors</c> returns an HTML 404 for every type on every arm,
/// verified live. The URL is asserted here because a wrong route yields an empty answer with
/// exit 0 — the silent failure this whole feature exists to prevent.
/// </para>
/// </remarks>
public sealed class AdoProcessDescriptionSourceBehaviourTests
{
    /// <summary>The literal count-shaped body this route family returns for a 404.</summary>
    private const string CountShaped404 =
        "{\"count\":1,\"value\":{\"Message\":\"VS403646: The resource does not exist.\"}}";

    /// <summary>The project payload that resolves the process id, so fetches can proceed.</summary>
    private const string ProjectBody =
        "{\"id\":\"p1\",\"name\":\"testproject\",\"capabilities\":{\"processTemplate\":"
        + "{\"templateName\":\"Niflheim\",\"templateTypeId\":\"proc-1\"}}}";

    /// <summary>
    /// Routes by URL fragment and RECORDS every URL requested.
    /// </summary>
    /// <remarks>
    /// The recording is what lets a test assert the membership route segment rather than only
    /// its result — a wrong route returns nothing, which is indistinguishable from a type that
    /// belongs to no backlog level.
    /// </remarks>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> RequestedUrls { get; } = [];

        public RoutingHandler() => Route("/_apis/projects/", ProjectBody);

        public RoutingHandler Route(
            string urlFragment, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes[urlFragment] = (status, body);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (RequestedUrls)
                RequestedUrls.Add(url);

            // Longest fragment first, so a specific route beats a generic prefix.
            foreach (var route in _routes.OrderByDescending(r => r.Key.Length))
            {
                if (url.Contains(route.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(route.Value.Status)
                    {
                        Content = new StringContent(route.Value.Body, Encoding.UTF8, "application/json"),
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(CountShaped404, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static AdoProcessDescriptionSource CreateSource(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) },
            new FakeAuthProvider(),
            "https://dev.azure.com/testorg",
            "testproject");

    // ═══════════════════════════════════════════════════════════════
    //  Rules — the customization tag off the wire
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A rule's <c>customizationType</c> reaches the domain from the wire, for every class.
    /// </summary>
    /// <remarks>
    /// 🔴 The whole carry-everything ruling rests on this tag being READ. A fetch layer that
    /// carried every rule but dropped the tag would pay the entire noise cost of the ruling and
    /// deliver none of its mitigation — the reader would have ~54 rules and no way to filter
    /// them.
    /// </remarks>
    [Fact]
    public async Task GetTypeDetail_RuleCustomizationType_IsReadOffTheWire()
    {
        var handler = new RoutingHandler().Route(
            "/workItemTypes/Niflheim.Epic/rules",
            "{\"count\":3,\"value\":["
            + "{\"customizationType\":\"system\",\"isDisabled\":false,\"name\":null,"
            + "\"conditions\":[{\"conditionType\":\"whenNotChanged\",\"field\":\"System.State\",\"value\":null}],"
            + "\"actions\":[{\"actionType\":\"makeReadOnly\",\"targetField\":\"System.Reason\",\"value\":null}]},"
            + "{\"customizationType\":\"custom\",\"isDisabled\":false,"
            + "\"name\":\"Epic must state what it delivered\","
            + "\"conditions\":[{\"conditionType\":\"when\",\"field\":\"System.State\",\"value\":\"Done\"}],"
            + "\"actions\":[{\"actionType\":\"makeRequired\",\"targetField\":\"Custom.Closing\",\"value\":null}]},"
            + "{\"customizationType\":\"inherited\",\"isDisabled\":true,\"name\":\"An inherited one\","
            + "\"conditions\":[],\"actions\":[]}"
            + "]}");

        var detail = await CreateSource(handler).GetTypeDetailAsync("Niflheim.Epic");

        detail.ShouldNotBeNull();
        detail.Rules.ShouldNotBeNull();
        detail.Rules.Count.ShouldBe(3);

        detail.Rules.Select(r => r.CustomizationOrUnknown.Kind).ShouldBe(
        [
            RuleCustomizationKind.System,
            RuleCustomizationKind.Custom,
            RuleCustomizationKind.Inherited,
        ]);

        // The server's own word is preserved alongside the classification.
        detail.Rules.Select(r => r.CustomizationOrUnknown.Token)
            .ShouldBe(["system", "custom", "inherited"]);

        // 🔴 `name` is legitimately null on system plumbing — verified live, all 53 system
        // rules on Niflheim.Epic carry it — and must not be manufactured.
        detail.Rules[0].Name.ShouldBeNull();
        detail.Rules[1].Name.ShouldBe("Epic must state what it delivered");
        detail.Rules[2].IsDisabled.ShouldBeTrue();
    }

    /// <summary>
    /// 🔴 A rule with NO <c>customizationType</c> key is <c>Unknown</c>, not <c>System</c>.
    /// </summary>
    /// <remarks>
    /// The reading a non-nullable <c>string</c> plus a null-coalesce would invite, and the
    /// dangerous one: the tag is the reader's FILTER, so mislabelling an authored rule as
    /// inherited plumbing invites the reader to throw it away. That undoes the carry-everything
    /// ruling from the far end while the document still technically carries everything.
    /// <para>
    /// Not hypothetical: this route is version-sensitive, and <c>7.1</c> carrying the key today
    /// is a fact about today's server rather than a contract.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetTypeDetail_RuleWithoutCustomizationType_IsUnknownNotSystem()
    {
        var handler = new RoutingHandler().Route(
            "/workItemTypes/Niflheim.Epic/rules",
            "{\"count\":1,\"value\":[{\"isDisabled\":false,\"conditions\":[],\"actions\":[]}]}");

        var detail = await CreateSource(handler).GetTypeDetailAsync("Niflheim.Epic");

        detail!.Rules!.Single().CustomizationOrUnknown.Kind.ShouldBe(
            RuleCustomizationKind.Unknown,
            "a missing key is not the server stating a class — reporting it as 'system' would "
            + "invite a reader to filter away a rule that may be authored");
    }

    /// <summary>
    /// An UNRECOGNISED customization token is <c>Unknown</c> with the token PRESERVED.
    /// </summary>
    /// <remarks>
    /// 🔴 Twig does not own this vocabulary. Guessing which known class a new server value
    /// resembles would be a confident claim about someone else's taxonomy, and discarding the
    /// token would make a new class invisible the moment it appeared.
    /// </remarks>
    [Fact]
    public async Task GetTypeDetail_UnrecognisedCustomizationToken_IsUnknownButPreserved()
    {
        var handler = new RoutingHandler().Route(
            "/workItemTypes/Niflheim.Epic/rules",
            "{\"count\":1,\"value\":[{\"customizationType\":\"somethingNew\",\"isDisabled\":false,"
            + "\"conditions\":[],\"actions\":[]}]}");

        var detail = await CreateSource(handler).GetTypeDetailAsync("Niflheim.Epic");
        var customization = detail!.Rules!.Single().CustomizationOrUnknown;

        customization.Kind.ShouldBe(RuleCustomizationKind.Unknown);
        customization.Token.ShouldBe(
            "somethingNew",
            "discarding an unrecognised class would make it invisible rather than merely "
            + "unclassified");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Behaviour membership — the route, and the row guard
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Membership is read from <c>workItemTypesBehaviors</c>, and the reference reaches the
    /// domain.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>The route segment is asserted, not merely the result.</b> The obvious route
    /// (<c>workItemTypes/{ref}/behaviors</c>) 404s for every type, and a wrong route here
    /// yields an empty membership list with exit 0 — indistinguishable from a type that
    /// genuinely belongs to no backlog level, which several types in this org do.
    /// </remarks>
    [Fact]
    public async Task GetTypeDetail_BehaviourMembership_UsesTheWorkItemTypesBehaviorsRoute()
    {
        var handler = new RoutingHandler().Route(
            "/workItemTypesBehaviors/Niflheim.Epic/behaviors",
            "{\"count\":1,\"value\":[{\"behavior\":{\"id\":\"Microsoft.VSTS.Basic.EpicBacklogBehavior\"},"
            + "\"isDefault\":true,\"isLegacyDefault\":true}]}");

        var detail = await CreateSource(handler).GetTypeDetailAsync("Niflheim.Epic");

        detail.ShouldNotBeNull();
        detail.Behaviours.ShouldNotBeNull();

        var membership = detail.Behaviours.Single();
        membership.ReferenceName.ShouldBe("Microsoft.VSTS.Basic.EpicBacklogBehavior");
        membership.IsDefault.ShouldBeTrue();
        // The NAME is resolved by the assembler's join against the catalogue, not here.
        membership.Name.ShouldBe(string.Empty);

        handler.RequestedUrls.ShouldContain(
            url => url.Contains("/workItemTypesBehaviors/", StringComparison.Ordinal),
            "the obvious /workItemTypes/{ref}/behaviors route 404s for every type, and getting "
            + "it wrong yields an empty answer with exit 0");
        detail.Unfetched!.ShouldNotContain("behaviours");
    }

    /// <summary>
    /// A membership ROW with no <c>behavior.id</c> is skipped, not carried as a membership of
    /// the empty-string behaviour.
    /// </summary>
    /// <remarks>
    /// 🔴 The row-level guard. The list envelope's <c>value</c> being a <c>List&lt;T&gt;</c>
    /// makes a count-shaped body throw, which covers the whole-response case — but the ROWS are
    /// bare objects, so a row that deserialized clean while carrying no reference would become
    /// a membership of <c>""</c>: a confident claim about a backlog level that does not exist.
    /// The other rows are still true, so it is skipped rather than failing the call.
    /// </remarks>
    [Fact]
    public async Task GetTypeDetail_BehaviourRowWithNoReference_IsSkippedNotCarriedAsEmpty()
    {
        var handler = new RoutingHandler().Route(
            "/workItemTypesBehaviors/Niflheim.Epic/behaviors",
            "{\"count\":2,\"value\":["
            + "{\"isDefault\":false},"
            + "{\"behavior\":{\"id\":\"Custom.Real\"},\"isDefault\":true}]}");

        var detail = await CreateSource(handler).GetTypeDetailAsync("Niflheim.Epic");

        detail!.Behaviours!.Select(b => b.ReferenceName).ShouldBe(
            ["Custom.Real"],
            "a row naming no behaviour is not a membership of the empty-string behaviour");
    }

    /// <summary>
    /// A count-shaped 404 on the membership route is a FAILURE, labelled — not "belongs to no
    /// backlog level".
    /// </summary>
    /// <remarks>
    /// 🔴 The two are indistinguishable downstream unless the failure is named, and the second
    /// is TRUE for several types in this org — so the collapse would be plausible-looking and
    /// wrong. The label is what tells a reader which one they are holding.
    /// </remarks>
    [Fact]
    public async Task GetTypeDetail_WhenMembershipFails_IsUnfetchedNotAnEmptyMembershipList()
    {
        // The membership route is unregistered, so the handler answers the count-shaped 404.
        var handler = new RoutingHandler().Route(
            "/workItemTypes/Niflheim.Epic/fields",
            "{\"count\":1,\"value\":[{\"referenceName\":\"System.Title\",\"name\":\"Title\","
            + "\"type\":\"string\"}]}");

        var detail = await CreateSource(handler).GetTypeDetailAsync("Niflheim.Epic");

        detail.ShouldNotBeNull();
        detail.Behaviours.ShouldBeNull(
            "an empty list would claim the type appears on no backlog level, on the strength "
            + "of a call that failed");
        detail.Unfetched!.ShouldContain("behaviours");
    }

    /// <summary>
    /// A type that genuinely belongs to NO backlog level reports an empty list, not a failure.
    /// </summary>
    /// <remarks>
    /// 🔴 The other side of the guard. Without this, an implementation that reported every
    /// membership fetch as failed would pass the test above while making the distinction
    /// worthless — and several types in this org really do belong to nothing.
    /// </remarks>
    [Fact]
    public async Task GetTypeDetail_TypeOnNoBacklog_IsEmptyNotUnfetched()
    {
        var handler = new RoutingHandler().Route(
            "/workItemTypesBehaviors/Niflheim.Task/behaviors",
            "{\"count\":0,\"value\":[]}");

        var detail = await CreateSource(handler).GetTypeDetailAsync("Niflheim.Task");

        detail!.Behaviours.ShouldNotBeNull();
        detail.Behaviours.ShouldBeEmpty();
        detail.Unfetched!.ShouldNotContain(
            "behaviours",
            "a type that genuinely belongs to no backlog level is a real answer, not a failure");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Behaviour catalogue
    // ═══════════════════════════════════════════════════════════════

    /// <summary>The catalogue's names and ranks reach the domain.</summary>
    /// <remarks>
    /// The positive half. Without it every negative below could be satisfied by a source that
    /// resolves nothing at all.
    /// </remarks>
    [Fact]
    public async Task GetBehaviourCatalogue_ReadsNamesAndRanks()
    {
        var handler = new RoutingHandler().Route(
            "/processes/proc-1/behaviors",
            "{\"count\":2,\"value\":["
            + "{\"referenceName\":\"Custom.3daa3b35\",\"name\":\"Wayfinding\",\"rank\":40,"
            + "\"customization\":\"custom\"},"
            + "{\"referenceName\":\"Microsoft.VSTS.Basic.EpicBacklogBehavior\",\"name\":\"Epics\","
            + "\"rank\":30}]}");

        var catalogue = await CreateSource(handler).GetBehaviourCatalogueAsync();

        catalogue.ShouldNotBeNull();
        catalogue.Count.ShouldBe(2);
        catalogue[0].ReferenceName.ShouldBe("Custom.3daa3b35");
        catalogue[0].Name.ShouldBe("Wayfinding");
        catalogue[0].Rank.ShouldBe(40);
        catalogue[1].Name.ShouldBe("Epics");
    }

    /// <summary>
    /// A failed catalogue call returns <c>null</c>, never an empty list.
    /// </summary>
    /// <remarks>
    /// 🔴 An empty catalogue asserts the process defines no backlog levels at all — a positive
    /// claim built on a call that never came back — while silently stripping every membership
    /// of its name. <c>null</c> is what makes the assembler label the affected types.
    /// </remarks>
    [Fact]
    public async Task GetBehaviourCatalogue_WhenTheCallFails_ReturnsNullNotAnEmptyList()
    {
        // Unregistered: the handler answers the count-shaped 404.
        var catalogue = await CreateSource(new RoutingHandler()).GetBehaviourCatalogueAsync();

        catalogue.ShouldBeNull(
            "an empty catalogue would claim the process defines no backlog levels on the "
            + "strength of a call that failed");
    }

    /// <summary>A catalogue row with no reference name is skipped — it can name nothing.</summary>
    [Fact]
    public async Task GetBehaviourCatalogue_RowWithNoReferenceName_IsSkipped()
    {
        var handler = new RoutingHandler().Route(
            "/processes/proc-1/behaviors",
            "{\"count\":2,\"value\":[{\"name\":\"Nameless\",\"rank\":1},"
            + "{\"referenceName\":\"Custom.Real\",\"name\":\"Real\",\"rank\":2}]}");

        var catalogue = await CreateSource(handler).GetBehaviourCatalogueAsync();

        catalogue!.Select(b => b.ReferenceName).ShouldBe(["Custom.Real"]);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Form layout — the deliberate count-shaped-body guard
    // ═══════════════════════════════════════════════════════════════

    /// <summary>The layout's four levels and their order keys reach the domain.</summary>
    /// <remarks>
    /// 🔴 The <c>order</c> key is the point. Without it the assembler has nothing faithful to
    /// sort on, and the document's layout would either be alphabetised — destroying the
    /// arrangement, which IS the content — or left in unprovable array order.
    /// </remarks>
    [Fact]
    public async Task GetTypeDetail_FormLayout_CarriesEveryLevelWithItsOrderKey()
    {
        var handler = new RoutingHandler().Route(
            "/workItemTypes/Niflheim.Epic/layout",
            "{\"pages\":[{\"id\":\"Basic.Epic.Epic\",\"label\":\"Details\",\"pageType\":\"custom\","
            + "\"visible\":true,\"inherited\":true,\"isContribution\":false,\"order\":0,"
            + "\"sections\":[{\"id\":\"Section1\",\"groups\":[{\"id\":\"G1\",\"label\":\"Description\","
            + "\"visible\":true,\"inherited\":true,\"isContribution\":false,\"order\":0,"
            + "\"controls\":[{\"id\":\"System.Description\",\"label\":\"Description\","
            + "\"controlType\":\"HtmlFieldControl\",\"readOnly\":false,\"visible\":true,"
            + "\"inherited\":true,\"isContribution\":false,\"order\":0}]}]}]},"
            + "{\"id\":\"Links\",\"label\":\"Links\",\"pageType\":\"links\",\"visible\":true,"
            + "\"order\":1,\"sections\":[]}]}");

        var detail = await CreateSource(handler).GetTypeDetailAsync("Niflheim.Epic");

        detail!.Layout.ShouldNotBeNull();
        detail.Layout.Pages.Count.ShouldBe(2);

        var page = detail.Layout.Pages[0];
        page.Id.ShouldBe("Basic.Epic.Epic");
        page.PageType.ShouldBe("custom");
        page.Order.ShouldBe(0);
        page.Inherited.ShouldBeTrue();

        var control = page.Sections.Single().Groups.Single().Controls.Single();
        control.Id.ShouldBe("System.Description");
        // 🔴 Verbatim: the reader compares the server's vocabulary, not Twig's paraphrase.
        control.ControlType.ShouldBe("HtmlFieldControl");
        control.Order.ShouldBe(0);
        control.Inherited.ShouldBeTrue();

        // 🔴 A non-custom page is carried even though it holds no field controls. A process
        // that removed the links tab differs from one that did not, and dropping these pages
        // would diff clean over exactly that.
        detail.Layout.Pages[1].PageType.ShouldBe("links");

        detail.Unfetched!.ShouldNotContain("formLayout");
    }

    /// <summary>
    /// 🔴 The layout's <c>systemControls</c> are carried, not deserialized and discarded.
    /// </summary>
    /// <remarks>
    /// They arrive in the SAME response as <c>pages</c> — state, reason, assigned-to, area and
    /// iteration path, tags — so they are reachable, and the carry-everything ruling reaches
    /// them. An earlier draft mapped the DTO member and then never projected it, which would
    /// have been an omission with no marker while the document's header simultaneously claimed
    /// it made no reservations: the false-completeness failure the feature exists to prevent.
    /// </remarks>
    [Fact]
    public async Task GetTypeDetail_LayoutSystemControls_AreCarriedNotDiscarded()
    {
        var handler = new RoutingHandler().Route(
            "/workItemTypes/Niflheim.Epic/layout",
            "{\"pages\":[{\"id\":\"P\",\"label\":\"Details\",\"pageType\":\"custom\",\"order\":0,"
            + "\"sections\":[]}],"
            + "\"systemControls\":[{\"id\":\"System.State\",\"label\":\"State\","
            + "\"controlType\":\"FieldControl\",\"readOnly\":false,\"visible\":true,"
            + "\"inherited\":true,\"isContribution\":false,\"order\":0}]}");

        var detail = await CreateSource(handler).GetTypeDetailAsync("Niflheim.Epic");

        var systemControl = detail!.Layout!.SystemControls.Single();
        systemControl.Id.ShouldBe("System.State");
        systemControl.ControlType.ShouldBe("FieldControl");
        systemControl.Inherited.ShouldBeTrue();
    }

    /// <summary>
    /// 🔴 A count-shaped 404 on the layout route is a FAILURE, not a form with no pages.
    /// </summary>
    /// <remarks>
    /// <b>This is the AB#237 defect class in its new home.</b> The count-shaped body carries
    /// none of the layout DTO's keys, and <c>System.Text.Json</c> ignores unmapped members — so
    /// it deserializes cleanly into an all-null instance rather than throwing. The sibling LIST
    /// fetches survive it only by accident, because their <c>value</c> is an array and the
    /// error body puts an object there; this DTO is a bare object and falls outside that
    /// accidental defence. Untreated, the document would report the type's form as empty:
    /// a positive claim built on a call that failed.
    /// </remarks>
    [Fact]
    public async Task GetTypeDetail_CountShaped404OnTheLayout_IsUnfetchedNotAnEmptyForm()
    {
        // A surviving fetch, so the type is describable and the LAYOUT guard is what is under
        // test — otherwise the all-parts-failed branch returns null and this asserts nothing.
        var handler = new RoutingHandler()
            .Route(
                "/workItemTypes/Niflheim.Epic/fields",
                "{\"count\":1,\"value\":[{\"referenceName\":\"System.Title\",\"name\":\"Title\","
                + "\"type\":\"string\"}]}")
            .Route("/workItemTypes/Niflheim.Epic/layout", CountShaped404, HttpStatusCode.NotFound);

        var detail = await CreateSource(handler).GetTypeDetailAsync("Niflheim.Epic");

        detail.ShouldNotBeNull();
        detail.Layout.ShouldBeNull(
            "a failed layout fetch must not be reported as a form with no pages — that is the "
            + "strongest possible positive claim built on a call that failed");
        detail.Unfetched!.ShouldContain("formLayout");
    }

    /// <summary>
    /// The same guard for a count-shaped body served with a 200, which no exception path
    /// catches at all.
    /// </summary>
    /// <remarks>
    /// 🔴 The case the <c>AdoNotFoundException</c> catch cannot reach, and the reason the guard
    /// is a positive envelope CHECK rather than more catch clauses. A real layout always
    /// carries <c>pages</c>; its absence is the tell.
    /// </remarks>
    [Fact]
    public async Task GetTypeDetail_CountShapedLayoutBodyWith200_IsStillUnfetched()
    {
        var handler = new RoutingHandler()
            .Route(
                "/workItemTypes/Niflheim.Epic/fields",
                "{\"count\":1,\"value\":[{\"referenceName\":\"System.Title\",\"name\":\"Title\","
                + "\"type\":\"string\"}]}")
            .Route("/workItemTypes/Niflheim.Epic/layout", CountShaped404);

        var detail = await CreateSource(handler).GetTypeDetailAsync("Niflheim.Epic");

        detail!.Layout.ShouldBeNull();
        detail.Unfetched!.ShouldContain("formLayout");
    }

    /// <summary>
    /// A layout that genuinely has an empty <c>pages</c> array is carried as a present layout
    /// with no pages — the guard must not swallow it.
    /// </summary>
    /// <remarks>
    /// 🔴 The other side of the guard, keyed on the PRESENCE of <c>pages</c> rather than on its
    /// emptiness. "We could not read the layout" and "the server served a layout with no pages"
    /// are different claims about the process, and only the presence check keeps them
    /// distinguishable.
    /// </remarks>
    [Fact]
    public async Task GetTypeDetail_LayoutWithAnEmptyPagesArray_IsPresentNotUnfetched()
    {
        var handler = new RoutingHandler().Route(
            "/workItemTypes/Niflheim.Epic/layout", "{\"pages\":[],\"systemControls\":[]}");

        var detail = await CreateSource(handler).GetTypeDetailAsync("Niflheim.Epic");

        detail!.Layout.ShouldNotBeNull(
            "a layout that exists and has no pages is a real state, which is not the same as "
            + "the layout being unreadable");
        detail.Layout.Pages.ShouldBeEmpty();
        detail.Unfetched!.ShouldNotContain("formLayout");
    }

    /// <summary>
    /// 🔴 A LOCKED system type answers the layout route with <b>400 VS403115</b>, and that must
    /// not fail the whole description.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found by running the command live, not by a test.</b> The seam tests drive a scripted
    /// source that never returns a 400, and the wire tests until now only exercised 404 and
    /// count-shaped bodies. Against the real process, <c>TestCase</c>, <c>TestPlan</c> and
    /// <c>TestSuite</c> are all locked, the layout route answers
    /// <i>"you cannot modify form layout information … as these work item types are locked"</i>
    /// with a 400, and the unhandled <c>AdoBadRequestException</c> propagated out of
    /// <c>GetTypeDetailAsync</c> and killed the entire run — 14 types lost to one type's answer,
    /// with a green suite behind it.
    /// </para>
    /// <para>
    /// 🔴 Swallowed rather than re-raised because it IS an answer: the process will not serve a
    /// layout for this type, ever. That is a fact about the type, not a transport failure. It is
    /// reported as <c>formLayout</c> unfetched — the honest weaker claim, since this layer
    /// cannot distinguish "locked" from "call failed", and an empty layout would assert the
    /// type's form has no pages.
    /// </para>
    /// <para>
    /// Verified live 2026-08-12 that this hazard is layout-ONLY: the rules, states, fields and
    /// behaviour routes all answer normally on the same locked type (55 rules, 3 states, 49
    /// fields, 0 behaviours), so a blanket 400-swallow across the fetch layer would be a wider
    /// change than the evidence supports.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetTypeDetail_LockedTypeReturns400OnLayout_IsUnfetchedAndDoesNotFailTheType()
    {
        const string Locked400 =
            "{\"$id\":\"1\",\"innerException\":null,\"message\":\"VS403115:You cannot modify form "
            + "layout information for work item types Microsoft.VSTS.WorkItemTypes.TestCase in "
            + "process proc-1 as these work item types are locked.\","
            + "\"typeName\":\"Microsoft.TeamFoundation.WorkItemTracking.Server.FormLayout."
            + "FormLayoutInfoNotAvailableException\"}";

        var handler = new RoutingHandler()
            .Route(
                "/workItemTypes/Microsoft.VSTS.WorkItemTypes.TestCase/layout",
                Locked400,
                HttpStatusCode.BadRequest)
            // The parts a locked type DOES serve, so the assertion is that the type survives
            // rather than that everything failed together.
            .Route(
                "/workItemTypes/Microsoft.VSTS.WorkItemTypes.TestCase/fields",
                "{\"count\":1,\"value\":[{\"referenceName\":\"System.Title\",\"name\":\"Title\","
                + "\"type\":\"string\"}]}")
            .Route(
                "/workItemTypesBehaviors/Microsoft.VSTS.WorkItemTypes.TestCase/behaviors",
                "{\"count\":0,\"value\":[]}");

        var detail = await CreateSource(handler)
            .GetTypeDetailAsync("Microsoft.VSTS.WorkItemTypes.TestCase");

        // 🔴 The type is still described. Before the fix this threw, and one locked type took
        // the whole document with it.
        detail.ShouldNotBeNull(
            "a locked type's layout answer must not fail the type, let alone the run");

        detail.Fields.Single().ReferenceName.ShouldBe("System.Title");
        detail.Layout.ShouldBeNull();
        detail.Unfetched!.ShouldContain("formLayout");
    }

    /// <summary>
    /// 🔴 A 400 that is NOT the locked-type answer PROPAGATES rather than being swallowed.
    /// </summary>
    /// <remarks>
    /// The bound on the catch above. Swallowing every 400 would turn a malformed api-version, a
    /// bad reference-name escape, or a future validation error into a silent
    /// "formLayout unfetched" with exit 0 — the exception-swallow-too-broad failure, and
    /// asymmetric with every sibling fetch, which swallows only <c>AdoNotFoundException</c> and
    /// <c>JsonException</c>. Proving the swallow HAPPENS is not enough; this proves it is
    /// bounded.
    /// </remarks>
    [Fact]
    public async Task GetTypeDetail_UnrelatedBadRequestOnLayout_Propagates()
    {
        var handler = new RoutingHandler().Route(
            "/workItemTypes/Niflheim.Epic/layout",
            "{\"$id\":\"1\",\"message\":\"VS402337: The api-version is not valid.\"}",
            HttpStatusCode.BadRequest);

        await Should.ThrowAsync<AdoBadRequestException>(
            async () => await CreateSource(handler).GetTypeDetailAsync("Niflheim.Epic"));
    }

    /// <summary>
    /// A type whose every part fails is <c>null</c> — including the three parts this ticket
    /// adds.
    /// </summary>
    /// <remarks>
    /// 🔴 The all-failed check must consider the NEW fetches too. Left unextended, a type whose
    /// fields, states, transitions and rules all failed but whose behaviours came back empty
    /// would produce a document row claiming an empty type rather than reporting that the type
    /// could not be described.
    /// </remarks>
    [Fact]
    public async Task GetTypeDetail_WhenEveryPartFails_ReturnsNull()
    {
        // Only the project route is registered; every per-type route answers the 404.
        var detail = await CreateSource(new RoutingHandler()).GetTypeDetailAsync("Niflheim.Epic");

        detail.ShouldBeNull();
    }

    /// <summary>
    /// A PARTIAL failure keeps the parts that succeeded and labels only the ones that did not.
    /// </summary>
    /// <remarks>
    /// The counterpart to the all-failed case: discarding good answers because a neighbouring
    /// fetch failed would trade a lot of truth for no extra honesty.
    /// </remarks>
    [Fact]
    public async Task GetTypeDetail_PartialFailure_KeepsWhatSucceededAndLabelsTheRest()
    {
        var handler = new RoutingHandler()
            .Route(
                "/workItemTypesBehaviors/Niflheim.Epic/behaviors",
                "{\"count\":1,\"value\":[{\"behavior\":{\"id\":\"Custom.Real\"},\"isDefault\":true}]}")
            // rules and layout are unregistered → count-shaped 404.
            .Route(
                "/workItemTypes/Niflheim.Epic/fields",
                "{\"count\":1,\"value\":[{\"referenceName\":\"System.Title\",\"name\":\"Title\","
                + "\"type\":\"string\"}]}");

        var detail = await CreateSource(handler).GetTypeDetailAsync("Niflheim.Epic");

        detail.ShouldNotBeNull();
        detail.Behaviours!.Single().ReferenceName.ShouldBe("Custom.Real");
        detail.Fields.Single().ReferenceName.ShouldBe("System.Title");

        detail.Unfetched!.ShouldContain("formLayout");
        detail.Unfetched!.ShouldContain("rules");
        detail.Unfetched!.ShouldNotContain("behaviours");
        detail.Unfetched!.ShouldNotContain("fields");
    }

    // ═══════════════════════════════════════════════════════════════
    //  The ROSTER — which types the whole-process document covers (AB#239)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Every type the process reports reaches the roster, off the wire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>The roster is what "every type" and "this type is absent" both mean, and until
    /// AB#239 it had no wire-level test at all.</b> The assembler seam tests script this call's
    /// RESULT, so they are structurally blind to how it is produced — the same blind spot that
    /// shipped six defects in AB#237 and a description-killing 400 in AB#238, both behind a
    /// green suite.
    /// </para>
    /// <para>
    /// It matters more here than for the per-type fetches because a roster defect is
    /// SILENT AND INDISTINGUISHABLE FROM THE ANSWER: a type dropped from the roster carries no
    /// <c>unfetched</c> label — it simply is not in the document — and against a second process
    /// that reports it, the drop reads as a genuine structural difference. That is a lie in
    /// exactly the direction ruling S3 exists to prevent, arriving through the one criterion
    /// this ticket owns.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetTypes_CarriesEveryTypeTheProcessReports()
    {
        var handler = new RoutingHandler().Route(
            "/workItemTypes?",
            "{\"count\":3,\"value\":["
            + "{\"referenceName\":\"Niflheim.Epic\",\"name\":\"Epic\",\"customization\":\"inherited\","
            + "\"inherits\":\"Microsoft.VSTS.WorkItemTypes.Epic\",\"isDisabled\":false},"
            + "{\"referenceName\":\"Niflheim.Grilling\",\"name\":\"Grilling\",\"customization\":\"custom\","
            + "\"isDisabled\":false},"
            + "{\"referenceName\":\"Niflheim.Retired\",\"name\":\"Retired\",\"customization\":\"custom\","
            + "\"isDisabled\":true}"
            + "]}");

        var types = await CreateSource(handler).GetTypesAsync();

        types.ShouldNotBeNull();
        types.Select(t => t.ReferenceName).ShouldBe(
            ["Niflheim.Epic", "Niflheim.Grilling", "Niflheim.Retired"],
            "every type the process reports must reach the roster — a dropped one is invisible "
            + "in the document and reads as a real difference against another process");

        // 🔴 A DISABLED type is still in the process and still in the roster, carrying its
        // flag. Dropping it would let "this process disabled the type" and "this process does
        // not have the type" render identically — two very different facts about a process.
        types.Single(t => t.ReferenceName == "Niflheim.Retired").IsDisabled.ShouldBeTrue();
        types.Single(t => t.ReferenceName == "Niflheim.Epic").Inherits
            .ShouldBe("Microsoft.VSTS.WorkItemTypes.Epic");
        types.Single(t => t.ReferenceName == "Niflheim.Grilling").Inherits.ShouldBeNull();
    }

    /// <summary>
    /// 🔴 A count-shaped 404 on the type-list route is a FAILURE, never a process with no
    /// types.
    /// </summary>
    /// <remarks>
    /// The count-shaped body is the shape of a thin success, so the natural reading yields an
    /// empty roster — and an empty roster is a document asserting the process contains
    /// nothing. Diffed against a real process, every one of its types then reads as an
    /// addition. <c>null</c> is the honest answer and the caller renders a hard failure rather
    /// than an empty document.
    /// </remarks>
    [Fact]
    public async Task GetTypes_CountShaped404_IsAFailureNotAnEmptyProcess()
    {
        // The type-list route is unregistered, so the handler answers count-shaped 404.
        var types = await CreateSource(new RoutingHandler()).GetTypesAsync();

        types.ShouldBeNull(
            "a count-shaped 404 is the shape of a thin success — laundering it into an empty "
            + "roster would claim the process has no types, and every type of the process it "
            + "is compared against would then read as an addition");
    }

    /// <summary>
    /// 🔴 A row with no <c>referenceName</c> is DROPPED, and this test pins that as a
    /// deliberate choice rather than leaving it as an untested default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference name is the identity every other fetch is keyed by and the only attribute
    /// two processes can be matched on, so a row without one cannot be described or compared —
    /// there is nothing to fetch its fields with and nothing to line it up against. Keeping it
    /// would put an unaddressable entry in the document.
    /// </para>
    /// <para>
    /// 🔴 <b>Named as a known limitation rather than presented as obviously right.</b> The drop
    /// is silent: it carries no <c>unfetched</c> label, so a roster short by one row is
    /// indistinguishable from a process that genuinely lacks the type — the failure class S3
    /// exists to prevent. It is tolerated because no such row has ever been observed on this
    /// route (reference name is the route's own key), and because the alternative — failing
    /// the entire description over one malformed row — loses thirteen good types to one bad
    /// one, which is the AB#238 blast radius exactly. Recorded here so the trade is visible
    /// and testable rather than an accident of a <c>continue</c> statement. The sibling rows
    /// must survive it, which is the half that would break silently.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetTypes_RowWithNoReferenceName_IsDroppedWithoutLosingItsSiblings()
    {
        var handler = new RoutingHandler().Route(
            "/workItemTypes?",
            "{\"count\":3,\"value\":["
            + "{\"referenceName\":\"Niflheim.Epic\",\"name\":\"Epic\",\"customization\":\"inherited\"},"
            + "{\"referenceName\":\"\",\"name\":\"Nameless\",\"customization\":\"custom\"},"
            + "{\"referenceName\":\"Niflheim.Grilling\",\"name\":\"Grilling\",\"customization\":\"custom\"}"
            + "]}");

        var types = await CreateSource(handler).GetTypesAsync();

        types.ShouldNotBeNull();

        // Precondition: the payload genuinely carried three rows, so the drop below is a real
        // drop rather than a fixture that never had the row.
        types.Count.ShouldBe(2);
        types.Select(t => t.ReferenceName).ShouldBe(["Niflheim.Epic", "Niflheim.Grilling"]);

        // 🔴 The half that would break silently: one unusable row must not take the usable
        // ones with it. Losing the siblings is the AB#238 blast radius — many types lost to
        // one type's answer.
        types.ShouldAllBe(t => !string.IsNullOrWhiteSpace(t.ReferenceName));
    }
}
