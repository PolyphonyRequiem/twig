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

// Ambient-mode workspace guard uses shared split-aware discovery.
var (isValid, guardError, discoveredTwigDir) = WorkspaceGuard.CheckWorkspaceAmbient(Directory.GetCurrentDirectory());

// When no workspace is found, start the server anyway with zero workspaces.
// Tools will return informative errors when invoked. This prevents Copilot from
// marking the MCP as "failed" in sessions opened outside a twig project.
string? twigRoot = discoveredTwigDir;

// Workspace infrastructure — replaces single-workspace singleton registrations.
// WorkspaceRegistry loads split config and scans legacy per-workspace configs (DD-5).
// WorkspaceContextFactory lazily creates per-workspace service bundles.
// WorkspaceResolver routes per-tool-call workspace selection.
var launchCwd = Directory.GetCurrentDirectory();
var registry = new WorkspaceRegistry(twigRoot ?? Path.Combine(launchCwd, ".twig"), launchCwd);

// Global singletons shared across all workspaces (auth is per-user, not per-workspace).
// Determine auth method from workspace configs — if any workspace uses PAT, use PAT;
// otherwise fall through to the MSAL-cache-first AzCli chain via the centralized factory.
var httpClient = NetworkServiceModule.CreateHttpClient();
var authMethod = registry.Workspaces
    .Select(key => registry.GetConfig(key).Auth.Method)
    .FirstOrDefault(m => string.Equals(m, "pat", StringComparison.OrdinalIgnoreCase))
    ?? "azcli";
var authProvider = AuthProviderFactory.Create(authMethod);

var factory = new WorkspaceContextFactory(registry, httpClient, authProvider, twigRoot ?? Path.Combine(launchCwd, ".twig"));
var resolver = new WorkspaceResolver(registry, factory);

var builder = Host.CreateApplicationBuilder(args);

// NFR-3: All logging to stderr — stdout is reserved for MCP stdio transport.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Register workspace infrastructure as singletons
builder.Services.AddSingleton<IWorkspaceRegistry>(registry);
builder.Services.AddSingleton(resolver);

// Seed factory — singleton counter for consistent negative ID generation across MCP tool calls.
builder.Services.AddSingleton<Twig.Domain.Interfaces.ISeedIdCounter, Twig.Domain.Services.Seed.SeedIdCounter>();
builder.Services.AddSingleton<Twig.Domain.Services.Seed.SeedFactory>();

// Batch dispatch — interface enables BatchExecutionEngine to be tested in isolation (NFR-7).
builder.Services.AddSingleton<IToolDispatcher, ToolDispatcher>();

// Tool classes — registered as singletons so DI can resolve cross-tool dependencies
// (e.g., ReadTools depends on NavigationTools) and so ToolDispatcher can compose them.
// WithTools<T>() below registers MCP tool metadata for discovery but does NOT add the
// type to the service container; that's our responsibility.
builder.Services.AddSingleton<ContextTools>();
builder.Services.AddSingleton<ReadTools>();
builder.Services.AddSingleton<MutationTools>();
builder.Services.AddSingleton<NavigationTools>();
builder.Services.AddSingleton<CreationTools>();
builder.Services.AddSingleton<WorkspaceTools>();
builder.Services.AddSingleton<ProcessTools>();
builder.Services.AddSingleton<AdminTools>();
builder.Services.AddSingleton<TrackingTools>();
builder.Services.AddSingleton<BatchTools>();
builder.Services.AddSingleton<SeedTools>();

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
    .WithTools<ProcessTools>()
    .WithTools<AdminTools>()
    .WithTools<TrackingTools>()
    .WithTools<BatchTools>()
    .WithTools<SeedTools>();

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
