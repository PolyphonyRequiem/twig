using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.Persistence;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Durable field overlays change what <c>twig show</c> displays for the item, its parent and its
/// children; identity, selection and the stored mirror stay owned by the mirror.
/// </summary>
public sealed class ShowDurableFieldOverlayTests : IDisposable
{
    private const string Scope = "11111111-2222-3333-4444-555555555555";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "twig-show-overlay-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly CountingProvider _provider = new();
    private readonly IProviderWorkItemReader _reader = Substitute.For<IProviderWorkItemReader>();

    public ShowDurableFieldOverlayTests() => Directory.CreateDirectory(_root);

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
            throw new InvalidOperationException("Display-only show must not acquire an ADO token.");
        }

        public void InvalidateToken() { }
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new HttpRequestException("Display-only show must not send an ADO request.");
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
        services.AddTwigRenderingServices();
        services.AddTwigCommandServices();
        services.AddTwigCommands();
        return (services.BuildServiceProvider(), auth, http);
    }

    private static Twig.Domain.Aggregates.WorkItem Remote(int id, string title, int? parentId = null)
    {
        var builder = new WorkItemBuilder(id, title).InState("Active").WithIterationPath(@"Project\Sprint 1");
        if (parentId is not null)
            builder = builder.WithParent(parentId.Value);
        var item = builder.Build();
        item.MarkSynced(3);
        return item;
    }

    private async Task SeedAsync(
        ServiceProvider services, int itemId, string remoteTitle, string? localTitle = null, int? parentId = null)
    {
        await services.GetRequiredService<IWorkItemRepository>().SaveAsync(Remote(itemId, remoteTitle, parentId));
        if (localTitle is null)
            return;

        var identity = new ExternalWorkItemIdentity("ado", Scope, itemId.ToString(CultureInfo.InvariantCulture));
        var localId = (await services.GetRequiredService<IExternalIdentityMap>().FindAsync(identity))!.Value;
        var localStore = services.GetRequiredService<ILocalWorkItemStore>();
        var current = (await localStore.GetAsync(localId))!;
        (await localStore.UpdateAsync(localId, current.Revision, FieldValues.Empty.Set("System.Title", new TextValue(localTitle))))
            .Value.ShouldBeOfType<LocalWriteApplied>();
    }

    private async Task SeedWithoutResolvingScopeAsync(
        IServiceProvider services, int itemId, string remoteTitle, string? localTitle = null, int? parentId = null)
    {
        await new SqliteWorkItemRepository(_store, new WorkItemMapper())
            .SaveAsync(Remote(itemId, remoteTitle, parentId));
        if (localTitle is null)
            return;

        var identity = new ExternalWorkItemIdentity("ado", Scope, itemId.ToString(CultureInfo.InvariantCulture));
        var localId = (await services.GetRequiredService<IExternalIdentityMap>()
            .ResolveAsync(identity, observedToken: null)).Id;
        (await services.GetRequiredService<ILocalWorkItemStore>().UpdateAsync(
            localId, 1, FieldValues.Empty.Set("System.Title", new TextValue(localTitle))))
            .Value.ShouldBeOfType<LocalWriteApplied>();
    }

    private static async Task<string> ShowAsync(
        IServiceProvider services, int id, string format = "json", string? sections = null)
    {
        var (exit, output) = await StdoutCapture.RunAsync(() =>
            services.GetRequiredService<ShowCommand>().ExecuteAsync(id, format, sections: sections));
        exit.ShouldBe(0);
        return output;
    }

    private static ShowCommand ShowWithoutOverlay(IServiceProvider sp) => new(
        sp.GetRequiredService<CommandContext>(),
        sp.GetRequiredService<IWorkItemRepository>(),
        sp.GetRequiredService<IWorkItemLinkRepository>(),
        sp.GetRequiredService<SyncCoordinatorFactory>(),
        sp.GetRequiredService<StatusFieldConfigReader>(),
        fieldDefinitionStore: sp.GetService<IFieldDefinitionStore>(),
        processConfigProvider: sp.GetService<IProcessConfigurationProvider>(),
        contextStore: sp.GetService<IContextStore>(),
        activeItemResolver: sp.GetService<ActiveItemResolver>(),
        pendingChangeStore: sp.GetService<IPendingChangeStore>(),
        workingSetService: sp.GetService<WorkingSetService>(),
        twigPaths: sp.GetService<TwigPaths>(),
        adoGitService: sp.GetService<IAdoGitService>(),
        treeRenderingService: sp.GetService<TreeRenderingService>(),
        rendererFactory: sp.GetRequiredService<Twig.Rendering.RendererFactory>());

    private static string Title(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("title").GetString()!;
    }

    [Fact]
    public async Task ShowJsonDisplaysTheDurableTitleWithoutChangingTheMirror()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title", "Local title");

        Title(await ShowAsync(services, 42)).ShouldBe("Local title");

        (await services.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42))!.Title.ShouldBe("Remote title");
    }

    [Fact]
    public async Task ShowWithoutLocalOverridesDisplaysTheMirror()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title");

        Title(await ShowAsync(services, 42)).ShouldBe("Remote title");
    }

    [Theory]
    [InlineData("human")]
    [InlineData("markdown")]
    public async Task ShowHumanAndMarkdownDisplayTheDurableTitle(string format)
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title", "Local title");

        var output = await ShowAsync(services, 42, format);

        output.ShouldContain("Local title");
        output.ShouldNotContain("Remote title");
    }

    [Fact]
    public async Task ShowProjectionDisplaysDurableParentAndChildTitles()
    {
        using var services = Compose();
        await SeedAsync(services, 41, "Remote parent", "Local parent");
        await SeedAsync(services, 42, "Remote title", "Local title", parentId: 41);
        await SeedAsync(services, 43, "Remote child", "Local child", parentId: 42);

        var output = await ShowAsync(services, 42, sections: "parent,children");

        output.ShouldContain("Local title");
        output.ShouldContain("Local parent");
        output.ShouldContain("Local child");
        output.ShouldNotContain("Remote parent");
        output.ShouldNotContain("Remote child");
    }

    [Fact]
    public async Task ShowBatchDisplaysDurableTitlesInRequestedOrder()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title", "Local title");
        await SeedAsync(services, 43, "Untouched remote");

        var (exit, output) = await StdoutCapture.RunAsync(() =>
            services.GetRequiredService<ShowCommand>().ExecuteBatchAsync("43,42", "json"));

        exit.ShouldBe(0);
        output.ShouldContain("Local title");
        output.ShouldContain("Untouched remote");
        output.ShouldNotContain("Remote title");
        output.IndexOf("Untouched remote", StringComparison.Ordinal)
            .ShouldBeLessThan(output.IndexOf("Local title", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("human")]
    [InlineData("markdown")]
    public async Task ShowWithoutLocalOverridesIsByteIdenticalWithoutOverlay(string format)
    {
        using var services = Compose();
        await SeedAsync(services, 41, "Remote parent");
        await SeedAsync(services, 42, "Remote title", parentId: 41);
        await SeedAsync(services, 43, "Remote child", parentId: 42);

        var composed = await ShowAsync(services, 42, format);
        var (exit, optedOut) = await StdoutCapture.RunAsync(() =>
            ShowWithoutOverlay(services).ExecuteAsync(42, format));

        exit.ShouldBe(0);
        optedOut.ShouldBe(composed);
    }

    [Fact]
    public async Task ShowIncludeFieldsExportStaysRawMirror()
    {
        using var services = Compose(new TwigConfiguration { Organization = "example-org", Project = "ExampleProject" });
        await SeedAsync(services, 42, "Remote title", "Local title");

        var (exit, output) = await StdoutCapture.RunAsync(() =>
            services.GetRequiredService<ShowCommand>().ExecuteBatchAsync("42", "json", includeFields: true));

        exit.ShouldBe(0);
        output.ShouldContain("\"exportVersion\"");
        output.ShouldContain("Remote title");
        output.ShouldNotContain("Local title");
    }

    [Fact]
    public void ProductionCompositionResolvesShowWithTheDurableOverlay()
    {
        using var services = Compose();

        services.GetRequiredService<ShowCommand>().FieldOverlay
            .ShouldBeSameAs(services.GetRequiredService<DurableFieldOverlay>());
    }

    [Fact]
    public async Task ShowWithoutIdResolvesTheActiveItemAndDisplaysTheDurableTitle()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title", "Local title");
        await services.GetRequiredService<IContextStore>().SetActiveWorkItemIdAsync(42);

        var (exit, output) = await StdoutCapture.RunAsync(() =>
            services.GetRequiredService<ShowCommand>().ExecuteAsync(outputFormat: "json"));

        exit.ShouldBe(0);
        Title(output).ShouldBe("Local title");
    }

    [Fact]
    public async Task ShowRefreshUpdatesTheMirrorButStillDisplaysTheDurableTitleWithoutPersistingIt()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title", "Local title");
        var fresh = new WorkItemBuilder(42, "Fresh remote title").InState("Active").WithIterationPath(@"Project\Sprint 1").Build();
        fresh.MarkSynced(4);
        _reader.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((fresh, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));
        var command = services.GetRequiredService<ShowCommand>();
        var localId = (await services.GetRequiredService<IExternalIdentityMap>()
            .FindAsync(new ExternalWorkItemIdentity("ado", Scope, "42")))!.Value;
        var localStore = services.GetRequiredService<ILocalWorkItemStore>();
        var before = (await localStore.GetAsync(localId))!;

        var (exit, output) = await StdoutCapture.RunAsync(() => command.ExecuteAsync(42, "json", refresh: true));
        var (projectedExit, projected) = await StdoutCapture.RunAsync(() =>
            command.ExecuteAsync(42, "json", refresh: true, sections: "parent,children"));

        exit.ShouldBe(0);
        projectedExit.ShouldBe(0);
        Title(output).ShouldBe("Local title");
        projected.ShouldContain("Local title");
        projected.ShouldNotContain("Fresh remote title");
        await _reader.Received().FetchWithLinksAsync(42, Arg.Any<CancellationToken>());
        (await services.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42))!.Title.ShouldBe("Fresh remote title");

        var after = (await localStore.GetAsync(localId))!;
        after.Revision.ShouldBe(before.Revision);
        after.Fields.ShouldBe(before.Fields);

        // A later cache-only read still sees the durable title, so the refresh neither lost nor rewrote it.
        Title(await ShowAsync(services, 42)).ShouldBe("Local title");
    }

    [Fact]
    public async Task ShowFallsBackToTheMirrorWhenScopeIsNotAlreadyKnown()
    {
        using var services = Compose();
        await SeedWithoutResolvingScopeAsync(services, 42, "Remote title", "Local title");

        Title(await ShowAsync(services, 42)).ShouldBe("Remote title");

        _provider.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ColdProductionAdoCompositionDoesNotAcquireAuthOrSendHttpForShow()
    {
        var (services, auth, http) = ComposeColdAdo();
        using (services)
        {
            await SeedWithoutResolvingScopeAsync(services, 42, "Remote title", "Local title");

            Title(await ShowAsync(services, 42)).ShouldBe("Remote title");

            auth.Calls.ShouldBe(0);
            http.Calls.ShouldBe(0);
        }
    }
}
