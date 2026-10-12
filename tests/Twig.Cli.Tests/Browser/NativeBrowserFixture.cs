using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.Commands;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.Persistence;
using Twig.Rendering;

namespace Twig.Cli.Tests.Browser;

internal sealed class NativeBrowserFixture : IAsyncDisposable
{
    internal const string Organization = "fixture";
    internal const string Project = "Work";
    internal const string Actor = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    internal const string Sibling = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    internal const string ActorPat = "browser-actor-v1";
    internal const string RenewedPat = "browser-actor-v2";
    internal const string SiblingPat = "browser-sibling-v1";
    private readonly string _temp = CanonicalTempRoot.Create("twig-browser-read-");
    private readonly List<ServiceProvider> _runtimes = [];
    private HttpClient _http = null!;
    private ConnectionMigrationService _migration = null!;
    internal BrowserTransport Transport { get; } = new();
    internal ConnectionBindingService Bindings { get; private set; } = null!;
    internal ConnectionBindingTransitionService Transitions { get; private set; } = null!;
    internal TwigConfiguration Configuration { get; private set; } = null!;
    internal TwigPaths Paths { get; private set; } = null!;
    internal IdentityBinding ActorBinding { get; private set; } = null!;
    internal IdentityBinding SiblingBinding { get; private set; } = null!;
    internal string Home => Path.Combine(_temp, "home");

    internal async Task InitializeAsync()
    {
        var root = Path.Combine(_temp, "checkout");
        Directory.CreateDirectory(root);
        InitCommandTestFixture.InitTempWorktree(root).ShouldBeTrue();
        Configuration = new TwigConfiguration { Organization = Organization, Project = Project };
        Configuration.Display.Hints = false;
        Paths = TwigPaths.BuildPaths(Path.Combine(root, ".twig"), Configuration, root);
        Paths = new TwigPaths(Paths.TwigDir, Paths.ConfigPath, Paths.DbPath, root, Path.Combine(Home, "display.json"));
        await Configuration.SaveSplitAsync(Paths);
        using (var attachment = new WorktreeLocalAttachmentStore(Paths, Configuration, TimeProvider.System))
            (await attachment.InitializeAsync()).IsSuccess.ShouldBeTrue();
        using (var registry = new SqliteSystemWorktreeRegistry(Path.Combine(Home, "system.db"), TimeProvider.System))
        {
            var anchor = WorktreeAnchorDetector.Detect(root)!.Value;
            var connection = ConnectionRefResolver.Compute(Configuration);
            (await registry.UpsertConnectionAsync(connection, Organization, Project, null)).IsSuccess.ShouldBeTrue();
            (await registry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor), connection, root)).IsSuccess.ShouldBeTrue();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.DbPath)!);
        using (var legacy = new SqliteCacheStore($"Data Source={Paths.DbPath}"))
            await new SqliteWorkItemRepository(legacy, new WorkItemMapper()).SaveAsync(Item(900, "Legacy content must not leak"));
        _http = new HttpClient(Transport);
        Bindings = new ConnectionBindingService(Home, Substitute.For<ITokenRefresher>(), patHttpClient: _http);
        await Bindings.RegisterPatIdentityAsync("actor", Organization, ActorPat);
        await Bindings.RegisterPatIdentityAsync("sibling", Organization, SiblingPat);
        ActorBinding = await Bindings.CreateBindingAsync(Organization, Project, "actor", makeDefault: true);
        SiblingBinding = await Bindings.CreateBindingAsync(Organization, Project, "sibling", makeDefault: false);
        _migration = new ConnectionMigrationService(Home, Bindings);
        var preview = await _migration.PreviewAsync(Configuration, Paths, "actor", null);
        preview.CanApply.ShouldBeTrue(string.Join("\n", preview.Blockers));
        (await _migration.ApplyAsync(Configuration, Paths, "actor", null, preview.Digest)).State.ShouldBe("active");
        Paths = CurrentPaths();
        Transitions = new ConnectionBindingTransitionService(Home, Bindings);
    }

    internal TwigPaths CurrentPaths()
    {
        var paths = TwigPaths.BuildPaths(Paths.TwigDir, Configuration, Paths.StartDir);
        return new TwigPaths(paths.TwigDir, paths.ConfigPath, paths.DbPath, paths.StartDir, Path.Combine(Home, "display.json"));
    }

    internal async Task<ServiceProvider> CreateRuntimeAsync()
    {
        var paths = CurrentPaths();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(Bindings);
        services.AddConnectionServices(Configuration, paths.TwigDir, paths.StartDir);
        services.AddSingleton(paths);
        services.AddSingleton<ISystemWorktreeRegistry>(_ => new SqliteSystemWorktreeRegistry(Path.Combine(Home, "system.db"), TimeProvider.System));
        services.AddSingleton(_http);
        services.AddTwigNetworkServices(Configuration);
        services.AddTwigRenderingServices();
        services.AddTwigCommandServices();
        var runtime = services.BuildServiceProvider();
        _runtimes.Add(runtime);
        await runtime.GetRequiredService<IProcessTypeStore>().SaveAsync(new ProcessTypeRecord
        {
            TypeName = "Task",
            States = [new("To Do", StateCategory.Proposed, null), new("Doing", StateCategory.InProgress, null), new("Done", StateCategory.Completed, null)],
            DefaultChildType = "Task",
            ValidChildTypes = ["Task"],
        });
        await runtime.GetRequiredService<IBenchRepository>().GetOrCreateDefaultAsync([]);
        return runtime;
    }

    internal BrowserReadModule CreateModule(ServiceProvider runtime, IFieldDefinitionStore? metadata = null,
        IConnectionBindingService? bindings = null, IBenchRepository? benches = null) => new(
        runtime.GetRequiredService<CommandContext>(), benches is null
            ? runtime.GetRequiredService<CurrentBenchResolver>()
            : new CurrentBenchResolver(benches, new DefaultBenchSelectors(runtime.GetRequiredService<IIterationService>())),
        runtime.GetRequiredService<IWorkItemRepository>(),
        metadata ?? runtime.GetRequiredService<IFieldDefinitionStore>(), runtime.GetRequiredService<IAuthenticationProvider>(),
        bindings ?? Bindings, runtime.GetRequiredService<TwigPaths>(), runtime.GetRequiredService<IWorkItemLinkRepository>(),
        runtime.GetRequiredService<IPendingChangeStore>(), runtime.GetRequiredService<StatusFieldConfigReader>(),
        runtime.GetRequiredService<SyncCoordinatorFactory>(), runtime.GetRequiredService<IProcessConfigurationProvider>(),
        runtime.GetRequiredService<SpectreTheme>());

    internal async Task SwitchAsync(string binding)
    {
        var preview = await Transitions.PreviewAsync(Configuration, CurrentPaths(), binding);
        preview.CanApply.ShouldBeTrue(string.Join("\n", preview.Blockers));
        var applied = await Transitions.ApplyAsync(Configuration, CurrentPaths(), binding, preview.Digest);
        applied.CanApply.ShouldBeTrue(string.Join("\n", applied.Blockers));
    }

    internal static WorkItem Item(int id, string title)
    {
        var item = new WorkItem { Id = id, Type = WorkItemType.Task, Title = title, State = "To Do" };
        item.MarkSynced(3);
        return item;
    }

    public ValueTask DisposeAsync()
    {
        Transport.Release.TrySetResult();
        foreach (var runtime in _runtimes) runtime.Dispose();
        Transitions?.Dispose();
        _migration?.Dispose();
        Bindings?.Dispose();
        _http?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
        return ValueTask.CompletedTask;
    }

    internal sealed class BrowserTransport : HttpMessageHandler
    {
        internal List<int> RequestedItems { get; } = [];
        internal int Mutations { get; private set; }
        internal bool HoldNext { get; set; }
        internal bool FailNext { get; set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var encoded = request.Headers.Authorization?.Parameter;
            var secret = encoded is null ? null : Encoding.UTF8.GetString(Convert.FromBase64String(encoded))[1..];
            var actor = secret is ActorPat or RenewedPat ? Actor : secret == SiblingPat ? Sibling : null;
            if (actor is null) return Reply(HttpStatusCode.Unauthorized, new { message = "Unregistered credential" });
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/_apis/connectionData", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { authenticatedUser = new { id = actor, providerDisplayName = "Fixture reader" } });
            const string segment = "/_apis/wit/workitems/";
            var offset = path.IndexOf(segment, StringComparison.OrdinalIgnoreCase);
            if (offset < 0) return Reply(HttpStatusCode.OK, new { count = 0, value = Array.Empty<object>() });
            var id = int.Parse(path[(offset + segment.Length)..].Split('/')[0], System.Globalization.CultureInfo.InvariantCulture);
            RequestedItems.Add(id);
            if (request.Method != HttpMethod.Get)
            {
                Mutations++;
                return Reply(HttpStatusCode.MethodNotAllowed, new { message = "Detail reads cannot publish" });
            }
            if (HoldNext)
            {
                HoldNext = false;
                Started.TrySetResult();
                // Deliberately return a late response even when the consumer has canceled.
                await Release.Task;
            }
            if (FailNext || actor == Sibling && id == 42)
            {
                FailNext = false;
                return Reply(HttpStatusCode.NotFound, new { message = "Selected item unavailable" });
            }
            return Reply(HttpStatusCode.OK, new
            {
                id, rev = 7,
                fields = new Dictionary<string, object>
                {
                    ["System.Title"] = secret == RenewedPat ? "Renewed selected item" : "Remote selected item",
                    ["System.WorkItemType"] = "Task", ["System.State"] = "To Do",
                    ["System.Description"] = "<p>Remote description</p>",
                },
                relations = new[] { new { rel = "System.LinkTypes.Related", url = $"https://dev.azure.com/{Organization}/{Project}/_apis/wit/workItems/99" } },
            });
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, object body) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }
}
