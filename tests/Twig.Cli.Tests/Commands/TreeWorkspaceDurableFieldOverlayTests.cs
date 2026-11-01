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
            services.GetRequiredService<ISeedLinkRepository>(),
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

    private static object? GetFieldOverlay<T>(T command)
        where T : class =>
        typeof(T).GetProperty(
            "FieldOverlay",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?.GetValue(command);

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
    public void ProductionCompositionRegistersTheOverlayForTargetedTree()
    {
        using var services = Compose();
        var overlay = services.GetRequiredService<DurableFieldOverlay>();

        GetFieldOverlay(services.GetRequiredService<TreeRenderingService>()).ShouldBeSameAs(overlay);
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
