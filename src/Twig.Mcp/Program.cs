using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Infrastructure;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;

SQLitePCL.Batteries.Init();

// FR-11: Workspace guard — exit with clear error if .twig/config is missing.
// Must run before host build since TwigConfiguration.Load() silently returns
// defaults for missing files, which would produce confusing downstream errors.
var twigDir = Path.Combine(Directory.GetCurrentDirectory(), ".twig");
var configPath = Path.Combine(twigDir, "config");

if (!File.Exists(configPath))
{
    Console.Error.WriteLine("Twig workspace not initialized. Run 'twig init' first.");
    return 1;
}

var config = TwigConfiguration.Load(configPath);

var builder = Host.CreateApplicationBuilder(args);

// NFR-3: All logging to stderr — stdout is reserved for MCP stdio transport.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Shared service registration (Infrastructure layer)
builder.Services.AddTwigCoreServices(config);
builder.Services.AddTwigNetworkServices(config);

// Domain orchestration services (subset of CLI's CommandServiceModule —
// only services consumed by MCP tools; see DD-10 for exclusions).
// All registrations use factory-based sp => new ...() for AOT robustness.
// ActivatorUtilities reflection-based activation may be trimmed under PublishAot=true.
builder.Services.AddSingleton(sp => new ActiveItemResolver(
    sp.GetRequiredService<IContextStore>(),
    sp.GetRequiredService<IWorkItemRepository>(),
    sp.GetRequiredService<IAdoWorkItemService>()));

builder.Services.AddSingleton(sp => new ProtectedCacheWriter(
    sp.GetRequiredService<IWorkItemRepository>(),
    sp.GetRequiredService<IPendingChangeStore>()));

builder.Services.AddSingleton(sp => new SyncCoordinator(
    sp.GetRequiredService<IWorkItemRepository>(),
    sp.GetRequiredService<IAdoWorkItemService>(),
    sp.GetRequiredService<ProtectedCacheWriter>(),
    sp.GetRequiredService<IPendingChangeStore>(),
    sp.GetRequiredService<IWorkItemLinkRepository>(),
    sp.GetRequiredService<TwigConfiguration>().Display.CacheStaleMinutes));

builder.Services.AddSingleton(sp => new WorkingSetService(
    sp.GetRequiredService<IContextStore>(),
    sp.GetRequiredService<IWorkItemRepository>(),
    sp.GetRequiredService<IPendingChangeStore>(),
    sp.GetRequiredService<IIterationService>(),
    sp.GetRequiredService<TwigConfiguration>().User.DisplayName));

// DD-10: BacklogOrderer, SeedPublishOrchestrator, SeedReconcileOrchestrator,
// and FlowTransitionService are NOT registered — no MCP tool consumes them.
builder.Services.AddSingleton(sp => new RefreshOrchestrator(
    sp.GetRequiredService<IContextStore>(),
    sp.GetRequiredService<IWorkItemRepository>(),
    sp.GetRequiredService<IAdoWorkItemService>(),
    sp.GetRequiredService<IIterationService>(),
    sp.GetRequiredService<IPendingChangeStore>(),
    sp.GetRequiredService<ProtectedCacheWriter>(),
    sp.GetRequiredService<WorkingSetService>(),
    sp.GetRequiredService<SyncCoordinator>(),
    sp.GetRequiredService<IProcessTypeStore>(),
    sp.GetRequiredService<IFieldDefinitionStore>()));

builder.Services.AddSingleton(sp => new StatusOrchestrator(
    sp.GetRequiredService<IContextStore>(),
    sp.GetRequiredService<IWorkItemRepository>(),
    sp.GetRequiredService<IPendingChangeStore>(),
    sp.GetRequiredService<ActiveItemResolver>(),
    sp.GetRequiredService<WorkingSetService>(),
    sp.GetRequiredService<SyncCoordinator>()));

// MCP-specific services
builder.Services.AddSingleton(sp => new McpPendingChangeFlusher(
    sp.GetRequiredService<IWorkItemRepository>(),
    sp.GetRequiredService<IAdoWorkItemService>(),
    sp.GetRequiredService<IPendingChangeStore>()));

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
    .WithTools<MutationTools>();

await builder.Build().RunAsync();
return 0;

static string GetVersion()
{
    var version = typeof(McpPendingChangeFlusher).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        is [System.Reflection.AssemblyInformationalVersionAttribute attr]
        ? attr.InformationalVersion
        : "0.0.0";
    var plusIndex = version.IndexOf('+');
    if (plusIndex >= 0) version = version[..plusIndex];
    return version;
}
