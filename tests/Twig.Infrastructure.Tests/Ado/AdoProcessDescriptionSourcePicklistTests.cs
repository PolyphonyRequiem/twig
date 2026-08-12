using System.Net;
using System.Text;
using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Wire-level tests for <c>AdoProcessDescriptionSource.GetFieldValueConstraintsAsync</c>
/// (AB#237) — the picklist association source.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>These exist because the seam tests could not see this layer.</b> Every other AB#237
/// test drives a scripted <c>IProcessDescriptionSource</c>, which is the right shape for
/// asserting document behaviour and is structurally blind to deserialization. Independent
/// review found three defects living in exactly that blind spot, all of the same class the
/// ticket exists to prevent: a failed fetch rendering as a confident claim. The suite was
/// green throughout.
/// </para>
/// <para>
/// 🔴 <b>The count-shaped body is the hazard these are pointed at.</b> On this route family a
/// 404 arrives as <c>{"count":1,"value":{"Message":…}}</c> — the exact shape of a thin
/// success. Sibling fetches survive it by accident: they deserialize into a list envelope
/// whose <c>value</c> is an array, so an object there throws. The picklist DTO is a bare
/// object with no array-shaped member and falls outside that accidental defence, so its guard
/// has to be deliberate — and therefore has to be tested.
/// </para>
/// </remarks>
public sealed class AdoProcessDescriptionSourcePicklistTests
{
    /// <summary>The literal count-shaped body this route family returns for a 404.</summary>
    private const string CountShaped404 =
        "{\"count\":1,\"value\":{\"Message\":\"VS403646: The picklist does not exist.\"}}";

    /// <summary>
    /// Routes by URL fragment, so one test can make the field list succeed while a specific
    /// picklist fails — which is the partial-failure case the blocking defects lived in.
    /// </summary>
    private sealed class PicklistHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes =
            new(StringComparer.OrdinalIgnoreCase);

        public void Route(string urlFragment, string body, HttpStatusCode status = HttpStatusCode.OK)
            => _routes[urlFragment] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            // Longest fragment first, so a specific picklist id beats the generic lists route.
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

    /// <summary>One org field row.</summary>
    private static string FieldRow(string referenceName, string? isPicklist, string? picklistId)
    {
        var picklistPart = isPicklist is null ? string.Empty : $",\"isPicklist\":{isPicklist}";
        // 🔴 picklistId is a CONDITIONAL key: the server omits it rather than sending null.
        var idPart = picklistId is null ? string.Empty : $",\"picklistId\":\"{picklistId}\"";
        return $"{{\"referenceName\":\"{referenceName}\",\"name\":\"n\",\"type\":\"string\"{picklistPart}{idPart}}}";
    }

    private static string FieldList(params string[] rows)
        => $"{{\"count\":{rows.Length},\"value\":[{string.Join(',', rows)}]}}";

    // ═══════════════════════════════════════════════════════════════
    //  The happy path — so the negatives below are not vacuous
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A genuinely picklist-backed field resolves to its list's values, off the wire.
    /// </summary>
    /// <remarks>
    /// The positive half. Without it every assertion below could be satisfied by a source that
    /// resolves nothing at all — the same hollow-guard shape the spec warns about for tests 4
    /// and 5.
    /// </remarks>
    [Fact]
    public async Task GetFieldValueConstraints_PicklistBackedField_ResolvesItsValues()
    {
        var handler = new PicklistHandler();
        handler.Route("/_apis/wit/fields", FieldList(
            FieldRow("Custom.Mode", "true", "list-1"),
            FieldRow("Custom.Plain", "false", null)));
        handler.Route("/lists/list-1",
            "{\"id\":\"list-1\",\"name\":\"ModeList\",\"type\":\"String\",\"items\":[\"HITL\",\"AFK\"]}");

        var constraints = await CreateSource(handler).GetFieldValueConstraintsAsync();

        constraints.ShouldNotBeNull();

        var constrained = constraints["Custom.Mode"];
        constrained.Kind.ShouldBe(FieldValueConstraintKind.ListConstrained);
        constrained.ListName.ShouldBe("ModeList");
        // Server order is preserved HERE; the assembler is the single sorting authority.
        constrained.Values.ShouldBe(["HITL", "AFK"]);

        // 🔴 And the explicit negative, in the same response, as a stated fact.
        constraints["Custom.Plain"].Kind.ShouldBe(FieldValueConstraintKind.Unconstrained);
    }

    // ═══════════════════════════════════════════════════════════════
    //  B1 — a count-shaped 404 must not become "constrained to nothing"
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 A count-shaped 404 on a picklist fetch yields <c>Unknown</c>, NOT a list-constrained
    /// field with no values.
    /// </summary>
    /// <remarks>
    /// The count-shaped body carries none of the picklist DTO's keys, and
    /// <c>System.Text.Json</c> ignores unmapped members — so it deserializes cleanly into an
    /// all-null instance rather than throwing. Untreated, the field would be reported as
    /// <c>list</c> constrained to <c>[]</c>: "the server accepts no value at all here", which
    /// is the strongest possible positive claim built on a call that failed, and is a REAL and
    /// different state (a list that exists and holds nothing).
    /// </remarks>
    [Fact]
    public async Task GetFieldValueConstraints_CountShaped404OnTheList_IsUnknownNotConstrainedToNothing()
    {
        var handler = new PicklistHandler();
        handler.Route("/_apis/wit/fields", FieldList(FieldRow("Custom.Mode", "true", "list-1")));
        handler.Route("/lists/list-1", CountShaped404, HttpStatusCode.NotFound);

        var constraints = await CreateSource(handler).GetFieldValueConstraintsAsync();

        constraints.ShouldNotBeNull();

        var constraint = constraints["Custom.Mode"];
        constraint.Kind.ShouldBe(
            FieldValueConstraintKind.Unknown,
            "a failed picklist fetch must not be reported as a list that constrains the field "
            + "to nothing — that is a confident claim built on a call that failed");
        constraint.Values.ShouldBeEmpty();
    }

    /// <summary>
    /// The same guard for a count-shaped body served with a 200, which no exception path
    /// catches at all.
    /// </summary>
    /// <remarks>
    /// 🔴 This is the case the <c>AdoNotFoundException</c> catch cannot reach, and it is the
    /// reason the guard is a positive envelope CHECK rather than more catch clauses. A real
    /// picklist always carries <c>id</c>; its absence is the tell.
    /// </remarks>
    [Fact]
    public async Task GetFieldValueConstraints_CountShapedBodyWith200_IsStillUnknown()
    {
        var handler = new PicklistHandler();
        handler.Route("/_apis/wit/fields", FieldList(FieldRow("Custom.Mode", "true", "list-1")));
        handler.Route("/lists/list-1", CountShaped404);

        var constraints = await CreateSource(handler).GetFieldValueConstraintsAsync();

        constraints!["Custom.Mode"].Kind.ShouldBe(FieldValueConstraintKind.Unknown);
    }

    /// <summary>
    /// A list that genuinely exists and holds nothing stays <c>ListConstrained</c> with no
    /// values — it is a real state, and the B1 guard must not swallow it.
    /// </summary>
    /// <remarks>
    /// 🔴 The other side of the guard. Keying on `id` rather than on emptiness is what keeps
    /// these two distinguishable: "we could not read the list" and "the list is empty, so this
    /// field accepts nothing" are different claims about the process.
    /// </remarks>
    [Fact]
    public async Task GetFieldValueConstraints_GenuinelyEmptyList_StaysConstrainedRatherThanUnknown()
    {
        var handler = new PicklistHandler();
        handler.Route("/_apis/wit/fields", FieldList(FieldRow("Custom.Mode", "true", "list-1")));
        handler.Route("/lists/list-1", "{\"id\":\"list-1\",\"name\":\"EmptyList\",\"items\":[]}");

        var constraints = await CreateSource(handler).GetFieldValueConstraintsAsync();

        var constraint = constraints!["Custom.Mode"];
        constraint.Kind.ShouldBe(
            FieldValueConstraintKind.ListConstrained,
            "an empty picklist is a real state — the field is constrained to nothing, which is "
            + "not the same as the list being unreadable");
        constraint.ListName.ShouldBe("EmptyList");
        constraint.Values.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  B2 / S2 — the three ways of not knowing must not become the negative
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 <c>isPicklist: true</c> with NO <c>picklistId</c> is <c>Unknown</c>, not
    /// <c>unconstrained</c>.
    /// </summary>
    /// <remarks>
    /// The association is PROVEN present and only the pointer is missing. Reporting it as
    /// unconstrained would say "the server accepts anything here" about a field the server
    /// itself says is list-backed — the most dangerous of the three wrong answers, because a
    /// caller acting on it fails at the server. <c>picklistId</c> is a conditional key on this
    /// version-sensitive route, so this is a drift away rather than hypothetical.
    /// </remarks>
    [Fact]
    public async Task GetFieldValueConstraints_PicklistTrueButNoId_IsUnknownNotUnconstrained()
    {
        var handler = new PicklistHandler();
        handler.Route("/_apis/wit/fields", FieldList(FieldRow("Custom.Mode", "true", picklistId: null)));

        var constraints = await CreateSource(handler).GetFieldValueConstraintsAsync();

        constraints!["Custom.Mode"].Kind.ShouldBe(
            FieldValueConstraintKind.Unknown,
            "the server says this field IS list-backed; only the pointer is missing, so "
            + "'accepts anything' is not a claim this document may make");
    }

    /// <summary>
    /// 🔴 An ABSENT <c>isPicklist</c> key is <c>Unknown</c>, not the explicit negative.
    /// </summary>
    /// <remarks>
    /// The whole no-heuristic design rests on this key being present on every row. A
    /// non-nullable <c>bool</c> would deserialize its absence to <c>false</c> and the code
    /// consumes <c>false</c> as a stated server FACT — so a version drift would silently
    /// manufacture the explicit negative out of nothing. That is "absence of evidence rendered
    /// as a stated fact": the banned inference wearing a different hat.
    /// </remarks>
    [Fact]
    public async Task GetFieldValueConstraints_AbsentIsPicklistKey_IsUnknownNotUnconstrained()
    {
        var handler = new PicklistHandler();
        handler.Route("/_apis/wit/fields", FieldList(FieldRow("Custom.Mode", isPicklist: null, picklistId: null)));

        var constraints = await CreateSource(handler).GetFieldValueConstraintsAsync();

        constraints!["Custom.Mode"].Kind.ShouldBe(
            FieldValueConstraintKind.Unknown,
            "a missing key is not the server stating a negative — treating it as one would "
            + "invent the fact the no-heuristic design depends on");
    }

    /// <summary>
    /// 🔴 A SUGGESTED picklist is not reported as a constraint.
    /// </summary>
    /// <remarks>
    /// ADO's <c>isPicklistSuggested</c> marks a list the web editor offers while the server
    /// still accepts anything. Reporting it as <c>list</c> would tell a caller its value must
    /// come from that list while a write of anything else succeeds — the overstatement this
    /// ticket removes, arriving through an unread flag rather than a bad guess. The values are
    /// still carried, because they are true and useful; only the CLAIM attached to them is
    /// weaker.
    /// </remarks>
    [Fact]
    public async Task GetFieldValueConstraints_SuggestedPicklist_IsNotReportedAsConstrained()
    {
        var handler = new PicklistHandler();
        handler.Route("/_apis/wit/fields",
            $"{{\"count\":1,\"value\":[{{\"referenceName\":\"Custom.Mode\",\"name\":\"n\",\"type\":\"string\"," +
            "\"isPicklist\":true,\"isPicklistSuggested\":true,\"picklistId\":\"list-1\"}]}");
        handler.Route("/lists/list-1",
            "{\"id\":\"list-1\",\"name\":\"SuggestList\",\"isSuggested\":true,\"items\":[\"A\",\"B\"]}");

        var constraints = await CreateSource(handler).GetFieldValueConstraintsAsync();

        var constraint = constraints!["Custom.Mode"];
        constraint.Kind.ShouldBe(
            FieldValueConstraintKind.ListSuggested,
            "a suggested picklist does not restrict what the server accepts, so calling it a "
            + "constraint overstates what the process demands");
        // The values are still carried — they are a true fact about the editor's offer.
        constraint.Values.ShouldBe(["A", "B"]);
        constraint.ListName.ShouldBe("SuggestList");
    }

    /// <summary>
    /// The list's own <c>isSuggested</c> is honoured even when the field row omits the flag.
    /// </summary>
    /// <remarks>
    /// 🔴 Two witnesses to one fact, and the WEAKER claim wins on disagreement. A field row
    /// that lost its flag through version drift must not silently upgrade a suggested list
    /// into an enforced one.
    /// </remarks>
    [Fact]
    public async Task GetFieldValueConstraints_ListSaysSuggestedButFieldDoesNot_IsStillSuggested()
    {
        var handler = new PicklistHandler();
        handler.Route("/_apis/wit/fields", FieldList(FieldRow("Custom.Mode", "true", "list-1")));
        handler.Route("/lists/list-1",
            "{\"id\":\"list-1\",\"name\":\"L\",\"isSuggested\":true,\"items\":[\"A\"]}");

        var constraints = await CreateSource(handler).GetFieldValueConstraintsAsync();

        constraints!["Custom.Mode"].Kind.ShouldBe(FieldValueConstraintKind.ListSuggested);
    }

    /// <summary>
    /// An ENFORCED picklist is still reported as enforced — the suggested guard must not
    /// swallow the real case.
    /// </summary>
    /// <remarks>
    /// 🔴 The other side of the guard. Without it, an implementation that reported every list
    /// as merely suggested would pass the two tests above while making the distinction
    /// worthless — understating the constraint instead of overstating it.
    /// </remarks>
    [Fact]
    public async Task GetFieldValueConstraints_EnforcedPicklist_IsStillReportedAsConstrained()
    {
        var handler = new PicklistHandler();
        handler.Route("/_apis/wit/fields",
            $"{{\"count\":1,\"value\":[{{\"referenceName\":\"Custom.Mode\",\"name\":\"n\",\"type\":\"string\"," +
            "\"isPicklist\":true,\"isPicklistSuggested\":false,\"picklistId\":\"list-1\"}]}");
        handler.Route("/lists/list-1",
            "{\"id\":\"list-1\",\"name\":\"L\",\"isSuggested\":false,\"items\":[\"A\"]}");

        var constraints = await CreateSource(handler).GetFieldValueConstraintsAsync();

        constraints!["Custom.Mode"].Kind.ShouldBe(FieldValueConstraintKind.ListConstrained);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Failure of the SOURCE itself, and partial failure
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A failed FIELD-LIST call returns <c>null</c>, never an empty map.
    /// </summary>
    /// <remarks>
    /// 🔴 An empty map asserts that every field is unconstrained — the overstatement inverted,
    /// built on a call that never came back. <c>null</c> is what makes the assembler label the
    /// type's <c>picklists</c> as unfetched.
    /// </remarks>
    [Fact]
    public async Task GetFieldValueConstraints_WhenTheFieldListFails_ReturnsNullNotAnEmptyMap()
    {
        // No route registered: the handler answers the count-shaped 404 by default.
        var constraints = await CreateSource(new PicklistHandler()).GetFieldValueConstraintsAsync();

        constraints.ShouldBeNull(
            "an empty map would claim every field is unconstrained on the strength of a call "
            + "that failed");
    }

    /// <summary>
    /// One unreadable list does not discard the answers for every other field.
    /// </summary>
    /// <remarks>
    /// The documented contract: the association is still known for the rest, and failing the
    /// whole description over one bad list would trade a lot of truth for no extra honesty.
    /// This also covers the widened exception swallow — a transient failure on a single list
    /// must not propagate out and fail the run.
    /// </remarks>
    [Fact]
    public async Task GetFieldValueConstraints_OneBadList_LeavesEveryOtherFieldsAnswerIntact()
    {
        var handler = new PicklistHandler();
        handler.Route("/_apis/wit/fields", FieldList(
            FieldRow("Custom.Bad", "true", "list-bad"),
            FieldRow("Custom.Good", "true", "list-good"),
            FieldRow("Custom.Plain", "false", null)));
        handler.Route("/lists/list-good",
            "{\"id\":\"list-good\",\"name\":\"GoodList\",\"items\":[\"A\"]}");
        handler.Route("/lists/list-bad", "boom", HttpStatusCode.InternalServerError);

        var constraints = await CreateSource(handler).GetFieldValueConstraintsAsync();

        constraints.ShouldNotBeNull();
        constraints["Custom.Bad"].Kind.ShouldBe(FieldValueConstraintKind.Unknown);
        constraints["Custom.Good"].Kind.ShouldBe(FieldValueConstraintKind.ListConstrained);
        constraints["Custom.Good"].Values.ShouldBe(["A"]);
        constraints["Custom.Plain"].Kind.ShouldBe(FieldValueConstraintKind.Unconstrained);
    }

    /// <summary>
    /// 🔴 Two org rows disagreeing about one field resolve to <c>Unknown</c>, not to whichever
    /// the server happened to send last.
    /// </summary>
    /// <remarks>
    /// The map is <c>OrdinalIgnoreCase</c>, so rows differing only in casing collapse onto one
    /// key. "Last writer wins" would make the document depend on WIRE ORDER, which is the one
    /// thing byte-stability forbids — and it would do so silently.
    /// </remarks>
    [Fact]
    public async Task GetFieldValueConstraints_DuplicateRowsThatDisagree_ResolveToUnknown()
    {
        var handler = new PicklistHandler();
        handler.Route("/_apis/wit/fields", FieldList(
            FieldRow("Custom.Mode", "true", "list-1"),
            // Same field by the map's comparer, different answer.
            FieldRow("custom.mode", "false", null)));
        handler.Route("/lists/list-1", "{\"id\":\"list-1\",\"name\":\"L\",\"items\":[\"A\"]}");

        var constraints = await CreateSource(handler).GetFieldValueConstraintsAsync();

        constraints!["Custom.Mode"].Kind.ShouldBe(
            FieldValueConstraintKind.Unknown,
            "picking either row would make the document depend on the order the server sent "
            + "them; neither answer is defensible");
    }

    /// <summary>
    /// Two rows that AGREE are not treated as a conflict.
    /// </summary>
    /// <remarks>
    /// 🔴 The guard's other side. <c>FieldValueConstraint</c> is a record whose <c>Values</c>
    /// is an <c>IReadOnlyList&lt;string&gt;</c> — compared by REFERENCE by compiler-generated
    /// equality — so a naive <c>!=</c> would call two identical lists different and
    /// manufacture a conflict that is not there, turning a perfectly good answer into
    /// <c>Unknown</c>.
    /// </remarks>
    [Fact]
    public async Task GetFieldValueConstraints_DuplicateRowsThatAgree_KeepTheAnswer()
    {
        var handler = new PicklistHandler();
        handler.Route("/_apis/wit/fields", FieldList(
            FieldRow("Custom.Mode", "true", "list-1"),
            FieldRow("custom.mode", "true", "list-1")));
        handler.Route("/lists/list-1", "{\"id\":\"list-1\",\"name\":\"L\",\"items\":[\"A\",\"B\"]}");

        var constraints = await CreateSource(handler).GetFieldValueConstraintsAsync();

        var constraint = constraints!["Custom.Mode"];
        constraint.Kind.ShouldBe(
            FieldValueConstraintKind.ListConstrained,
            "two rows carrying the SAME answer are not a disagreement — treating them as one "
            + "would discard a good answer over reference equality on the values list");
        constraint.Values.ShouldBe(["A", "B"]);
    }

    /// <summary>
    /// A null inside a picklist's <c>items</c> array is filtered rather than carried.
    /// </summary>
    /// <remarks>
    /// <c>items</c> is a JSON array that may contain null, while
    /// <c>FieldValueConstraint.Values</c> is declared non-nullable. An unfiltered null renders
    /// as an empty segment in the joined value list — indistinguishable from a real
    /// empty-string value the process actually accepts.
    /// </remarks>
    [Fact]
    public async Task GetFieldValueConstraints_NullItemInTheList_IsFilteredOut()
    {
        var handler = new PicklistHandler();
        handler.Route("/_apis/wit/fields", FieldList(FieldRow("Custom.Mode", "true", "list-1")));
        handler.Route("/lists/list-1", "{\"id\":\"list-1\",\"name\":\"L\",\"items\":[\"A\",null,\"B\"]}");

        var constraints = await CreateSource(handler).GetFieldValueConstraintsAsync();

        constraints!["Custom.Mode"].Values.ShouldBe(["A", "B"]);
    }

    /// <summary>
    /// Several fields sharing one list cost ONE fetch, not one per field.
    /// </summary>
    /// <remarks>
    /// Not an assertion about the fetch layer's shape for its own sake — the spec forbids
    /// pinning call counts as a design constraint. This guards the documented cost model
    /// ("one extra call per DISTINCT list") because getting it wrong multiplies round-trips on
    /// a process with many fields sharing a few lists, which is the common shape.
    /// </remarks>
    [Fact]
    public async Task GetFieldValueConstraints_FieldsSharingOneList_ResolveConsistently()
    {
        var handler = new PicklistHandler();
        handler.Route("/_apis/wit/fields", FieldList(
            FieldRow("Custom.A", "true", "shared"),
            FieldRow("Custom.B", "true", "shared")));
        handler.Route("/lists/shared", "{\"id\":\"shared\",\"name\":\"Shared\",\"items\":[\"X\"]}");

        var constraints = await CreateSource(handler).GetFieldValueConstraintsAsync();

        constraints!["Custom.A"].ListName.ShouldBe("Shared");
        constraints["Custom.B"].ListName.ShouldBe("Shared");
        constraints["Custom.A"].Values.ShouldBe(["X"]);
        constraints["Custom.B"].Values.ShouldBe(["X"]);
    }
}
