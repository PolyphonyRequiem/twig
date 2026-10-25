using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Commands.SetTree;
using Twig.DependencyInjection;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.Persistence;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class SetTreeDurableFieldOverlayTests : IDisposable
{
    private const string Scope = "11111111-2222-3333-4444-555555555555";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "twig-set-tree-overlay-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly CountingProvider _provider = new();

    public SetTreeDurableFieldOverlayTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class CountingProvider : IWorkItemProvider
    {
        public int Calls { get; private set; }
        public ProviderCapabilityDescriptor Capabilities => ProviderCapabilityCatalog.Ado;

        public Task<string?> ResolveResourceScopeAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<string?>(Scope);
        }
    }

    private sealed class ThrowingAuthenticationProvider : IAuthenticationProvider
    {
        public int Calls { get; private set; }

        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("Cache-only tree-set must not acquire an ADO token.");
        }

        public void InvalidateToken() { }
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new HttpRequestException("Cache-only tree-set must not send an ADO request.");
        }
    }

    private ServiceProvider Compose(
        ThrowingAuthenticationProvider? auth = null, ThrowingHttpHandler? http = null)
    {
        var twigDir = Path.Combine(_root, ".twig");
        Directory.CreateDirectory(twigDir);
        var config = new TwigConfiguration { Organization = "example-org", Project = "ExampleProject" };
        var services = new ServiceCollection();
        services.AddConnectionServices(config, twigDir, _root);
        services.AddSingleton(new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db")));
        services.AddSingleton(_store);
        if (auth is not null && http is not null)
        {
            services.AddTwigNetworkServices(config);
            services.Replace(ServiceDescriptor.Singleton<IAuthenticationProvider>(auth));
            services.Replace(ServiceDescriptor.Singleton(new HttpClient(http)));
        }
        else
        {
            services.AddSingleton<IWorkItemProvider>(_provider);
            services.AddSingleton(Substitute.For<IProviderWorkItemReader>());
            services.AddSingleton(Substitute.For<IIterationService>());
        }
        services.AddTwigRenderingServices();
        services.AddTwigCommandServices();
        services.AddTwigCommands();
        return services.BuildServiceProvider();
    }

    private static Twig.Domain.Aggregates.WorkItem Remote(int id, int? parentId = null)
    {
        var builder = new WorkItemBuilder(id, $"Mirror {id}")
            .AsTask().InState("MirrorState").WithIterationPath(@"Project\Sprint");
        if (parentId is not null)
            builder = builder.WithParent(parentId.Value);
        var item = builder.Build();
        item.MarkSynced(3);
        return item;
    }

    private async Task SeedAsync(IServiceProvider services, int id, int? parentId = null, bool knownScope = true)
    {
        var repo = knownScope
            ? services.GetRequiredService<IWorkItemRepository>()
            : new SqliteWorkItemRepository(_store, new WorkItemMapper());
        await repo.SaveAsync(Remote(id, parentId));
    }

    private static async Task<LocalWorkItemId> OverrideAsync(IServiceProvider services, int id, FieldValues fields)
    {
        var localId = (await services.GetRequiredService<IExternalIdentityMap>().ResolveAsync(
            new ExternalWorkItemIdentity("ado", Scope, id.ToString(CultureInfo.InvariantCulture)), observedToken: null)).Id;
        var local = services.GetRequiredService<ILocalWorkItemStore>();
        var current = (await local.GetAsync(localId))!;
        (await local.UpdateAsync(localId, current.Revision, fields)).Value.ShouldBeOfType<LocalWriteApplied>();
        return localId;
    }

    private static FieldValues TitleFields(int id) =>
        FieldValues.Empty.Set("System.Title", new TextValue($"Durable {id}"));

    private static WorkingSetTreeCommand WithoutOverlay(IServiceProvider services) => new(
        services.GetRequiredService<CommandContext>(),
        services.GetRequiredService<IWorkItemRepository>(),
        services.GetRequiredService<RendererFactory>(),
        services.GetRequiredService<TwigConfiguration>());

    private static async Task<(string Output, string Error)> RunAsync(
        WorkingSetTreeCommand command, string items, string format = "json", int depth = 0,
        bool rootsOnly = false, string? annotations = null)
    {
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var previousError = Console.Error;
        Console.SetError(error);
        try
        {
            var (exit, output) = await StdoutCapture.RunAsync(() =>
                command.ExecuteAsync(items, annotations, format, depth, rootsOnly, "unicode", CancellationToken.None));
            exit.ShouldBe(0);
            return (output, error.ToString());
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    private static IEnumerable<JsonElement> Branches(JsonElement branch)
    {
        yield return branch;
        if (branch.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
                foreach (var descendant in Branches(child))
                    yield return descendant;
        }
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement document) =>
        document.GetProperty("structures").EnumerateArray().SelectMany(Branches);

    private static string Structure(string json)
    {
        using var document = JsonDocument.Parse(json);
        var rows = new List<string>();
        void Visit(JsonElement node, string position)
        {
            var keys = new[] { "id", "parentId", "inWorkingSet", "notInCache", "note", "style", "icon" };
            rows.Add(position + ":" + string.Join("|", keys.Select(key =>
                node.TryGetProperty(key, out var value) ? key + "=" + value.GetRawText() : key + "=absent")));
            if (node.TryGetProperty("children", out var children))
            {
                var childIndex = 0;
                foreach (var child in children.EnumerateArray())
                    Visit(child, position + "." + childIndex++);
            }
        }
        var rootIndex = 0;
        foreach (var root in document.RootElement.GetProperty("structures").EnumerateArray())
            Visit(root, (rootIndex++).ToString(CultureInfo.InvariantCulture));
        rows.Add("count=" + document.RootElement.GetProperty("structureCount").GetInt32());
        rows.Add(document.RootElement.TryGetProperty("missingIds", out var missing) ? missing.GetRawText() : "no-missing");
        return string.Join("\n", rows);
    }

    [Theory]
    [InlineData("json", 41)]
    [InlineData("json", 42)]
    [InlineData("json", 43)]
    [InlineData("human", 41)]
    [InlineData("human", 42)]
    [InlineData("human", 43)]
    [InlineData("minimal", 41)]
    [InlineData("minimal", 42)]
    [InlineData("minimal", 43)]
    public async Task DisplaysDurableTitleForMemberSpineAndDepthChild(string format, int overriddenId)
    {
        using var services = Compose();
        await SeedAsync(services, 41);
        await SeedAsync(services, 42, 41);
        await SeedAsync(services, 43, 42);
        await OverrideAsync(services, overriddenId, TitleFields(overriddenId));

        var (output, error) = await RunAsync(services.GetRequiredService<WorkingSetTreeCommand>(), "42", format, depth: 1);

        output.ShouldContain($"Durable {overriddenId}");
        output.ShouldNotContain($"Mirror {overriddenId}");
        foreach (var untouched in new[] { 41, 42, 43 }.Where(id => id != overriddenId))
            output.ShouldContain($"Mirror {untouched}");
        error.ShouldBeEmpty();
        if (format == "json")
        {
            using var document = JsonDocument.Parse(output);
            var node = Nodes(document.RootElement).Single(node => node.GetProperty("id").GetInt32() == overriddenId);
            node.GetProperty("title").GetString().ShouldBe($"Durable {overriddenId}");
            node.GetProperty("inWorkingSet").GetBoolean().ShouldBe(overriddenId == 42);
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("human")]
    [InlineData("minimal")]
    public async Task DisplaysDurableStateAndTypeCells(string format)
    {
        using var services = Compose();
        await SeedAsync(services, 42);
        await OverrideAsync(services, 42, TitleFields(42)
            .Set("System.State", new TextValue("LocalState"))
            .Set("System.WorkItemType", new TextValue("LocalType")));

        var (output, error) = await RunAsync(services.GetRequiredService<WorkingSetTreeCommand>(), "42", format);

        output.ShouldContain("Durable 42");
        output.ShouldContain("LocalState");
        output.ShouldContain("LocalType");
        output.ShouldNotContain("MirrorState");
        error.ShouldBeEmpty();
        if (format == "json")
        {
            using var document = JsonDocument.Parse(output);
            var node = Nodes(document.RootElement).Single();
            node.GetProperty("state").GetString().ShouldBe("LocalState");
            node.GetProperty("type").GetString().ShouldBe("LocalType");
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task EffectiveFieldsPreserveRawTopologyMembershipAnnotationsAndOrder(bool rootsOnly, int depth)
    {
        using var services = Compose();
        await SeedAsync(services, 41);
        await SeedAsync(services, 42, 41);
        await SeedAsync(services, 43, 42);
        await SeedAsync(services, 44, 41);
        await SeedAsync(services, 50);
        await OverrideAsync(services, 42, TitleFields(42)
            .Set("System.State", new TextValue("LocalState"))
            .Set("System.WorkItemType", new TextValue("LocalType"))
            .Set("System.AreaPath", new TextValue(@"Elsewhere\Area"))
            .Set("System.IterationPath", new TextValue(@"Elsewhere\Sprint"))
            .Set("System.Parent", new IntegerValue(50)));
        const string annotations = """{"42":{"note":"review member","style":"proposed","icon":"icon_parachute"},"404":{"note":"missing member","style":"warn"}}""";
        var raw = await RunAsync(WithoutOverlay(services), "50,44,42,404", depth: depth,
            rootsOnly: rootsOnly, annotations: annotations);

        var shown = await RunAsync(services.GetRequiredService<WorkingSetTreeCommand>(), "50,44,42,404",
            depth: depth, rootsOnly: rootsOnly, annotations: annotations);

        shown.Output.ShouldContain("Durable 42");
        shown.Output.ShouldNotContain("Mirror 42");
        Structure(shown.Output).ShouldBe(Structure(raw.Output));
        shown.Error.ShouldBe(raw.Error);
        shown.Error.ShouldContain("#404");
        using var document = JsonDocument.Parse(shown.Output);
        var roots = document.RootElement.GetProperty("structures").EnumerateArray().ToArray();
        roots.Select(node => node.GetProperty("id").GetInt32())
            .ShouldBe(rootsOnly ? new[] { 50, 44, 42, 404 } : new[] { 50, 41, 404 });
        var nodes = Nodes(document.RootElement).ToArray();
        nodes.Any(node => node.GetProperty("id").GetInt32() == 43).ShouldBe(depth > 0);
        var member = nodes.Single(node => node.GetProperty("id").GetInt32() == 42);
        member.GetProperty("parentId").GetInt32().ShouldBe(41);
        member.GetProperty("note").GetString().ShouldBe("review member");
        member.GetProperty("style").GetString().ShouldBe("proposed");
        member.GetProperty("icon").GetString().ShouldBe("icon_parachute");
        if (!rootsOnly)
        {
            roots[1].GetProperty("inWorkingSet").GetBoolean().ShouldBeFalse();
            roots[1].GetProperty("children").EnumerateArray()
                .Select(node => node.GetProperty("id").GetInt32()).ShouldBe(new[] { 44, 42 });
        }
    }

    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    [InlineData("json-full")]
    [InlineData("json-compact")]
    [InlineData("minimal")]
    [InlineData("ids")]
    public async Task NoOverridesIsByteIdenticalToOptionalNullOverlay(string format)
    {
        using var services = Compose();
        await SeedAsync(services, 41);
        await SeedAsync(services, 42, 41);
        await SeedAsync(services, 43, 42);
        const string annotations = """{"42":{"note":"unchanged","style":"muted"}}""";

        var expected = await RunAsync(WithoutOverlay(services), "42,404", format, depth: 1, annotations: annotations);
        var actual = await RunAsync(services.GetRequiredService<WorkingSetTreeCommand>(), "42,404",
            format, depth: 1, annotations: annotations);

        actual.ShouldBe(expected);
    }

    [Fact]
    public async Task UnknownScopeFallsBackWithoutResolvingTheProvider()
    {
        using var services = Compose();
        await SeedAsync(services, 42, knownScope: false);
        await OverrideAsync(services, 42, TitleFields(42));

        var shown = await RunAsync(services.GetRequiredService<WorkingSetTreeCommand>(), "42");

        shown.Output.ShouldContain("Mirror 42");
        shown.Output.ShouldNotContain("Durable 42");
        shown.Error.ShouldBeEmpty();
        _provider.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ColdAdoCompositionDoesNotAcquireAuthOrSendHttp()
    {
        var auth = new ThrowingAuthenticationProvider();
        using var http = new ThrowingHttpHandler();
        using var services = Compose(auth, http);
        await SeedAsync(services, 41, knownScope: false);
        await SeedAsync(services, 42, 41, knownScope: false);
        await SeedAsync(services, 43, 42, knownScope: false);
        await OverrideAsync(services, 42, TitleFields(42));

        var shown = await RunAsync(services.GetRequiredService<WorkingSetTreeCommand>(), "42", depth: 1);

        foreach (var id in new[] { 41, 42, 43 })
            shown.Output.ShouldContain($"Mirror {id}");
        shown.Output.ShouldNotContain("Durable 42");
        shown.Error.ShouldBeEmpty();
        auth.Calls.ShouldBe(0);
        http.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task RenderingDoesNotPersistEffectiveFieldsOrAdvanceRevisions()
    {
        using var services = Compose();
        var local = services.GetRequiredService<ILocalWorkItemStore>();
        var mirror = services.GetRequiredService<IWorkItemRepository>();
        var snapshots = new Dictionary<int, WorkItemSnapshot>();
        var records = new Dictionary<LocalWorkItemId, LocalWorkItemRecord>();
        foreach (var id in new[] { 41, 42, 43 })
        {
            await SeedAsync(services, id, id == 41 ? null : id - 1);
            var localId = await OverrideAsync(services, id, TitleFields(id)
                .Set("System.State", new TextValue("LocalState"))
                .Set("System.WorkItemType", new TextValue("LocalType"))
                .Set("System.AssignedTo", new TextValue("Local assignee"))
                .Set("System.AreaPath", new TextValue(@"Elsewhere\Area"))
                .Set("System.IterationPath", new TextValue(@"Elsewhere\Sprint"))
                .Set("Custom.Detail", new TextValue("Local detail")));
            records.Add(localId, (await local.GetAsync(localId))!);
            snapshots.Add(id, (await ((SqliteWorkItemRepository)mirror).GetSnapshotByIdAsync(id))!);
        }
        var providerCalls = _provider.Calls;

        var shown = await RunAsync(services.GetRequiredService<WorkingSetTreeCommand>(), "42", depth: 1);

        foreach (var id in snapshots.Keys)
        {
            shown.Output.ShouldContain($"Durable {id}");
            var after = (await ((SqliteWorkItemRepository)mirror).GetSnapshotByIdAsync(id))!;
            after.Fields.ShouldBe(snapshots[id].Fields);
            after.ShouldBe(snapshots[id] with { Fields = after.Fields });
        }
        foreach (var (id, before) in records)
            (await local.GetAsync(id)).ShouldBe(before);
        _provider.Calls.ShouldBe(providerCalls);
        shown.Error.ShouldBeEmpty();
    }

    [Fact]
    public async Task ProductionRegistrationUsesTheSameSingletonAndDisplaysDurableValues()
    {
        using var services = Compose();
        await SeedAsync(services, 42);
        await OverrideAsync(services, 42, TitleFields(42));
        var command = services.GetRequiredService<WorkingSetTreeCommand>();
        var shown = await RunAsync(command, "42");
        shown.Output.ShouldContain("Durable 42");

        // Only the new wiring seam is reflected, keeping this file compilable on the pinned baseline.
        var property = typeof(WorkingSetTreeCommand).GetProperty("FieldOverlay", BindingFlags.Instance | BindingFlags.NonPublic);
        property.ShouldNotBeNull();
        property!.GetValue(command).ShouldBeSameAs(services.GetRequiredService<DurableFieldOverlay>());
        command.ShouldBeSameAs(services.GetRequiredService<WorkingSetTreeCommand>());
    }

    [Theory]
    [InlineData("json")]
    [InlineData("human")]
    [InlineData("minimal")]
    public async Task MissingPlaceholderAndStderrRemainRawEvenWithADurableIdentity(string format)
    {
        using var services = Compose();
        await SeedAsync(services, 42);
        await OverrideAsync(services, 42, TitleFields(42));
        await OverrideAsync(services, 404, TitleFields(404));
        const string annotations = """{"404":{"note":"missing member","style":"warn"}}""";
        var raw = await RunAsync(WithoutOverlay(services), "42,404", format, annotations: annotations);

        var shown = await RunAsync(services.GetRequiredService<WorkingSetTreeCommand>(), "42,404",
            format, annotations: annotations);

        shown.Output.ShouldContain("Durable 42");
        shown.Output.ShouldNotContain("Durable 404");
        shown.Output.ShouldContain("not in cache");
        shown.Output.ShouldContain("missing member");
        shown.Error.ShouldBe(raw.Error);
        shown.Error.ShouldContain("#404");
        if (format == "json")
        {
            using var document = JsonDocument.Parse(shown.Output);
            var missing = Nodes(document.RootElement).Single(node => node.GetProperty("id").GetInt32() == 404);
            missing.GetProperty("notInCache").GetBoolean().ShouldBeTrue();
            missing.GetProperty("inWorkingSet").GetBoolean().ShouldBeTrue();
            missing.TryGetProperty("children", out _).ShouldBeFalse();
            document.RootElement.GetProperty("missingIds")[0].GetProperty("id").GetInt32().ShouldBe(404);
        }
    }

    [Fact]
    public async Task IdsOutputRemainsIdentityOnlyWithOverrides()
    {
        using var services = Compose();
        await SeedAsync(services, 42);
        await OverrideAsync(services, 42, TitleFields(42));

        var expected = await RunAsync(WithoutOverlay(services), "42", "ids");
        var actual = await RunAsync(services.GetRequiredService<WorkingSetTreeCommand>(), "42", "ids");

        actual.ShouldBe(expected);
        actual.Output.Trim().ShouldBe("42");
        actual.Output.ShouldNotContain("Durable");
    }
}
