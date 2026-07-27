using System.Text.Json;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;
using Twig.Mcp.Services;
using Xunit;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Infrastructure.Persistence;

namespace Twig.Mcp.Tests.Services;

public sealed class ConnectionScopeFactoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly HttpClient _httpClient;
    private readonly IAuthenticationProvider _authProvider;
    private readonly List<ConnectionScopeFactory> _factories = [];

    public ConnectionScopeFactoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twig-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _httpClient = new HttpClient();
        _authProvider = Substitute.For<IAuthenticationProvider>();
    }

    public void Dispose()
    {
        // Dispose all tracked factories first (closes SQLite connections)
        foreach (var factory in _factories)
            factory.Dispose();
        _factories.Clear();

        // Clear SQLite connection pool to release file handles on Windows
        SqliteConnection.ClearAllPools();

        _httpClient.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch (IOException) { /* Best-effort cleanup — WAL files may still be locked briefly on Windows */ }
        }
    }

    // ── Factory creation ────────────────────────────────────────────

    [Fact]
    public void GetOrCreate_Returns_WorkspaceContext_With_Correct_Key()
    {
        var key = WriteWorkspace("orgA", "proj1");
        using var factory = CreateFactory();

        var context = factory.GetOrCreate(key);

        context.ShouldNotBeNull();
        context.Connection.ShouldBe(key);
    }

    [Fact]
    public void GetOrCreate_Context_Has_All_Services_Populated()
    {
        var key = WriteWorkspace("orgA", "proj1");
        using var factory = CreateFactory();

        var context = factory.GetOrCreate(key);

        context.Config.ShouldNotBeNull();
        context.Paths.ShouldNotBeNull();
        context.Get<IWorkItemRepository>().ShouldNotBeNull();
        context.Get<IContextStore>().ShouldNotBeNull();
        context.Get<IPendingChangeStore>().ShouldNotBeNull();
        context.Get<IAdoWorkItemService>().ShouldNotBeNull();
        context.Get<IIterationService>().ShouldNotBeNull();
        context.Get<IProcessConfigurationProvider>().ShouldNotBeNull();
        context.Get<ActiveItemResolver>().ShouldNotBeNull();
        context.Get<SyncCoordinatorFactory>().ShouldNotBeNull();
        context.Get<ContextChangeService>().ShouldNotBeNull();
        context.Get<WorkingSetService>().ShouldNotBeNull();
        context.Get<McpPendingChangeFlusher>().ShouldNotBeNull();
        context.Get<IPromptStateWriter>().ShouldNotBeNull();
        context.Get<SprintIterationResolver>().ShouldNotBeNull();
        context.Get<ITrackingRepository>().ShouldNotBeNull();
    }

    [Fact]
    public void GetOrCreate_TrackingRepo_Is_FileTrackingRepository()
    {
        var key = WriteWorkspace("orgA", "proj1");
        using var factory = CreateFactory();

        var context = factory.GetOrCreate(key);

        context.Get<ITrackingRepository>().ShouldBeOfType<FileTrackingRepository>();
    }

    [Fact]
    public void GetOrCreate_WithGitConfig_Has_BranchLinkService()
    {
        var key = WriteWorkspace("orgA", "proj1", gitProject: "proj1", gitRepository: "myrepo");
        using var factory = CreateFactory();

        var context = factory.GetOrCreate(key);

        context.GetOptional<BranchLinkService>().ShouldNotBeNull();
    }

    [Fact]
    public void GetOrCreate_WithoutGitConfig_BranchLinkService_IsNull()
    {
        var key = WriteWorkspace("orgA", "proj1");
        using var factory = CreateFactory();

        var context = factory.GetOrCreate(key);

        context.GetOptional<BranchLinkService>().ShouldBeNull();
    }

    [Fact]
    public void GetOrCreate_Config_Matches_Written_Config()
    {
        var key = WriteWorkspace("orgA", "proj1", team: "MyTeam");
        using var factory = CreateFactory();

        var context = factory.GetOrCreate(key);

        context.Config.Organization.ShouldBe("orgA");
        context.Config.Project.ShouldBe("proj1");
        context.Config.Team.ShouldBe("MyTeam");
    }

    // ── Key-based caching (same key → same instance) ────────────────

    [Fact]
    public void GetOrCreate_Same_Key_Returns_Same_Instance()
    {
        var key = WriteWorkspace("orgA", "proj1");
        using var factory = CreateFactory();

        var first = factory.GetOrCreate(key);
        var second = factory.GetOrCreate(key);

        ReferenceEquals(first, second).ShouldBeTrue();
    }

    [Fact]
    public void GetOrCreate_Same_Key_Concurrent_Returns_Same_Instance()
    {
        var key = WriteWorkspace("orgA", "proj1");
        using var factory = CreateFactory();

        ConnectionScope? ctx1 = null;
        ConnectionScope? ctx2 = null;

        Parallel.Invoke(
            () => ctx1 = factory.GetOrCreate(key),
            () => ctx2 = factory.GetOrCreate(key));

        ctx1.ShouldNotBeNull();
        ctx2.ShouldNotBeNull();
        ReferenceEquals(ctx1, ctx2).ShouldBeTrue();
    }

    // ── Context isolation (different keys → different instances) ─────

    [Fact]
    public void GetOrCreate_Different_Keys_Return_Different_Instances()
    {
        var key1 = WriteWorkspace("orgA", "proj1");
        var key2 = WriteWorkspace("orgA", "proj2");
        using var factory = CreateFactory();

        var ctx1 = factory.GetOrCreate(key1);
        var ctx2 = factory.GetOrCreate(key2);

        ReferenceEquals(ctx1, ctx2).ShouldBeFalse();
        ctx1.Connection.ShouldBe(key1);
        ctx2.Connection.ShouldBe(key2);
    }

    [Fact]
    public void Different_Contexts_Have_Independent_Services()
    {
        var key1 = WriteWorkspace("orgA", "proj1");
        var key2 = WriteWorkspace("orgB", "proj2");
        using var factory = CreateFactory();

        var ctx1 = factory.GetOrCreate(key1);
        var ctx2 = factory.GetOrCreate(key2);

        // Each context should have its own independent service instances
        ReferenceEquals(ctx1.Get<IWorkItemRepository>(), ctx2.Get<IWorkItemRepository>()).ShouldBeFalse();
        ReferenceEquals(ctx1.Get<IContextStore>(), ctx2.Get<IContextStore>()).ShouldBeFalse();
        ReferenceEquals(ctx1.Get<IPendingChangeStore>(), ctx2.Get<IPendingChangeStore>()).ShouldBeFalse();
        ReferenceEquals(ctx1.Get<ActiveItemResolver>(), ctx2.Get<ActiveItemResolver>()).ShouldBeFalse();
        ReferenceEquals(ctx1.Get<McpPendingChangeFlusher>(), ctx2.Get<McpPendingChangeFlusher>()).ShouldBeFalse();
        ReferenceEquals(ctx1.Get<ITrackingRepository>(), ctx2.Get<ITrackingRepository>()).ShouldBeFalse();
    }

    [Fact]
    public void Different_Contexts_Have_Independent_CacheStores()
    {
        var key1 = WriteWorkspace("orgA", "proj1");
        var key2 = WriteWorkspace("orgB", "proj2");
        using var factory = CreateFactory();

        var ctx1 = factory.GetOrCreate(key1);
        var ctx2 = factory.GetOrCreate(key2);

        ReferenceEquals(ctx1.Get<SqliteCacheStore>(), ctx2.Get<SqliteCacheStore>()).ShouldBeFalse();
    }

    // ── Unknown key ─────────────────────────────────────────────────

    [Fact]
    public void GetOrCreate_Unknown_Key_Throws_KeyNotFoundException()
    {
        WriteWorkspace("orgA", "proj1");
        using var factory = CreateFactory();

        Should.Throw<KeyNotFoundException>(() =>
            factory.GetOrCreate(new Connection("unknown", "missing")));
    }

    // ── Disposal ────────────────────────────────────────────────────

    [Fact]
    public void Dispose_Disposes_All_Created_Contexts()
    {
        var key1 = WriteWorkspace("orgA", "proj1");
        var key2 = WriteWorkspace("orgB", "proj2");
        var factory = CreateFactory();

        var ctx1 = factory.GetOrCreate(key1);
        var ctx2 = factory.GetOrCreate(key2);

        // Capture connections before disposal to verify they become unusable
        var conn1 = ctx1.Get<SqliteCacheStore>().GetConnection();
        var conn2 = ctx2.Get<SqliteCacheStore>().GetConnection();

        factory.Dispose();

        // After factory disposal, the underlying SQLite connections are closed.
        conn1.State.ShouldBe(System.Data.ConnectionState.Closed);
        conn2.State.ShouldBe(System.Data.ConnectionState.Closed);
    }

    [Fact]
    public void Dispose_Is_Idempotent()
    {
        var key = WriteWorkspace("orgA", "proj1");
        var factory = CreateFactory();
        factory.GetOrCreate(key);

        // Should not throw on double-dispose
        factory.Dispose();
        factory.Dispose();
    }

    [Fact]
    public void GetOrCreate_After_Dispose_Throws_ObjectDisposedException()
    {
        var key = WriteWorkspace("orgA", "proj1");
        var factory = CreateFactory();
        factory.Dispose();

        Should.Throw<ObjectDisposedException>(() => factory.GetOrCreate(key));
    }

    // ── ConnectionScope.Dispose ─────────────────────────────────────

    [Fact]
    public void Context_Dispose_Disposes_CacheStore()
    {
        var key = WriteWorkspace("orgA", "proj1");
        using var factory = CreateFactory();

        var context = factory.GetOrCreate(key);
        var conn = context.Get<SqliteCacheStore>().GetConnection();

        context.Dispose();

        conn.State.ShouldBe(System.Data.ConnectionState.Closed);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private Connection WriteWorkspace(string org, string project, string? team = null,
        string? gitProject = null, string? gitRepository = null)
    {
        var dir = Path.Combine(_tempDir, org, project);
        Directory.CreateDirectory(dir);

        var config = new TwigConfiguration
        {
            Organization = org,
            Project = project,
            Team = team ?? string.Empty,
            Git = new GitConfig
            {
                Project = gitProject ?? string.Empty,
                Repository = gitRepository ?? string.Empty,
            },
        };

        var json = JsonSerializer.Serialize(config, TwigJsonContext.Default.TwigConfiguration);
        File.WriteAllText(Path.Combine(dir, "config"), json);

        return new Connection(org, project);
    }

    private ConnectionScopeFactory CreateFactory()
    {
        var registry = new ConnectionRegistry(_tempDir);
        var factory = new ConnectionScopeFactory(registry, _httpClient, _authProvider, _tempDir);
        _factories.Add(factory);
        return factory;
    }
}
