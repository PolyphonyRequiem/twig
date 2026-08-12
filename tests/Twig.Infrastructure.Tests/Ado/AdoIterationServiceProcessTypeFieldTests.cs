using Shouldly;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Covers <c>AdoIterationService.GetTypeFieldsAsync</c> — the per-type field list (AB#234),
/// and the fix for the founding correctness defect behind the process descriptor work.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>The defect these tests exist for.</b> The per-type field view was fed from
/// <c>_apis/wit/fields</c>, which is PROJECT-wide and identical for every work item type:
/// <c>twig process Task</c> and <c>twig process Map</c> returned the same 85 fields in the
/// same order. That output is not merely thin — it is untrue about which fields belong to
/// the type.
/// </para>
/// <para>
/// The behaviours worth pinning are the ones the wrong implementation gets wrong: two types
/// returning DIFFERENT sets, the route being keyed by REFERENCE name rather than display
/// name, the version-selected attributes (<c>required</c>, <c>defaultValue</c>,
/// <c>customization</c>) surviving the parse, and the count-shaped 404 envelope not being
/// laundered into "this type has no fields".
/// </para>
/// <para>
/// Evidence: branch <c>docs/process-descriptor-map</c>,
/// <c>wayfinder-process-descriptor/assets/0001-endpoint-findings.md</c>.
/// </para>
/// </remarks>
public sealed class AdoIterationServiceProcessTypeFieldTests
{
    private const string ProcessId = "adcc42ab-9882-485e-a3ed-7678f01f66bc";

    /// <summary>
    /// The project lookup's capabilities payload — how the process template id, which
    /// every process-scoped route below is keyed by, is discovered.
    /// </summary>
    private const string ProcessCapabilitiesPayload =
        "{\"capabilities\":{\"processTemplate\":{\"templateName\":\"Agile\",\"templateTypeId\":\""
        + ProcessId + "\"}}}";

    /// <summary>
    /// The per-type fields payload shape as the route actually returns it at
    /// <c>7.1-preview.2</c> — the version that carries <c>required</c>,
    /// <c>defaultValue</c> and <c>customization</c>.
    /// </summary>
    private static string FieldsPayload(params string[] referenceNames)
    {
        var rows = referenceNames.Select(r =>
            $$"""
            {"customization":"inherited","description":"","isLocked":false,
             "name":"{{r.Split('.').Last()}}","referenceName":"{{r}}","type":"string",
             "url":"https://example.com/behaviors"}
            """);
        return $$"""{"count":{{referenceNames.Length}},"value":[{{string.Join(',', rows)}}]}""";
    }

    /// <summary>
    /// A handler wired for the process routes, serving a DIFFERENT per-type field list for
    /// each named type, alongside the project-wide <c>_apis/wit/fields</c> list that the
    /// defect used to read from.
    /// </summary>
    /// <remarks>
    /// 🔴 The project-wide list is deliberately present and deliberately DIFFERENT from
    /// both per-type lists. Without it in the fixture, a test asserting "two types differ"
    /// could pass against an implementation that simply never consults the project-wide
    /// source — it would not demonstrate that the correct source is being read.
    /// </remarks>
    private static FakeHandler HandlerWithTypeFields(
        params (string TypeName, string[] Fields)[] types)
    {
        var handler = new FakeHandler();
        handler.SetRawResponse(
            "/_apis/projects/",
            ProcessCapabilitiesPayload);
        handler.SetWorkItemTypesResponse(types.Select(t => t.TypeName).ToArray());

        // The PROJECT-wide list — one shared list, the source of the defect.
        handler.SetRawResponse(
            "/_apis/wit/fields",
            """
            {"count":3,"value":[
              {"referenceName":"System.ProjectWideOnly","name":"Project Wide Only","type":"string"},
              {"referenceName":"System.Title","name":"Title","type":"string"},
              {"referenceName":"System.State","name":"State","type":"string"}]}
            """);

        foreach (var (typeName, fields) in types)
        {
            var referenceName = "System." + typeName.Replace(" ", string.Empty);
            handler.SetRawResponse(
                $"/workItemTypes/{referenceName}/fields",
                FieldsPayload(fields));
        }

        return handler;
    }

    /// <summary>
    /// 🔴 <b>The defect, asserted directly.</b> Two different types must return different
    /// field sets. Against the unfixed code the only available field source was the
    /// project-wide list, which is byte-identical for every type — so this is the
    /// assertion that separates a correct implementation from the shipped one.
    /// </summary>
    [Fact]
    public async Task GetTypeFieldsAsync_ReturnsDifferentFieldsForDifferentTypes()
    {
        var handler = HandlerWithTypeFields(
            ("Task", ["System.Title", "System.State", "Microsoft.VSTS.Scheduling.RemainingWork"]),
            ("Map", ["System.Title", "Custom.MapScale"]));
        var service = FakeHandler.CreateService(handler);

        var taskFields = await service.GetTypeFieldsAsync("Task");
        var mapFields = await service.GetTypeFieldsAsync("Map");

        taskFields.ShouldNotBeNull();
        mapFields.ShouldNotBeNull();

        var taskRefs = taskFields.Select(f => f.ReferenceName).ToList();
        var mapRefs = mapFields.Select(f => f.ReferenceName).ToList();

        // Precondition, asserted rather than assumed: the fixture genuinely serves two
        // different lists. Without this a later fixture edit could hollow the test out
        // into a tautology.
        taskRefs.Count.ShouldBe(3);
        mapRefs.Count.ShouldBe(2);

        taskRefs.ShouldNotBe(mapRefs);
        taskRefs.ShouldContain("Microsoft.VSTS.Scheduling.RemainingWork");
        mapRefs.ShouldNotContain("Microsoft.VSTS.Scheduling.RemainingWork");
        mapRefs.ShouldContain("Custom.MapScale");
        taskRefs.ShouldNotContain("Custom.MapScale");
    }

    /// <summary>
    /// 🔴 The complement of the test above, and the one that makes it load-bearing: the
    /// type-scoped answer must not be the project-wide list. An implementation that kept
    /// reading <c>_apis/wit/fields</c> and merely relabelled it would satisfy neither.
    /// </summary>
    [Fact]
    public async Task GetTypeFieldsAsync_DoesNotReturnTheProjectWideList()
    {
        var handler = HandlerWithTypeFields(("Task", ["System.Title", "System.State"]));
        var service = FakeHandler.CreateService(handler);

        // Precondition: the project-wide list really does carry a field no type carries.
        var projectWide = await service.GetFieldDefinitionsAsync();
        projectWide.Select(f => f.ReferenceName).ShouldContain("System.ProjectWideOnly");

        var typeFields = await service.GetTypeFieldsAsync("Task");

        typeFields.ShouldNotBeNull();
        typeFields.Select(f => f.ReferenceName).ShouldNotContain("System.ProjectWideOnly");
    }

    /// <summary>
    /// The route is keyed by the type's REFERENCE name, not its display name. Sending the
    /// display name 404s against a real server, so the resolution step is pinned — the
    /// same resolution the rules and layout routes already do.
    /// </summary>
    [Fact]
    public async Task GetTypeFieldsAsync_KeysTheRouteByReferenceName()
    {
        var handler = new RecordingHandler();
        handler.SetRawResponse(
            "/_apis/projects/",
            ProcessCapabilitiesPayload);
        handler.SetWorkItemTypesResponse("User Story");
        handler.SetRawResponse("/workItemTypes/System.UserStory/fields", FieldsPayload("System.Title"));

        var service = FakeHandler.CreateService(handler);

        var fields = await service.GetTypeFieldsAsync("User Story");

        fields.ShouldNotBeNull();
        var fieldsUrl = handler.RequestedUrls.Single(u => u.Contains("/workItemTypes/", StringComparison.Ordinal));
        fieldsUrl.ShouldContain("/workItemTypes/System.UserStory/fields");
        // The display name must never reach the wire — it is not what the route accepts.
        fieldsUrl.ShouldNotContain("User%20Story");
    }

    /// <summary>
    /// 🔴 The api-version is part of the contract, not decoration. The same URL at
    /// <c>7.1-preview.1</c> returns the same COUNT of rows with a disjoint attribute set
    /// carrying neither <c>required</c> nor <c>defaultValue</c> — so a version slip is
    /// invisible in the data volume. Asserted against the pinned constant rather than a
    /// hardcoded literal, so the pin stays the single place the version is decided.
    /// </summary>
    [Fact]
    public async Task GetTypeFieldsAsync_CallsTheRouteAtThePinnedVersion()
    {
        var handler = new RecordingHandler();
        handler.SetRawResponse(
            "/_apis/projects/",
            ProcessCapabilitiesPayload);
        handler.SetWorkItemTypesResponse("Task");
        handler.SetRawResponse("/workItemTypes/System.Task/fields", FieldsPayload("System.Title"));

        var service = FakeHandler.CreateService(handler);

        await service.GetTypeFieldsAsync("Task");

        var fieldsUrl = handler.RequestedUrls.Single(u => u.Contains("/workItemTypes/", StringComparison.Ordinal));
        fieldsUrl.ShouldContain($"api-version={Twig.Infrastructure.Ado.AdoApiVersions.ProcessWorkItemTypeFields}");
    }

    /// <summary>
    /// The attributes that only exist at the pinned version must survive the parse:
    /// requiredness, default value, and authored-vs-inherited. Dropping any of them is a
    /// silent regression to the thinner schema.
    /// </summary>
    [Fact]
    public async Task GetTypeFieldsAsync_CarriesRequirednessDefaultValueAndCustomization()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse(
            "/_apis/projects/",
            ProcessCapabilitiesPayload);
        handler.SetWorkItemTypesResponse("Task");
        handler.SetRawResponse("/workItemTypes/System.Task/fields",
            """
            {"count":3,"value":[
              {"customization":"system","defaultValue":null,"isLocked":false,"name":"Title",
               "referenceName":"System.Title","required":true,"type":"string","description":"The title"},
              {"customization":"inherited","defaultValue":"New","isLocked":true,"name":"State",
               "referenceName":"System.State","required":false,"type":"string"},
              {"customization":"custom","isLocked":false,"name":"Execution Mode",
               "referenceName":"Custom.WayfinderExecutionMode","type":"string"}]}
            """);

        var service = FakeHandler.CreateService(handler);

        var fields = await service.GetTypeFieldsAsync("Task");

        fields.ShouldNotBeNull();

        var title = fields.Single(f => f.ReferenceName == "System.Title");
        title.Name.ShouldBe("Title");
        title.Type.ShouldBe("string");
        title.RequiredUnconditionally.ShouldBeTrue();
        title.DefaultValue.ShouldBeNull();
        title.Customization.ShouldBe("system");
        title.Description.ShouldBe("The title");

        var state = fields.Single(f => f.ReferenceName == "System.State");
        state.DefaultValue.ShouldBe("New");
        state.Customization.ShouldBe("inherited");
        state.IsLocked.ShouldBeTrue();
        state.RequiredUnconditionally.ShouldBeFalse();

        // The server omits `required` and `defaultValue` entirely on most custom rows.
        // Absent must read as not-required-unconditionally and no default, not as a crash
        // and not as required.
        var custom = fields.Single(f => f.ReferenceName == "Custom.WayfinderExecutionMode");
        custom.Customization.ShouldBe("custom");
        custom.RequiredUnconditionally.ShouldBeFalse();
        custom.DefaultValue.ShouldBeNull();
    }

    /// <summary>
    /// 🔴 <c>required</c> on this route reports only UNCONDITIONAL requiredness.
    /// Conditional requiredness — a <c>makeRequired</c> action gated on a <c>when</c>
    /// condition — lives on the rules route and is invisible here.
    /// </summary>
    /// <remarks>
    /// This test does not assert a merge; merging is not this ticket's scope. It pins the
    /// fact that the two sources genuinely disagree in the fixture, so that whoever
    /// implements the merge cannot mistake this route's <c>false</c> for "not required"
    /// and find the fixture quietly agreeing with them. The precondition is asserted
    /// explicitly per the repo's fixture-hazard convention: if the field were
    /// unconditionally required here, the disagreement would not exist and the test would
    /// be a tautology.
    /// </remarks>
    [Fact]
    public async Task GetTypeFieldsAsync_ReportsOnlyUnconditionalRequiredness()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse(
            "/_apis/projects/",
            ProcessCapabilitiesPayload);
        handler.SetWorkItemTypesResponse("Grilling");
        handler.SetRawResponse("/workItemTypes/System.Grilling/fields",
            """
            {"count":1,"value":[
              {"customization":"custom","isLocked":false,"name":"Answer",
               "referenceName":"Custom.WayfinderAnswer","required":false,"type":"string"}]}
            """);
        handler.SetRawResponse("/workItemTypes/System.Grilling/rules",
            """
            {"count":1,"value":[
              {"conditions":[{"conditionType":"when","field":"System.State","value":"Done"}],
               "actions":[{"actionType":"makeRequired","targetField":"Custom.WayfinderAnswer"}],
               "isDisabled":false}]}
            """);

        var service = FakeHandler.CreateService(handler);

        var fields = await service.GetTypeFieldsAsync("Grilling");
        var rules = await service.GetRulesAsync("Grilling");

        fields.ShouldNotBeNull();

        // Precondition: the rules source genuinely makes this field conditionally
        // required. Without it the two sources would not disagree and the assertion below
        // would prove nothing.
        rules.ShouldContain(r =>
            r.Actions.Any(a => a.ActionType == "makeRequired"
                && a.TargetField == "Custom.WayfinderAnswer")
            && r.Conditions.Any(c => c.ConditionType == "when"));

        // The fields route, read alone, calls it not-required. That is the honest report
        // of what this route knows — and is exactly why the property is named for
        // unconditional requiredness rather than for requiredness.
        fields.Single(f => f.ReferenceName == "Custom.WayfinderAnswer")
            .RequiredUnconditionally.ShouldBeFalse();
    }

    /// <summary>
    /// 🔴 A 404 from this route family arrives with a COUNT-SHAPED body
    /// (<c>{"count":1,"value":{"Message":…}}</c>) — the same shape as a successful thin
    /// response. It must not launder into "this type has no fields"; that misreading is
    /// what produced the original bug report about this endpoint family.
    /// </summary>
    [Fact]
    public async Task GetTypeFieldsAsync_DoesNotTreatACountShapedErrorAsEmptyData()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse(
            "/_apis/projects/",
            ProcessCapabilitiesPayload);
        handler.SetWorkItemTypesResponse("Task");
        // The route answers 200 with the count-shaped envelope: `value` is an OBJECT, not
        // an array of fields. An implementation that shrugged this off would report an
        // empty field list as fact.
        handler.SetRawResponse("/workItemTypes/System.Task/fields",
            """{"count":1,"value":{"Message":"The controller for path was not found."}}""");

        var service = FakeHandler.CreateService(handler);

        var fields = await service.GetTypeFieldsAsync("Task");

        fields.ShouldBeNull();
    }

    /// <summary>
    /// An unknown type must return null rather than an empty list. "We could not ask" and
    /// "this type carries no fields" are different facts, and collapsing them would make
    /// a failed lookup indistinguishable from a real answer.
    /// </summary>
    [Fact]
    public async Task GetTypeFieldsAsync_ReturnsNullForUnknownType()
    {
        var service = FakeHandler.CreateService(
            HandlerWithTypeFields(("Task", ["System.Title"])));

        var fields = await service.GetTypeFieldsAsync("NoSuchType");

        fields.ShouldBeNull();
    }

    /// <summary>
    /// A genuinely empty field list survives as an empty list, not as null — the other
    /// half of the distinction above.
    /// </summary>
    [Fact]
    public async Task GetTypeFieldsAsync_DistinguishesEmptyListFromNoAnswer()
    {
        var service = FakeHandler.CreateService(
            HandlerWithTypeFields(("Task", [])));

        var fields = await service.GetTypeFieldsAsync("Task");

        fields.ShouldNotBeNull();
        fields.ShouldBeEmpty();
    }

    /// <summary>
    /// Per-type and cached, matching the rules and layout pair it sits beside. Refetching
    /// per call would multiply requests across a whole-process description.
    /// </summary>
    [Fact]
    public async Task GetTypeFieldsAsync_CachesPerType()
    {
        var service = FakeHandler.CreateService(
            HandlerWithTypeFields(("Task", ["System.Title"])));

        var first = await service.GetTypeFieldsAsync("Task");
        var second = await service.GetTypeFieldsAsync("Task");

        first.ShouldBeSameAs(second);
    }
}

/// <summary>
/// A <see cref="FakeHandler"/> that records the URLs it was asked for, so tests can pin
/// the route shape and the api-version that actually reached the wire.
/// </summary>
internal sealed class RecordingHandler : FakeHandler
{
    private readonly List<string> _requested = [];

    public IReadOnlyList<string> RequestedUrls => _requested;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _requested.Add(request.RequestUri!.ToString());
        return base.SendAsync(request, cancellationToken);
    }
}
