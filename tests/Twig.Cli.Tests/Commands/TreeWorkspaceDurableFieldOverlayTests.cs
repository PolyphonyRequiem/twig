using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;
using Spectre.Console.Testing;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Aggregates;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.Persistence;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class TreeWorkspaceDurableFieldOverlayTests : IDisposable
{
    private const string Scope = "11111111-2222-3333-4444-555555555555";
    private const string Iteration = @"Project\Sprint 1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "twig-tree-workspace-overlay-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly CountingProvider _provider = new();
    private readonly IProviderWorkItemReader _reader = Substitute.For<IProviderWorkItemReader>();
    private readonly IIterationService _iterationService = Substitute.For<IIterationService>();

    public TreeWorkspaceDurableFieldOverlayTests()
    {
        Directory.CreateDirectory(_root);
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse(Iteration).Value);
        _iterationService.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string? DisplayName, string? UniqueName)>(
                ("Test User", "test@example.com")));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
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
            throw new InvalidOperationException("Cached tree/workspace display must not acquire an ADO token.");
        }

        public void InvalidateToken() { }
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new HttpRequestException("Cached tree/workspace display must not send an ADO request.");
        }
    }

    private ServiceProvider Compose(TwigConfiguration? configuration = null)
    {
        var twigDir = Path.Combine(_root, ".twig");
        Directory.CreateDirectory(twigDir);
        var config = configuration ?? new TwigConfiguration();
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        var services = new ServiceCollection();
        services.AddConnectionServices(config, twigDir, _root);
        services.AddSingleton(paths);
        services.AddSingleton(_store);
        services.AddSingleton<IWorkItemProvider>(_provider);
        services.AddSingleton<IProviderWorkItemReader>(_reader);
        services.AddSingleton<IAuthenticationProvider>(_ => new ThrowingAuthenticationProvider());
        services.AddSingleton(_iterationService);
        services.AddTwigRenderingServices();
        services.AddTwigCommandServices();
        services.AddTwigCommands();
        return services.BuildServiceProvider();
    }

    private (ServiceProvider Services, ThrowingAuthenticationProvider Auth, ThrowingHttpHandler Http) ComposeColdAdo()
    {
        var twigDir = Path.Combine(_root, ".twig-cold-ado");
        Directory.CreateDirectory(twigDir);
        var config = new TwigConfiguration { Organization = "example-org", Project = "ExampleProject" };
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));
        var auth = new ThrowingAuthenticationProvider();
        var http = new ThrowingHttpHandler();

        var services = new ServiceCollection();
        services.AddConnectionServices(config, twigDir, _root);
        services.AddSingleton(paths);
        services.AddSingleton(_store);
        services.AddTwigNetworkServices(config);
        services.Replace(ServiceDescriptor.Singleton<IAuthenticationProvider>(auth));
        services.Replace(ServiceDescriptor.Singleton(new HttpClient(http)));
        services.AddSingleton(_iterationService);
        services.AddTwigRenderingServices();
        services.AddTwigCommandServices();
        services.AddTwigCommands();
        return (services.BuildServiceProvider(), auth, http);
    }

    private static WorkItem Remote(
        int id,
        string title,
        int? parentId = null,
        string iteration = Iteration,
        IReadOnlyDictionary<string, string?>? fields = null,
        string? assignedToUniqueName = null)
    {
        var builder = new WorkItemBuilder(id, title).InState("Active")
            .WithIterationPath(iteration);
        if (parentId.HasValue)
            builder = builder.WithParent(parentId.Value);
        if (assignedToUniqueName is not null)
            builder = builder.AssignedToUniqueName(assignedToUniqueName);
        if (fields is not null)
            foreach (var (name, value) in fields)
                if (value is not null)
                    builder = builder.WithField(name, value);
        var item = builder.Build();
        item.MarkSynced(3);
        return item;
    }

    private async Task SeedAsync(
        ServiceProvider services,
        int id,
        string remoteTitle,
        string? localTitle = null,
        int? parentId = null,
        string iteration = Iteration,
        IReadOnlyDictionary<string, string?>? remoteFields = null,
        IReadOnlyDictionary<string, string>? localFields = null,
        string? assignedToUniqueName = null)
    {
        await services.GetRequiredService<IWorkItemRepository>()
            .SaveAsync(Remote(id, remoteTitle, parentId, iteration, remoteFields, assignedToUniqueName));
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (localTitle is not null)
            overrides["System.Title"] = localTitle;
        if (localFields is not null)
            foreach (var (name, value) in localFields)
                overrides[name] = value;
        if (overrides.Count == 0)
            return;

        var identity = new ExternalWorkItemIdentity("ado", Scope, id.ToString(CultureInfo.InvariantCulture));
        var localId = (await services.GetRequiredService<IExternalIdentityMap>().FindAsync(identity))!.Value;
        var current = (await services.GetRequiredService<ILocalWorkItemStore>().GetAsync(localId))!;
        var fields = FieldValues.Empty;
        foreach (var (name, value) in overrides)
            fields = fields.Set(name, new TextValue(value));
        (await services.GetRequiredService<ILocalWorkItemStore>().UpdateAsync(localId, current.Revision, fields))
            .Value.ShouldBeOfType<LocalWriteApplied>();
    }

    private async Task SeedWithoutResolvingScopeAsync(
        IServiceProvider services, int id, string remoteTitle, string? localTitle = null)
    {
        await new SqliteWorkItemRepository(_store, new WorkItemMapper())
            .SaveAsync(Remote(id, remoteTitle));
        if (localTitle is null)
            return;

        var identity = new ExternalWorkItemIdentity("ado", Scope, id.ToString(CultureInfo.InvariantCulture));
        var localId = (await services.GetRequiredService<IExternalIdentityMap>()
            .ResolveAsync(identity, observedToken: null)).Id;
        (await services.GetRequiredService<ILocalWorkItemStore>().UpdateAsync(
            localId, 1, FieldValues.Empty.Set("System.Title", new TextValue(localTitle))))
            .Value.ShouldBeOfType<LocalWriteApplied>();
    }

    private static async Task<string> ShowTreeAsync(IServiceProvider services, int id, string format)
    {
        var (exit, output) = await StdoutCapture.RunAsync(() =>
            services.GetRequiredService<ShowCommand>().ExecuteAsync(
                id, format, tree: true, noLive: true));
        exit.ShouldBe(0);
        return output;
    }

    private static async Task<string> WorkspaceAsync(
        IServiceProvider services, string format, bool all = false, bool flat = false, string? view = null, bool tree = false)
    {
        var (exit, output) = await StdoutCapture.RunAsync(() =>
            services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(
                format, all: all, flat: flat, view: view, tree: tree, noLive: true));
        exit.ShouldBe(0);
        return output;
    }

    private static ShowCommand ShowWithoutTreeOverlay(IServiceProvider services)
    {
        var tree = new TreeRenderingService(
            services.GetRequiredService<CommandContext>(),
            services.GetRequiredService<IContextStore>(),
            services.GetRequiredService<IWorkItemRepository>(),
            services.GetRequiredService<ActiveItemResolver>(),
            services.GetRequiredService<WorkingSetService>(),
            services.GetRequiredService<SyncCoordinatorFactory>(),
            services.GetRequiredService<IProcessTypeStore>(),
            services.GetRequiredService<Twig.Rendering.RendererFactory>());
        return new ShowCommand(
            services.GetRequiredService<CommandContext>(),
            services.GetRequiredService<IWorkItemRepository>(),
            services.GetRequiredService<IWorkItemLinkRepository>(),
            services.GetRequiredService<SyncCoordinatorFactory>(),
            services.GetRequiredService<StatusFieldConfigReader>(),
            fieldDefinitionStore: services.GetService<IFieldDefinitionStore>(),
            processConfigProvider: services.GetService<IProcessConfigurationProvider>(),
            contextStore: services.GetService<IContextStore>(),
            activeItemResolver: services.GetService<ActiveItemResolver>(),
            pendingChangeStore: services.GetService<IPendingChangeStore>(),
            workingSetService: services.GetService<WorkingSetService>(),
            twigPaths: services.GetService<TwigPaths>(),
            adoGitService: services.GetService<IAdoGitService>(),
            treeRenderingService: tree,
            rendererFactory: services.GetRequiredService<Twig.Rendering.RendererFactory>());
    }

    private static WorkspaceCommand WorkspaceWithoutOverlay(IServiceProvider services)
        => CreateWorkspaceCommand(
            services, services.GetRequiredService<CommandContext>(), includeOverlay: false);

    private static T WithFieldOverlay<T>(T command, DurableFieldOverlay? overlay)
        where T : class
    {
        typeof(T).GetProperty(
                "FieldOverlay",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(command, overlay);
        return command;
    }

    private static object? GetFieldOverlay<T>(T command)
        where T : class =>
        typeof(T).GetProperty(
            "FieldOverlay",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?.GetValue(command);

    private static async Task<Workspace> OverlayWorkspaceAsync(
        WorkspaceCommand command, Workspace workspace)
    {
        var method = typeof(WorkspaceCommand).GetMethod(
            "OverlayDisplayAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (method is null)
            return workspace;
        return await (Task<Workspace>)method.Invoke(command, [workspace, CancellationToken.None])!;
    }

    private static WorkspaceCommand CreateWorkspaceCommand(
        IServiceProvider services, CommandContext context, bool includeOverlay = true)
        => WithFieldOverlay(new WorkspaceCommand(
            context,
            services.GetRequiredService<IContextStore>(),
            services.GetRequiredService<IWorkItemRepository>(),
            services.GetRequiredService<IIterationService>(),
            services.GetRequiredService<IProcessTypeStore>(),
            services.GetRequiredService<IFieldDefinitionStore>(),
            services.GetRequiredService<ActiveItemResolver>(),
            services.GetRequiredService<WorkingSetService>(),
            services.GetRequiredService<ITrackingService>(),
            services.GetRequiredService<ISprintHierarchyBuilder>(),
            services.GetRequiredService<SprintIterationResolver>(),
            services.GetService<TreeRenderingService>(),
            services.GetService<SyncCoordinatorFactory>(),
            services.GetService<Twig.Rendering.RendererFactory>()),
            includeOverlay ? services.GetRequiredService<DurableFieldOverlay>() : null);

    private static WorkspaceCommand CreateBrowserWorkspaceCommand(
        IServiceProvider services, Bench bench, bool includeOverlay)
    {
        var benchRepository = Substitute.For<IBenchRepository>();
        benchRepository.GetAllAsync(Arg.Any<CancellationToken>()).Returns([bench]);
        var resolver = new CurrentBenchResolver(
            benchRepository,
            new DefaultBenchSelectors(services.GetRequiredService<IIterationService>()));
        var evaluator = new BenchEvaluator(
            services.GetRequiredService<IWorkItemRepository>(),
            Substitute.For<IIterationCalendar>(),
            services.GetRequiredService<IPendingChangeStore>());
        var command = new WorkspaceCommand(
            services.GetRequiredService<CommandContext>(),
            services.GetRequiredService<IContextStore>(),
            services.GetRequiredService<IWorkItemRepository>(),
            services.GetRequiredService<IIterationService>(),
            services.GetRequiredService<IProcessTypeStore>(),
            services.GetRequiredService<IFieldDefinitionStore>(),
            services.GetRequiredService<ActiveItemResolver>(),
            services.GetRequiredService<WorkingSetService>(),
            services.GetRequiredService<ITrackingService>(),
            services.GetRequiredService<ISprintHierarchyBuilder>(),
            services.GetRequiredService<SprintIterationResolver>(),
            services.GetService<TreeRenderingService>(),
            services.GetService<SyncCoordinatorFactory>(),
            services.GetService<Twig.Rendering.RendererFactory>(),
            resolver,
            evaluator)
        {
            ResolveBrowserBindingAsync = _ => Task.FromResult(new ResolvedConnectionBinding(
                new IdentityBinding("binding-1", "connection-1", "identity-1", 1),
                new AuthenticationIdentity("identity-1", "Work", "tenant", "object", "issuer", "host", "credential", "user"),
                "C:/checkout", "checkout-binding-pin", 1,
                new ConnectionOperationSnapshot("org", "Project", "Team", "fingerprint", 1,
                    "portable", "preferences", "unicode", "portable"))),
        };
        command = WithFieldOverlay(command, includeOverlay
            ? services.GetRequiredService<DurableFieldOverlay>()
            : null);
        return command;
    }

    [Theory]
    [InlineData("json")]
    [InlineData("human")]
    [InlineData("markdown")]
    public async Task ShowTreeDisplaysDurableRootParentChildAndGrandchild(string format)
    {
        using var services = Compose();
        await SeedAsync(services, 41, "Remote parent", "Local parent");
        await SeedAsync(services, 42, "Remote focus", "Local focus", parentId: 41);
        await SeedAsync(services, 43, "Remote child", "Local child", parentId: 42);
        await SeedAsync(services, 44, "Remote grandchild", "Local grandchild", parentId: 43);

        var output = await ShowTreeAsync(services, 42, format);

        output.ShouldContain("Local focus");
        output.ShouldContain("Local parent");
        output.ShouldContain("Local child");
        output.ShouldContain("Local grandchild");
        output.ShouldNotContain("Remote focus");
        output.ShouldNotContain("Remote parent");
        output.ShouldNotContain("Remote child");
        output.ShouldNotContain("Remote grandchild");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("human")]
    [InlineData("markdown")]
    public async Task TreeWithoutLocalOverridesIsByteIdenticalWithoutOverlay(string format)
    {
        using var services = Compose();
        await SeedAsync(services, 41, "Remote parent");
        await SeedAsync(services, 42, "Remote focus", parentId: 41);
        await SeedAsync(services, 43, "Remote child", parentId: 42);

        var composed = await ShowTreeAsync(services, 42, format);
        var (exit, optedOut) = await StdoutCapture.RunAsync(() =>
            ShowWithoutTreeOverlay(services).ExecuteAsync(
                42, format, tree: true, noLive: true));

        exit.ShouldBe(0);
        optedOut.ShouldBe(composed);
    }

    [Fact]
    public async Task ShowTreeRefreshUpdatesMirrorButDoesNotWriteDurableValues()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title", "Local title");
        await services.GetRequiredService<IBenchRepository>()
            .GetOrCreateDefaultAsync([BenchSelector.ForItem(42)]);

        var fresh = Remote(42, "Fresh remote title");
        _reader.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(fresh);
        _reader.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((fresh, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));
        _reader.FetchChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var localId = (await services.GetRequiredService<IExternalIdentityMap>().FindAsync(
            new ExternalWorkItemIdentity("ado", Scope, "42")))!.Value;
        var localStore = services.GetRequiredService<ILocalWorkItemStore>();
        var before = (await localStore.GetAsync(localId))!;

        var (exit, output) = await StdoutCapture.RunAsync(() =>
            services.GetRequiredService<ShowCommand>().ExecuteAsync(
                42, "json", tree: true, refresh: true, noLive: true));

        exit.ShouldBe(0);
        output.ShouldContain("Local title");
        output.ShouldNotContain("Fresh remote title");
        (await services.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42))!.Title
            .ShouldBe("Fresh remote title");
        var after = (await localStore.GetAsync(localId))!;
        after.Revision.ShouldBe(before.Revision);
        after.Fields.ShouldBe(before.Fields);
    }

    [Fact]
    public async Task WorkspaceDefaultViewDisplaysDurablePinnedItemAndContext()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title", "Local title");
        await services.GetRequiredService<IContextStore>().SetActiveWorkItemIdAsync(42);
        await services.GetRequiredService<IBenchRepository>()
            .GetOrCreateDefaultAsync([BenchSelector.ForItem(42)]);

        var output = await WorkspaceAsync(services, "human");

        output.ShouldContain("Local title");
        output.ShouldNotContain("Remote title");
    }

    [Fact]
    public async Task WorkspaceTreeDisplaysDurableRootParentChildAndGrandchild()
    {
        using var services = Compose();
        await SeedAsync(services, 41, "Remote parent", "Local parent", assignedToUniqueName: "test@example.com");
        await SeedAsync(services, 42, "Remote focus", "Local focus", parentId: 41, assignedToUniqueName: "test@example.com");
        await SeedAsync(services, 43, "Remote child", "Local child", parentId: 42, assignedToUniqueName: "test@example.com");
        await SeedAsync(services, 44, "Remote grandchild", "Local grandchild", parentId: 43, assignedToUniqueName: "test@example.com");
        var workItemRepository = services.GetRequiredService<IWorkItemRepository>();
        foreach (var id in new[] { 41, 42, 43, 44 })
            (await workItemRepository.GetByIdAsync(id))!.AssignedToUniqueName.ShouldBe("test@example.com");
        await services.GetRequiredService<IContextStore>().SetActiveWorkItemIdAsync(42);

        var output = await WorkspaceAsync(services, "json", tree: true);

        output.ShouldContain("Local focus");
        output.ShouldContain("Local parent");
        output.ShouldContain("Local child");
        output.ShouldContain("Local grandchild");
        output.ShouldNotContain("Remote focus");
        output.ShouldNotContain("Remote parent");
        output.ShouldNotContain("Remote child");
        output.ShouldNotContain("Remote grandchild");
    }

    [Fact]
    public async Task WorkspaceOverlayPreservesDistinctDisplayCopiesWithTheSameId()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title", "Local title");
        var contextItem = Remote(42, "Context remote", fields:
            new Dictionary<string, string?> { ["Custom.Origin"] = "context" });
        var firstHierarchyItem = Remote(42, "First hierarchy remote", fields:
            new Dictionary<string, string?> { ["Custom.Origin"] = "first hierarchy" });
        var secondHierarchyItem = Remote(42, "Second hierarchy remote", fields:
            new Dictionary<string, string?> { ["Custom.Origin"] = "second hierarchy" });
        var groups = new Dictionary<string, IReadOnlyList<SprintHierarchyNode>>(StringComparer.Ordinal)
        {
            ["User"] = [new SprintHierarchyNode(firstHierarchyItem, isSprintItem: true)],
            ["user"] = [new SprintHierarchyNode(secondHierarchyItem, isSprintItem: true)],
        };
        var workspace = Workspace.Build(
            contextItem, Array.Empty<WorkItem>(), Array.Empty<WorkItem>(), SprintHierarchy.Create(groups));
        var command = CreateWorkspaceCommand(services, services.GetRequiredService<CommandContext>());

        var displayed = await OverlayWorkspaceAsync(command, workspace);

        displayed.ContextItem!.Title.ShouldBe("Local title");
        displayed.ContextItem.Fields["Custom.Origin"].ShouldBe("context");
        displayed.Hierarchy!.AssigneeGroups.Count.ShouldBe(2);
        displayed.Hierarchy.AssigneeGroups["User"][0].Item.Fields["Custom.Origin"]
            .ShouldBe("first hierarchy");
        displayed.Hierarchy.AssigneeGroups["user"][0].Item.Fields["Custom.Origin"]
            .ShouldBe("second hierarchy");
    }

    [Fact]
    public async Task WorkspaceWithoutDurableOverridesReturnsOriginalProjection()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title");
        var item = (await services.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42))!;
        var workspace = Workspace.Build(null, [item], Array.Empty<WorkItem>());
        var command = CreateWorkspaceCommand(services, services.GetRequiredService<CommandContext>());

        var displayed = await OverlayWorkspaceAsync(command, workspace);

        displayed.ShouldBeSameAs(workspace);
    }

    [Fact]
    public async Task WorkspaceTtyStreamDisplaysDurableContextAndPinnedItem()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title", "Local title");
        await services.GetRequiredService<IContextStore>().SetActiveWorkItemIdAsync(42);
        await services.GetRequiredService<IBenchRepository>()
            .GetOrCreateDefaultAsync([BenchSelector.ForItem(42)]);

        var config = services.GetRequiredService<TwigConfiguration>();
        var formatterFactory = services.GetRequiredService<OutputFormatterFactory>();
        var testConsole = new TestConsole();
        var renderer = new SpectreRenderer(testConsole, new SpectreTheme(config.Display));
        var context = new CommandContext(
            new RenderingPipelineFactory(formatterFactory, renderer, isOutputRedirected: () => false),
            formatterFactory,
            services.GetRequiredService<HintEngine>(),
            config);
        var command = CreateWorkspaceCommand(services, context);

        var exit = await command.ExecuteAsync("human", view: "table");

        exit.ShouldBe(0);
        testConsole.Output.ShouldContain("Local title");
        testConsole.Output.ShouldNotContain("Remote title");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkspaceSprintAndFlatViewsDisplayDurableTitle(bool flat)
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title", "Local title");

        var output = await WorkspaceAsync(services, "human", all: true, flat: flat);

        output.ShouldContain("Local title");
        output.ShouldNotContain("Remote title");
    }

    [Fact]
    public async Task WorkspaceDynamicColumnDisplaysDurableFieldValue()
    {
        var config = new TwigConfiguration
        {
            Display = new DisplayConfig
            {
                Columns = new DisplayColumnsConfig { Sprint = ["Custom.Marker"] },
            },
        };
        using var services = Compose(config);
        await services.GetRequiredService<IFieldDefinitionStore>()
            .SaveBatchAsync([new FieldDefinition("Custom.Marker", "Marker", "string", false)]);
        await SeedAsync(
            services, 42, "Remote title",
            remoteFields: new Dictionary<string, string?> { ["Custom.Marker"] = "Remote marker" },
            localFields: new Dictionary<string, string> { ["Custom.Marker"] = "Local marker" });

        var output = await WorkspaceAsync(services, "json", all: true);

        output.ShouldContain("Custom.Marker");
        output.ShouldContain("Local marker");
        output.ShouldNotContain("Remote marker");
    }

    [Fact]
    public async Task WorkspaceDirtyOrphanDisplaysDurableTitle()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote orphan", "Local orphan", iteration: @"Project\Other");
        await services.GetRequiredService<IPendingChangeStore>()
            .AddChangeAsync(42, "field", "System.Title", "Remote orphan", "Pending title");

        var output = await WorkspaceAsync(services, "human", view: "table");

        output.ShouldContain("Local orphan");
        output.ShouldNotContain("Remote orphan");
    }

    [Fact]
    public async Task WorkspaceFallsBackToMirrorWhenScopeIsUnknown()
    {
        using var services = Compose();
        await SeedWithoutResolvingScopeAsync(services, 42, "Remote title", "Local title");

        var output = await WorkspaceAsync(services, "json", all: true);

        output.ShouldContain("Remote title");
        output.ShouldNotContain("Local title");
        _provider.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task WorkspaceWithoutLocalOverridesIsByteIdenticalWithoutOverlay()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title");
        var noOverlay = WorkspaceWithoutOverlay(services);

        foreach (var format in new[] { "json", "human", "markdown" })
        {
            var composed = await WorkspaceAsync(services, format, all: true);
            var (exit, optedOut) = await StdoutCapture.RunAsync(() =>
                noOverlay.ExecuteAsync(format, all: true, noLive: true));

            exit.ShouldBe(0);
            optedOut.ShouldBe(composed);
        }
    }

    [Fact]
    public async Task BrowserV2BenchReadRemainsByteIdenticalAndRaw()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title", "Local title");
        var bench = new Bench { Id = 7, Name = "Focused", Selectors = [BenchSelector.ForItem(42)] };
        var overlaid = CreateBrowserWorkspaceCommand(services, bench, includeOverlay: true);
        var raw = CreateBrowserWorkspaceCommand(services, bench, includeOverlay: false);

        var (overlaidExit, overlaidOutput) = await StdoutCapture.RunAsync(() =>
            overlaid.ExecuteAsync("json", view: "tree", includeBrowser: true,
                browserVersion: 2, bench: "Focused", expectBench: "7"));
        var (rawExit, rawOutput) = await StdoutCapture.RunAsync(() =>
            raw.ExecuteAsync("json", view: "tree", includeBrowser: true,
                browserVersion: 2, bench: "Focused", expectBench: "7"));

        overlaidExit.ShouldBe(0);
        rawExit.ShouldBe(0);
        overlaidOutput.ShouldBe(rawOutput);
        overlaidOutput.ShouldContain("raw-mirror");
        overlaidOutput.ShouldContain("Remote title");
        overlaidOutput.ShouldNotContain("Local title");
    }

    [Fact]
    public void ProductionCompositionRegistersTheSameOverlayForTreeAndWorkspace()
    {
        using var services = Compose();
        var overlay = services.GetRequiredService<DurableFieldOverlay>();

        GetFieldOverlay(services.GetRequiredService<TreeRenderingService>()).ShouldBeSameAs(overlay);
        GetFieldOverlay(services.GetRequiredService<WorkspaceCommand>()).ShouldBeSameAs(overlay);
    }

    [Fact]
    public async Task ColdAdoCompositionDoesNotAcquireAuthOrSendHttpForCachedTreeDisplay()
    {
        var (services, auth, http) = ComposeColdAdo();
        using (services)
        {
            await SeedWithoutResolvingScopeAsync(services, 42, "Remote title", "Local title");

            var output = await ShowTreeAsync(services, 42, "json");

            output.ShouldContain("Remote title");
            output.ShouldNotContain("Local title");
            auth.Calls.ShouldBe(0);
            http.Calls.ShouldBe(0);
        }
    }
}
