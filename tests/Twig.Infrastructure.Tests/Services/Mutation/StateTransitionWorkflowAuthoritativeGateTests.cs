using System.Net;
using System.Text;
using NSubstitute;
using Shouldly;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Mutation;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Services.Mutation;
using Twig.Infrastructure.Tests.Ado;
using Twig.TestKit;
using Xunit;

namespace Twig.Infrastructure.Tests.Services.Mutation;

/// <summary>
/// AB#1209: the state-transition process gate must read the authoritative expected-revision
/// snapshot, never the filtered cache projection <see cref="AdoRestClient.FetchAsync"/>
/// returns. Drives the real <see cref="AdoRestClient"/> over a fake HTTP boundary so the
/// projection's field filtering is the production code, not a stand-in.
/// </summary>
public sealed class StateTransitionWorkflowAuthoritativeGateTests
{
    private const int ItemId = 1198;
    private const int Revision = 7;

    private static readonly ProcessRule[] UnconditionalSystemRequirements =
    [
        new([], [new RuleAction("$makeRequired", "System.AreaId", null)], IsDisabled: false),
        new([], [new RuleAction("$makeRequired", "System.IterationId", null)], IsDisabled: false),
    ];

    [Fact]
    public async Task ExecuteAsync_RequiredFieldPopulatedButFilteredFromProjection_Transitions()
    {
        // System.AreaId / System.IterationId are read-only server fields, so the cache
        // projection drops them. They ARE populated, and the authoritative revision carries
        // them; refusing on them is a false positive.
        var handler = new WorkItemHandler(
            authoritativeFields: """
                "System.AreaId":123,"System.IterationId":456
                """);
        var (client, workflow) = Compose(handler, UnconditionalSystemRequirements);

        var remote = await client.FetchAsync(ItemId);
        remote.Fields.ContainsKey("System.AreaId").ShouldBeFalse(
            "precondition: the cache projection must filter the read-only field, or this test exercises nothing");

        var outcome = await workflow.ExecuteAsync(remote, "Doing", remote.Revision);

        outcome.ShouldBeOfType<StateTransitionOutcome.Succeeded>();
        handler.PatchCount.ShouldBe(1);
        handler.RevisionUrls.ShouldHaveSingleItem().ShouldBe(
            $"/workitems/{ItemId}/revisions/{Revision}?$expand=all",
            "the gate must read the exact expected revision");
    }

    [Fact]
    public async Task ExecuteAsync_RequiredFieldEmptyInAuthoritativeSnapshot_StillRefuses()
    {
        var handler = new WorkItemHandler(authoritativeFields: "\"System.AreaId\":123");
        var (client, workflow) = Compose(handler, UnconditionalSystemRequirements);
        var remote = await client.FetchAsync(ItemId);

        var outcome = await workflow.ExecuteAsync(remote, "Doing", remote.Revision);

        var refused = outcome.ShouldBeOfType<StateTransitionOutcome.RequiredFieldsMissing>();
        refused.MissingFields.ShouldBe(["System.IterationId"]);
        handler.PatchCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_SupplierConditionDoesNotFire_StillRefuses()
    {
        var rules = new ProcessRule[]
        {
            new([new RuleCondition("$when", "System.State", "Doing")],
                [new RuleAction("$makeRequired", "Custom.Gate", null)], IsDisabled: false),
            new([new RuleCondition("$when", "System.State", "Done")],
                [new RuleAction("$copyValue", "Custom.Gate", "supplied")], IsDisabled: false),
        };
        var handler = new WorkItemHandler(authoritativeFields: "\"System.AreaId\":123");
        var (client, workflow) = Compose(handler, rules);
        var remote = await client.FetchAsync(ItemId);

        var outcome = await workflow.ExecuteAsync(remote, "Doing", remote.Revision);

        outcome.ShouldBeOfType<StateTransitionOutcome.RequiredFieldsMissing>()
            .MissingFields.ShouldBe(["Custom.Gate"]);
        handler.PatchCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_AuthoritativeFetchFails_FailsClosedWithoutPatching()
    {
        // No fallback to the filtered projection: without the authoritative snapshot the
        // gate cannot be evaluated, and the write must not be emitted.
        var handler = new WorkItemHandler(
            authoritativeFields: "\"System.AreaId\":123,\"System.IterationId\":456",
            failRevisionFetch: true);
        var (client, workflow) = Compose(handler, UnconditionalSystemRequirements);
        var remote = await client.FetchAsync(ItemId);

        await Should.ThrowAsync<Exception>(() => workflow.ExecuteAsync(remote, "Doing", remote.Revision));

        handler.PatchCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_AuthoritativeSnapshotAtDifferentRevision_FailsClosedWithoutPatching()
    {
        var item = new WorkItemBuilder(ItemId, "Gate").AsTask().InState("To Do").Build();
        var adoService = Substitute.For<IAdoWorkItemService>();
        var revisionBound = Substitute.For<IRevisionBoundAdoWorkItemService>();
        revisionBound.FetchAtRevisionAsync(ItemId, Revision, Arg.Any<CancellationToken>())
            .Returns(new WorkItemMapper().ToSnapshot(item) with { Revision = Revision + 1 });
        var processConfig = Substitute.For<IProcessConfigurationProvider>();
        processConfig.GetConfiguration().Returns(ProcessConfigBuilder.Basic());
        var workflow = new StateTransitionWorkflow(
            Substitute.For<IWorkItemRepository>(),
            adoService,
            Substitute.For<IPendingChangeStore>(),
            processConfig,
            parentPropagation: null,
            promptStateWriter: null,
            processRuleProvider: new FakeProcessRuleProvider(UnconditionalSystemRequirements),
            revisionBound: revisionBound);

        await Should.ThrowAsync<InvalidOperationException>(() => workflow.ExecuteAsync(item, "Doing", Revision));

        await adoService.DidNotReceiveWithAnyArgs().PatchAsync(default, default!, default, default);
    }

    [Fact]
    public void Constructor_GateWithoutAuthoritativeSource_IsRejected()
    {
        Should.Throw<ArgumentNullException>(() => new StateTransitionWorkflow(
            Substitute.For<IWorkItemRepository>(),
            Substitute.For<IAdoWorkItemService>(),
            Substitute.For<IPendingChangeStore>(),
            Substitute.For<IProcessConfigurationProvider>(),
            parentPropagation: null,
            promptStateWriter: null,
            processRuleProvider: new FakeProcessRuleProvider([]),
            revisionBound: null));
    }

    private static (AdoRestClient Client, StateTransitionWorkflow Workflow) Compose(
        HttpMessageHandler handler,
        IReadOnlyList<ProcessRule> rules)
    {
        var fieldDefs = Substitute.For<IFieldDefinitionStore>();
        fieldDefs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new FieldDefinition("System.AreaId", "Area ID", "integer", IsReadOnly: true),
            new FieldDefinition("System.IterationId", "Iteration ID", "integer", IsReadOnly: true),
        ]);
        var client = new AdoRestClient(
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) },
            new FakeAuthProvider(),
            "https://dev.azure.com/testorg",
            "testproject",
            new WorkItemMapper(),
            fieldDefs);

        var processConfig = Substitute.For<IProcessConfigurationProvider>();
        processConfig.GetConfiguration().Returns(ProcessConfigBuilder.Basic());
        var pending = Substitute.For<IPendingChangeStore>();
        pending.GetChangesAsync(ItemId, Arg.Any<CancellationToken>()).Returns(Array.Empty<PendingChangeRecord>());

        // Mirrors TwigServiceRegistration: AdoRestClient serves both the everyday work-item
        // surface and the revision-bound surface.
        var workflow = new StateTransitionWorkflow(
            Substitute.For<IWorkItemRepository>(),
            client,
            pending,
            processConfig,
            parentPropagation: null,
            promptStateWriter: null,
            processRuleProvider: new FakeProcessRuleProvider(rules),
            revisionBound: client);

        return (client, workflow);
    }

    private sealed class FakeProcessRuleProvider(IReadOnlyList<ProcessRule> rules) : IProcessRuleProvider
    {
        public Task<IReadOnlyList<ProcessRule>> GetRulesAsync(string workItemTypeName, CancellationToken ct = default)
            => Task.FromResult(rules);
    }

    private sealed class WorkItemHandler(string authoritativeFields, bool failRevisionFetch = false) : HttpMessageHandler
    {
        private string _state = "To Do";
        private int _rev = Revision;

        public int PatchCount { get; private set; }

        public List<string> RevisionUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;

            if (request.Method == HttpMethod.Patch)
            {
                PatchCount++;
                _state = "Doing";
                _rev++;
                return Json(Item());
            }

            if (path.Contains("/revisions/", StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = path[path.IndexOf("/workitems/", StringComparison.OrdinalIgnoreCase)..];
                RevisionUrls.Add(trimmed[..trimmed.IndexOf("&api-version", StringComparison.OrdinalIgnoreCase)]);
                if (failRevisionFetch)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                return Json(Item());
            }

            if (path.Contains($"/workitems/{ItemId}", StringComparison.OrdinalIgnoreCase))
                return Json(Item());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private string Item() =>
            $$$"""
            {"id":{{{ItemId}}},"rev":{{{_rev}}},"fields":{"System.WorkItemType":"Task","System.Title":"Gate","System.State":"{{{_state}}}",{{{authoritativeFields}}}}}
            """;

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
