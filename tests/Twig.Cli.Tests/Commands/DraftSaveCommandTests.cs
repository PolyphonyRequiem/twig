using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Projections;
using Twig.Domain.Services;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Plan;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class DraftSaveCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("twig-save-command-").FullName;

    [Theory]
    [InlineData("json")]
    [InlineData("human")]
    [InlineData("minimal")]
    public async Task GeneratedParserForwardsSeparateAuthorizationThroughRealNativeComposition(string format)
    {
        using var h = await CreateAsync();
        var result = Run(h, Request(h), format);
        result.Exit.ShouldBe(0, result.Error);
        result.Error.ShouldBeEmpty();
        if (format == "json")
        {
            using var json = JsonDocument.Parse(result.Out);
            json.RootElement.GetProperty("status").GetString().ShouldBe("saved");
            json.RootElement.GetProperty("verified").GetBoolean().ShouldBeTrue();
            json.RootElement.GetProperty("settled").GetBoolean().ShouldBeTrue();
            json.RootElement.GetProperty("hostAttested").GetBoolean().ShouldBeFalse();
            json.RootElement.GetProperty("callerIdentity").GetString().ShouldBe("caller-account");
        }
        else result.Out.Trim().ShouldBe($"saved\t{h.Digest}");
        var journal = (await new SqlitePlanJournalRepository(h.Cache).GetAsync(h.Digest))!;
        journal.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        journal.AuthorizerIdentity.ShouldBe(Request(h).Authorization.AuthorizerIdentity);
        (await new SqliteRecoverableWorkItemDraftStore(h.Cache).GetAsync(h.Key))!.Changes.ShouldBeEmpty();
        await h.Ado.Received(1).PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(changes =>
            changes.Count == 1 && changes[0].FieldName == "System.Title" && changes[0].NewValue == "intent"),
            5, Arg.Any<CancellationToken>());
        await h.Ado.Received(1).FetchAsync(42, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("caller")]
    [InlineData("model")]
    [InlineData("surface")]
    [InlineData("named-human")]
    public async Task InvalidAuthorizationCannotMintHumanApprovalOrTouchNativeJournal(string fault)
    {
        using var h = await CreateAsync();
        var request = Request(h);
        request = fault switch
        {
            "digest" => request with { Authorization = request.Authorization with { Digest = new string('a', 64) } },
            "caller" => request with { CallerIdentity = "other-account" },
            "model" => request with { Authorization = request.Authorization with { Mode = ProposalAuthorizationMode.Model } },
            "surface" => request with { Surface = "agent-tool" },
            _ => request with { Authorization = request.Authorization with { AuthorizerIdentity = "claimed person" } },
        };
        var result = Run(h, request);
        result.Exit.ShouldBe(fault == "caller" ? 1 : 2);
        using var error = JsonDocument.Parse(result.Error);
        error.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrWhiteSpace();
        h.Ado.ReceivedCalls().ShouldBeEmpty();
        var journal = (await new SqlitePlanJournalRepository(h.Cache).GetAsync(h.Digest))!;
        journal.AuthorizationMode.ShouldBeNull();
        journal.Operations.Single().State.ShouldBe(PlanOperationState.Planned);
    }

    [Fact]
    public async Task LostReadbackReportsIndeterminateNotSavedAndRetainsDraft()
    {
        using var h = await CreateAsync();
        h.Ado.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns<Task<WorkItem>>(_ => throw new HttpRequestException("Fixture lost readback"));
        var result = Run(h, Request(h));
        result.Exit.ShouldBe(1);
        using var json = JsonDocument.Parse(result.Out);
        json.RootElement.GetProperty("status").GetString().ShouldBe("not-verified");
        json.RootElement.GetProperty("operations")[0].GetProperty("state").GetString().ShouldBe("Indeterminate");
        (await new SqliteRecoverableWorkItemDraftStore(h.Cache).GetAsync(h.Key))!.Changes.ShouldNotBeEmpty();
        await h.Ado.Received(1).PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StrictReaderRejectsUnknownNestedPropertiesDuplicatesAndMissingAuthorization()
    {
        using var h = await CreateAsync();
        var valid = JsonSerializer.Serialize(Request(h), TwigJsonContext.Default.DraftSaveRequest);
        foreach (var malformed in new[]
        {
            "{}", valid.Insert(1, "\"version\":1,"), valid.Insert(1, "\"effects\":[],"),
            valid.Replace("\"mode\":0", "\"extra\":true,\"mode\":0", StringComparison.Ordinal),
        })
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(malformed));
            await Should.ThrowAsync<JsonException>(() => DraftEditingCommand.ReadSaveRequestAsync(input, default));
        }
        h.Ado.ReceivedCalls().ShouldBeEmpty();
    }

    private static DraftSaveRequest Request(Harness h) => new(1, h.Digest, "caller-account",
        "twig-workbench-explicit-save", new ProposalAuthorization
        {
            Digest = h.Digest, Mode = ProposalAuthorizationMode.Human,
            AuthorizerIdentity = "workbench-ui-user (individual identity unverified)",
            Rationale = "Explicit Save gesture; caller/surface attribution only, not host-attested identity.",
            AuthorizedAt = DateTimeOffset.UtcNow,
        });

    private static (int Exit, string Out, string Error) Run(Harness h, DraftSaveRequest request, string format = "json")
    {
        using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request, TwigJsonContext.Default.DraftSaveRequest));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var context = new CommandContext(null!, null!, null!, h.Config, Stderr: error);
        var exit = Twig.TwigConsoleApp.Run(["draft", "save", "--stdin", "-o", format], services =>
            services.AddSingleton(new DraftSaveCommand(context, h.Lifecycle, h.Origin, output, input)));
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<Harness> CreateAsync()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"))).FullName;
        var cache = new SqliteCacheStore($"Data Source={Path.Combine(directory, "twig.db")};Pooling=False");
        var key = new QualifiedWorkItemKey(directory, "connection", "binding", 42);
        var origin = new PlanOrigin(directory, "fingerprint", 1, "connection", "binding", 1,
            "selection", 1, "caller-account", "method", "credential", "tenant", "object", "issuer", "authority", null);
        var repository = new SqliteWorkItemRepository(cache, new WorkItemMapper());
        var before = new WorkItemSnapshot { Id = 42, Revision = 5, TypeName = "Fixture", Title = "baseline", State = "Before" };
        await repository.SaveAsync(new WorkItemMapper().Map(before));
        await new SqliteProcessTypeStore(cache).SaveAsync(new ProcessTypeRecord
        {
            TypeName = "Fixture", States = [new("Before", StateCategory.Proposed, null)],
        });
        before = (await repository.GetSnapshotByIdAsync(42))!;
        var draft = RecoverableWorkItemDraft.Create(key, 5).SetField("System.Title", "baseline", "intent");
        (await new SqliteRecoverableWorkItemDraftStore(cache).TrySaveAsync(draft, null)).ShouldBeTrue();
        var benches = new SqliteBenchRepository(cache);
        var bench = (await benches.CreateAsync("inspected"))!;
        await benches.AddSelectorAsync(bench.Id, BenchSelector.ForItem(42));
        bench = (await benches.GetAllAsync()).Single();
        var source = new SqliteDraftCaptureEvidenceStore(cache);
        var evidence = new DraftCaptureEvidence(1, key, origin, null, before.TypeName, 5,
            source.ProcessFingerprint(), SqliteDraftCaptureEvidenceStore.SnapshotFingerprint(before),
            new FormLayout("fixture", "fixture", []) { SystemControls =
                [new LayoutControl("System.Title", "Title", "FieldControl", false, true, false)] },
            [], bench, null);
        await source.RecordAsync(evidence, before, default);
        var workspace = new PlanWorkspace { Organization = "scope", Project = "project" };
        var journal = await new SqliteDraftProposalCaptureStore(cache).CaptureQualifiedAsync(
            draft, before, origin, workspace, Path.Combine(directory, "proposals"),
            SqliteDraftCaptureEvidenceStore.Fingerprint(evidence));
        var ado = Substitute.For<IAdoWorkItemService>();
        ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>()).Returns(6);
        ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(new WorkItemMapper().Map(before with { Revision = 6, Title = "intent" }));
        var bound = Substitute.For<IRevisionBoundAdoWorkItemService>();
        bound.FetchAtRevisionAsync(42, 5, Arg.Any<CancellationToken>()).Returns(before);
        var fields = Substitute.For<IFieldDefinitionStore>();
        var links = Substitute.For<ISeedLinkRepository>();
        var mapping = Substitute.For<IPublishIdMapRepository>();
        var intents = Substitute.For<IPublishIntentRepository>();
        var pending = new SqlitePendingChangeStore(cache);
        var unit = new SqliteUnitOfWork(cache);
        var seed = new SeedPublishOrchestrator(repository, ado, links, Substitute.For<IWorkItemLinkRepository>(),
            mapping, Substitute.For<ISeedPublishRulesProvider>(), unit, new BacklogOrderer(ado, fields), pending,
            intents, Twig.TestKit.ReferenceProfileBuilder.SprintPolicy());
        var steering = Substitute.For<ISessionSteeringModeProvider>();
        steering.Resolve().Returns(SessionSteeringMode.HumanSteered);
        var origins = Substitute.For<IPlanOriginProvider>();
        origins.GetOriginAsync(Arg.Any<CancellationToken>()).Returns(origin);
        var rules = Substitute.For<IProcessRuleProvider>();
        rules.GetRulesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<ProcessRule>());
        var config = new TwigConfiguration { Organization = workspace.Organization, Project = workspace.Project };
        var twig = Directory.CreateDirectory(Path.Combine(directory, ".twig")).FullName;
        var lifecycle = new PlanLifecycleService(new PlanDocumentParser(), new SqlitePlanJournalRepository(cache),
            pending, fields, new AdoProviderWorkItemWriter(ado), AdoProviderRevisionCodec.Instance, bound, seed,
            repository, links, Substitute.For<IStagedIdentityRegistry>(), mapping, intents, config,
            new TwigPaths(twig, Path.Combine(twig, "config"), Path.Combine(directory, "twig.db"), directory),
            TimeProvider.System, steering, origins, rules, unit, systemRegistryPath: Path.Combine(directory, "system.db"));
        return new(cache, key, journal.Digest, lifecycle, origins, ado, config);
    }

    private sealed record Harness(SqliteCacheStore Cache, QualifiedWorkItemKey Key, string Digest,
        PlanLifecycleService Lifecycle, IPlanOriginProvider Origin, IAdoWorkItemService Ado,
        TwigConfiguration Config) : IDisposable
    {
        public void Dispose() => Cache.Dispose();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
