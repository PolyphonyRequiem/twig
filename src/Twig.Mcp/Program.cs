using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.DependencyInjection;
using Twig.Mcp;
using Twig.Mcp.Services;
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
var httpClient = NetworkServiceModule.CreateHttpClient();
var authProvider = CreateAuthProvider(registry);

var factory = new WorkspaceContextFactory(registry, httpClient, authProvider, twigRoot);
var resolver = new WorkspaceResolver(registry, factory);

var builder = Host.CreateApplicationBuilder(args);

// NFR-3: All logging to stderr — stdout is reserved for MCP stdio transport.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Register workspace infrastructure as singletons
builder.Services.AddSingleton<IWorkspaceRegistry>(registry);
builder.Services.AddSingleton(resolver);

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
    .WithTools<WorkspaceTools>();

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

static IAuthenticationProvider CreateAuthProvider(WorkspaceRegistry registry)
{
    // Auth method is per-user, not per-workspace. Check first workspace config
    // for auth method preference; default to AzCli when no workspaces have PAT configured.
    foreach (var key in registry.Workspaces)
    {
        var config = registry.GetConfig(key);
        if (string.Equals(config.Auth.Method, "pat", StringComparison.OrdinalIgnoreCase))
            return new PatAuthProvider();
    }

    return new AzCliAuthProvider();
}
