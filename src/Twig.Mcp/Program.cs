using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.DependencyInjection;
using Twig.Mcp;
using Twig.Mcp.Services;
using Twig.Mcp.Services.Batch;
using Twig.Mcp.Tools;

SQLitePCL.Batteries.Init();

// FR-7: Ambient-mode workspace guard — succeeds when any .twig/{org}/{project}/config
// exists, even without top-level .twig/config. Must run before host build to provide
// clear error messages when no workspaces are found.
var (isValid, guardError, discoveredTwigDir) = WorkspaceGuard.CheckWorkspaceAmbient(Directory.GetCurrentDirectory());
if (!isValid)
{
    Console.Error.WriteLine(guardError);
    return 1;
}

var twigRoot = discoveredTwigDir!;

// Workspace infrastructure — replaces single-workspace singleton registrations.
// WorkspaceRegistry scans .twig/{org}/{project}/config on disk (DD-5).
// WorkspaceContextFactory lazily creates per-workspace service bundles.
// WorkspaceResolver routes per-tool-call workspace selection.
var registry = new WorkspaceRegistry(twigRoot);

// Global singletons shared across all workspaces (auth is per-user, not per-workspace).
// Determine auth method from workspace configs — if any workspace uses PAT, use PAT;
// otherwise fall through to the MSAL-cache-first AzCli chain via the centralized factory.
var httpClient = NetworkServiceModule.CreateHttpClient();
var authMethod = registry.Workspaces
    .Select(key => registry.GetConfig(key).Auth.Method)
    .FirstOrDefault(m => string.Equals(m, "pat", StringComparison.OrdinalIgnoreCase))
    ?? "azcli";
var authProvider = AuthProviderFactory.Create(authMethod);

var factory = new WorkspaceContextFactory(registry, httpClient, authProvider, twigRoot);
var resolver = new WorkspaceResolver(registry, factory);

var builder = Host.CreateApplicationBuilder(args);

// NFR-3: All logging to stderr — stdout is reserved for MCP stdio transport.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Register workspace infrastructure as singletons
builder.Services.AddSingleton<IWorkspaceRegistry>(registry);
builder.Services.AddSingleton(resolver);

// Batch dispatch — interface enables BatchExecutionEngine to be tested in isolation (NFR-7).
builder.Services.AddSingleton<IToolDispatcher, ToolDispatcher>();

// Parent-process watchdog — self-terminates when the host process exits,
// preventing orphaned twig-mcp instances on VS Code reload or CLI exit.
builder.Services.AddHostedService<ParentProcessWatchdog>();

// MCP server — WithTools<T>() is the AOT-safe generic registration
// (not WithToolsFromAssembly which uses reflection-based discovery).
builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "twig-mcp", Version = GetVersion() };
    })
    .WithStdioServerTransport()
    .WithTools<ContextTools>()
    .WithTools<ReadTools>()
    .WithTools<MutationTools>()
    .WithTools<NavigationTools>()
    .WithTools<CreationTools>()
    .WithTools<WorkspaceTools>()
    .WithTools<BatchTools>();

await builder.Build().RunAsync();
return 0;

static string GetVersion()
{
    var version = typeof(WorkspaceResolver).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        is [System.Reflection.AssemblyInformationalVersionAttribute attr]
        ? attr.InformationalVersion
        : "0.0.0";
    var plusIndex = version.IndexOf('+');
    if (plusIndex >= 0) version = version[..plusIndex];
    return version;
}
