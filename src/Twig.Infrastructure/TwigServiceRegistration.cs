using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Mutation;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services.Mutation;
using Twig.Infrastructure.Telemetry;

namespace Twig.Infrastructure;

/// <summary>
/// Registers core Twig services into an <see cref="IServiceCollection"/>.
/// Shared by both CLI and TUI entry points to eliminate duplicate DI setup.
/// </summary>
/// <remarks>
/// <b>Public visibility</b>: This class MUST be <c>public</c> because
/// <c>InternalsVisibleTo</c> in <c>Twig.Infrastructure.csproj</c> does NOT
/// include <c>Twig.Tui</c>. An <c>internal</c> class would cause a compilation
/// error in the TUI project.
/// <para/>
/// <b>LegacyDbMigrator exclusion</b>: <c>LegacyDbMigrator</c> is an
/// <c>internal static class</c> in the CLI project and cannot be referenced
/// from Infrastructure. CLI <c>Program.cs</c> must call
/// <c>LegacyDbMigrator.MigrateIfNeeded()</c> directly after consuming
/// <see cref="AddConnectionServices"/>.
/// </remarks>
public static class TwigServiceRegistration
{
    /// <summary>
    /// Registers core Twig services: configuration, paths, SQLite persistence,
    /// repositories, stores, and the process configuration provider.
    /// Uses factory-based <c>AddSingleton(sp => ...)</c> for AOT robustness.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="preloadedConfig">Optional pre-loaded config to avoid redundant file I/O.
    /// When null, config is loaded from disk on first resolution.</param>
    /// <param name="twigDir">Optional explicit path to the <c>.twig</c> directory.
    /// When null, falls back to <c>Path.Combine(Directory.GetCurrentDirectory(), ".twig")</c>.</param>
    /// <param name="startDir">Optional CWD override. Stored on <see cref="TwigPaths.StartDir"/>
    /// so commands like <c>twig init</c> can create workspaces relative to the invocation
    /// directory rather than the walked-up <c>.twig</c> ancestor.</param>
    public static IServiceCollection AddConnectionServices(
        this IServiceCollection services,
        TwigConfiguration? preloadedConfig = null,
        string? twigDir = null,
        string? startDir = null)
    {
        var resolvedTwigDir = twigDir ?? Path.Combine(Directory.GetCurrentDirectory(), ".twig");

        // Configuration — use pre-loaded instance if available, otherwise load on first resolution
        if (preloadedConfig is not null)
        {
            services.AddSingleton(preloadedConfig);
        }
        else
        {
            services.AddSingleton(_ =>
            {
                // AB#3296: split-aware load. Probe TwigPaths needed for path derivation;
                // the real TwigPaths comes from BuildPaths after config is loaded.
                var probePaths = new TwigPaths(resolvedTwigDir, Path.Combine(resolvedTwigDir, "config"), Path.Combine(resolvedTwigDir, "twig.db"));
                return TwigConfiguration.LoadSplit(probePaths);
            });
        }

        // Multi-context DB path: .twig/{org}/{project}/twig.db
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<TwigConfiguration>();
            return TwigPaths.BuildPaths(resolvedTwigDir, config, startDir);
        });

        // SQLite persistence — registered unconditionally. SqliteCacheStore is
        // created lazily (on first resolution) for any discovered workspace.
        services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<TwigPaths>();
            if (!WorkspaceDiscovery.IsWorkspaceDirectory(paths.TwigDir))
                throw new WorkspaceNotFoundException();

            Directory.CreateDirectory(Path.GetDirectoryName(paths.DbPath)!);
            return new SqliteCacheStore($"Data Source={paths.DbPath}");
        });

        services.AddSingleton<IWorkItemRepository>(sp => new SqliteWorkItemRepository(sp.GetRequiredService<SqliteCacheStore>(), new WorkItemMapper()));
        services.AddSingleton<IContextStore>(sp => new SqliteContextStore(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<INavigationHistoryStore>(sp => new SqliteNavigationHistoryStore(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<IPendingChangeStore>(sp => new SqlitePendingChangeStore(sp.GetRequiredService<SqliteCacheStore>()));
        // IPendingChangeReader aliases the same SqlitePendingChangeStore instance so
        // read-only consumers (plan preview, MCP status) resolve without the mutating
        // surface. Cast is safe: SqlitePendingChangeStore implements both.
        services.AddSingleton<IPendingChangeReader>(sp =>
            (IPendingChangeReader)sp.GetRequiredService<IPendingChangeStore>());
        services.AddSingleton<IUnitOfWork>(sp => new SqliteUnitOfWork(sp.GetRequiredService<SqliteCacheStore>()));

        // Domain services
        services.AddSingleton<SeedFactory>();
        services.AddSingleton<SeedDiscardOrchestrator>();
        services.AddSingleton<ISprintHierarchyBuilder, SprintHierarchyBuilder>();
        services.AddSingleton<IProcessTypeStore>(sp => new SqliteProcessTypeStore(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<IProcessConfigurationProvider>(sp => new DynamicProcessConfigProvider(sp.GetRequiredService<IProcessTypeStore>()));
        services.AddSingleton<IFieldDefinitionStore>(sp => new SqliteFieldDefinitionStore(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<IWorkItemLinkRepository>(sp => new SqliteWorkItemLinkRepository(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<ISeedLinkRepository>(sp => new SqliteSeedLinkRepository(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<IStagedIdentityRegistry>(sp => new SqliteStagedIdentityRegistry(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<IPublishIdMapRepository>(sp => new SqlitePublishIdMapRepository(sp.GetRequiredService<SqliteCacheStore>(), sp.GetRequiredService<IStagedIdentityRegistry>()));
        services.AddSingleton<IPublishIntentRepository>(sp => new SqlitePublishIntentRepository(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<IPlanJournalRepository>(sp => new SqlitePlanJournalRepository(sp.GetRequiredService<SqliteCacheStore>()));
        services.AddSingleton<ITrackingRepository>(sp => new FileTrackingRepository(sp.GetRequiredService<TwigPaths>()));
        // ITrackingService is registered in AddConnectionDomainServices, AFTER the Bench pin
        // reader/writer it now depends on (ADO #146).

        // Seed publish rules provider — loads .twig/seed-rules.json or falls back to defaults.
        services.AddSingleton<ISeedPublishRulesProvider>(sp =>
        {
            var paths = sp.GetRequiredService<TwigPaths>();
            return new FileSeedPublishRulesProvider(paths.TwigDir);
        });

        // Global profile store — best-effort file-backed storage for process profiles.
        services.AddSingleton<IGlobalProfileStore, GlobalProfileStore>();

        // Telemetry client — no-op when TWIG_TELEMETRY_ENDPOINT is unset.
        services.AddSingleton<ITelemetryClient, TelemetryClient>();

        // Prompt state writer — writes .twig/prompt.json atomically after mutating commands.
        services.AddSingleton<IPromptStateWriter>(sp => new PromptStateWriter(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetRequiredService<TwigPaths>(),
            sp.GetRequiredService<IProcessTypeStore>(),
            sp.GetService<Twig.Domain.Services.Attachment.PrimaryScopeAttachmentService>()));

        // AB#738 primary-scope attachment (deep module). Every surface (CLI status
        // projection, MCP status tool, prompt writer) resolves the same service
        // instance so attach/switch/detach behavior cannot diverge across surfaces.
        services.AddSingleton<Twig.Domain.Interfaces.IPrimaryScopeAttachmentStore>(sp =>
            new Twig.Infrastructure.Persistence.WorktreeLocalAttachmentStore(
                sp.GetRequiredService<TwigPaths>(),
                sp.GetRequiredService<TwigConfiguration>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.TryAddSingleton<Twig.Domain.Services.Attachment.IPrimaryScopePolicySource>(sp =>
            new Twig.Infrastructure.Config.CheckedInProfilePolicySource(
                sp.GetRequiredService<TwigConfiguration>()));
        services.TryAddSingleton<Twig.Domain.Interfaces.IPrimaryScopeTypeEligibility>(sp =>
            new Twig.Infrastructure.Config.ConfigPrimaryScopeTypeEligibility(
                sp.GetRequiredService<Twig.Domain.Services.Attachment.IPrimaryScopePolicySource>()));
        services.AddSingleton<Twig.Domain.Services.Attachment.IWorktreeFingerprintProvider>(sp =>
            new Twig.Infrastructure.Persistence.WorktreeFingerprintProvider(
                sp.GetRequiredService<TwigPaths>(),
                sp.GetRequiredService<TwigConfiguration>()));
        services.AddSingleton<Twig.Domain.Services.Attachment.IPrimaryScopeUrlBuilder>(sp =>
            new Twig.Infrastructure.Persistence.ConfiguredPrimaryScopeUrlBuilder(
                sp.GetRequiredService<TwigConfiguration>()));
        services.AddSingleton<Twig.Domain.Interfaces.ISystemWorktreeRegistry>(sp =>
            new Twig.Infrastructure.Persistence.SqliteSystemWorktreeRegistry(
                Path.Combine(Twig.Infrastructure.Config.WorkspaceDiscovery.GlobalHomePath, "system.db"),
                sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddSingleton<Twig.Domain.Services.Attachment.PrimaryScopeAttachmentService>(sp =>
            new Twig.Domain.Services.Attachment.PrimaryScopeAttachmentService(
                sp.GetRequiredService<Twig.Domain.Interfaces.IPrimaryScopeAttachmentStore>(),
                sp.GetRequiredService<Twig.Domain.Interfaces.IPrimaryScopeTypeEligibility>(),
                sp.GetRequiredService<Twig.Domain.Interfaces.IWorkItemRepository>(),
                sp.GetRequiredService<Twig.Domain.Interfaces.ISystemWorktreeRegistry>(),
                sp.GetRequiredService<Twig.Domain.Services.Attachment.IWorktreeFingerprintProvider>(),
                sp.GetRequiredService<Twig.Domain.Services.Attachment.IPrimaryScopeUrlBuilder>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddSingleton<Twig.Domain.Interfaces.IPrimaryScopeAttachmentService>(sp =>
            sp.GetRequiredService<Twig.Domain.Services.Attachment.PrimaryScopeAttachmentService>());
        services.AddSingleton<Twig.Domain.Interfaces.IAttachmentStatusProjection>(sp =>
            new Twig.Domain.Services.Attachment.AttachmentStatusProjectionAdapter(
                sp.GetRequiredService<Twig.Domain.Services.Attachment.PrimaryScopeAttachmentService>()));
        services.AddSingleton<Twig.Domain.Interfaces.IManagedWorktreeInitializer>(sp =>
            new Twig.Infrastructure.Persistence.ManagedWorktreeInitializer(
                sp.GetRequiredService<Twig.Domain.Interfaces.IPrimaryScopeAttachmentStore>(),
                sp.GetRequiredService<Twig.Domain.Interfaces.ISystemWorktreeRegistry>(),
                sp.GetRequiredService<Twig.Domain.Services.Attachment.IWorktreeFingerprintProvider>()));

        AddConnectionDomainServices(services);

        return services;
    }

    /// <summary>
    /// Registers the surface-neutral domain services — resolvers, sync coordination, and the
    /// mutation workflows — that every surface (CLI, TUI, MCP) needs identically.
    /// </summary>
    /// <remarks>
    /// Exposed separately from <see cref="AddConnectionServices"/> so a caller that already owns
    /// persistence and network registrations — a test fixture over substitutes, or a surface
    /// composing its own container — can add exactly this block without duplicating the list.
    /// Callers of <see cref="AddConnectionServices"/> get it automatically and must not call both.
    /// <para/>
    /// These previously lived in the CLI-only <c>CommandServiceModule</c>, which MCP cannot
    /// reference, forcing <c>WorkspaceContextFactory</c> to hand-mirror the wiring. A mirror has
    /// no compiler forcing the two copies to agree, which is the mechanism behind
    /// PolyphonyRequiem/twig#269 and #270 (wayfinder 0016).
    /// <para/>
    /// Membership test: a service belongs here when it does <b>not</b> name a surface. Services
    /// that do — <c>HintEngine</c>, <c>IEditorLauncher</c>/<c>IConsoleInput</c>,
    /// <c>CommandContext</c>, <c>StatusFieldConfigReader</c>, <c>IPendingChangeFlusher</c> (which
    /// takes <c>IConsoleInput</c> and an output-format string) — stay in the CLI.
    /// <para/>
    /// Several of these depend on <see cref="IAdoWorkItemService"/> and
    /// <see cref="IIterationService"/> from
    /// <see cref="NetworkServiceModule.AddTwigNetworkServices"/>. That is safe because every
    /// registration here is factory-based and therefore resolved lazily: a surface that never
    /// resolves them (the TUI) never needs the network module.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddConnectionDomainServices(this IServiceCollection services)
    {
        // The clock, as a service. Registered here rather than defaulted inside each
        // consumer so a test can freeze it — which is what makes the process description's
        // byte-stability assertable at all: with a live clock the capture timestamp moves and
        // there is no way to tell a real ordering defect from the one permitted variance.
        services.AddSingleton(TimeProvider.System);

        // The process description assembler — the ONE seam both the CLI and the agent
        // surface assemble through, so exactly one document format exists rather than two
        // that drift. Surface-neutral, so it lives here rather than in either surface's
        // module.
        services.AddSingleton(sp => new ProcessDescriptionAssembler(
            sp.GetRequiredService<IProcessDescriptionSource>())
        {
            // Read from the fetch layer so a version recorded in a document header cannot
            // drift from the version the fetch actually used.
            RouteVersions = AdoProcessDescriptionSource.RouteVersions,
        });

        // Mutation providers. Previously registered in BOTH this module and the CLI's
        // CommandServiceModule — harmless last-wins on the same concrete type, but the tell
        // that the boundary sat where nobody could see both sides at once (wayfinder 0016).
        services.AddSingleton<SeedMutationProvider>();
        services.AddSingleton<AdoMutationProvider>();

        services.AddSingleton<ActiveItemResolver>(sp => new ActiveItemResolver(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>()));

        services.AddSingleton<ProtectedCacheWriter>(sp => new ProtectedCacheWriter(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IPendingChangeStore>()));

        // DD-13 + #1614: SyncCoordinatorFactory holds ReadOnly (longer TTL) and ReadWrite (shorter TTL)
        // tiers. Accepts int primitives to avoid Domain → Infrastructure circular reference.
        services.AddSingleton<SyncCoordinatorFactory>(sp =>
        {
            var display = sp.GetRequiredService<TwigConfiguration>().Display;
            return new SyncCoordinatorFactory(
                sp.GetRequiredService<IWorkItemRepository>(),
                sp.GetRequiredService<IAdoWorkItemService>(),
                sp.GetRequiredService<ProtectedCacheWriter>(),
                sp.GetRequiredService<IPendingChangeStore>(),
                sp.GetRequiredService<IWorkItemLinkRepository>(),
                display.CacheStaleMinutesReadOnly,
                display.CacheStaleMinutes);
        });

        // Backward compat — direct SyncCoordinator consumers resolve to pair.ReadWrite
        services.AddSingleton(sp => sp.GetRequiredService<SyncCoordinatorFactory>().ReadWrite);

        // ADO #144: the Bench, and the local iteration calendar that answers its sprint rule
        // without a network call. Registered here, beside WorkingSetService, because that is the
        // consumer — a registration in the other module resolves for the CLI and leaves the MCP
        // surface unable to build the same service.
        //
        // TryAdd, not Add: this module is composed on top of fixtures that substitute their own
        // stores. A plain Add is last-wins and would override a substitute with a SQLite-backed
        // implementation demanding a real cache store the fixture never registered.
        services.TryAddSingleton<IBenchRepository>(sp => new SqliteBenchRepository(sp.GetRequiredService<SqliteCacheStore>()));
        services.TryAddSingleton<IIterationCalendar>(sp => new SqliteIterationCalendar(sp.GetRequiredService<SqliteCacheStore>()));
        services.TryAddSingleton<BenchEvaluator>(sp => new BenchEvaluator(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IIterationCalendar>(),
            sp.GetRequiredService<IPendingChangeStore>()));

        // ADO #145/#146: the one answer to "what does a fresh default Bench hold", shared by the
        // view and by the pin workflow so the read and write paths cannot disagree. Since #146 it
        // is the sprint rule alone — seeding from the tracking file would resurrect the second pin
        // store the wipe removed.
        services.TryAddSingleton<DefaultBenchSelectors>(sp => new DefaultBenchSelectors(
            sp.GetRequiredService<TwigConfiguration>().User.DisplayName));

        // ADO #149: ONE answer to "which Bench am I standing on". Shared by the view, the pin
        // workflow and the Bench workflow — a second copy is how one surface gets left reading the
        // default after a switch, showing the wrong arrangement with nothing to fail.
        services.TryAddSingleton<CurrentBenchResolver>(sp => new CurrentBenchResolver(
            sp.GetRequiredService<IBenchRepository>(),
            sp.GetRequiredService<DefaultBenchSelectors>()));

        // ADO #145: pinning and unpinning act on the current Bench. Registered in the SHARED
        // domain-services module because both the CLI and the MCP surface route through it —
        // registering it beside one adapter would leave the other unable to build it.
        services.TryAddSingleton<PinWorkflow>(sp => new PinWorkflow(
            sp.GetRequiredService<IBenchRepository>(),
            sp.GetRequiredService<DefaultBenchSelectors>(),
            sp.GetRequiredService<CurrentBenchResolver>()));

        // ADO #146: the Bench is the ONLY pin store. One object is both the reader and the writer
        // so the two cannot end up on different stores — which is the failure the tracking file
        // caused, and one the parity baseline could not see because it covers the view, not sync.
        services.TryAddSingleton<BenchPinReader>(sp => new BenchPinReader(
            sp.GetRequiredService<IBenchRepository>(),
            sp.GetRequiredService<CurrentBenchResolver>()));
        services.TryAddSingleton<IPinReader>(sp => sp.GetRequiredService<BenchPinReader>());
        services.TryAddSingleton<IPinWriter>(sp => sp.GetRequiredService<BenchPinReader>());

        // Tracking now reads and writes pins through the Bench. The tracking REPOSITORY survives
        // for exclusions only, which are deliberately out of the Bench entirely.
        services.TryAddSingleton<ITrackingService>(sp => new TrackingService(
            sp.GetRequiredService<ITrackingRepository>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IProcessTypeStore>(),
            sp.GetService<IPinReader>(),
            sp.GetService<IPinWriter>()));

        // ADO #148/#149: creating, listing and switching Benches. Same seam, same module, same
        // reason — both surfaces route through this workflow, so registering it beside one adapter
        // would leave the other unable to build it.
        services.TryAddSingleton<BenchWorkflow>(sp => new BenchWorkflow(
            sp.GetRequiredService<IBenchRepository>(),
            sp.GetRequiredService<DefaultBenchSelectors>(),
            sp.GetRequiredService<CurrentBenchResolver>()));

        // DD-02: WorkingSetService accepts string? userDisplayName primitive (same pattern)
        services.AddSingleton<WorkingSetService>(sp => new WorkingSetService(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IIterationService>(),
            sp.GetRequiredService<TwigConfiguration>().User.DisplayName,
            sp.GetRequiredService<IBenchRepository>(),
            sp.GetRequiredService<BenchEvaluator>()));

        // EPIC-003: Seed publish orchestrator
        services.AddSingleton<BacklogOrderer>(sp => new BacklogOrderer(
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IFieldDefinitionStore>()));
        services.AddSingleton<SeedPublishOrchestrator>(sp => new SeedPublishOrchestrator(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<ISeedLinkRepository>(),
            sp.GetRequiredService<IWorkItemLinkRepository>(),
            sp.GetRequiredService<IPublishIdMapRepository>(),
            sp.GetRequiredService<ISeedPublishRulesProvider>(),
            sp.GetRequiredService<IUnitOfWork>(),
            sp.GetRequiredService<BacklogOrderer>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IPublishIntentRepository>()));
        services.AddSingleton<SeedLinkRepair>(sp => new SeedLinkRepair(
            sp.GetRequiredService<ISeedLinkRepository>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IPublishIdMapRepository>()));

        // EPIC-002: Domain orchestration services
        services.AddSingleton<ParentStatePropagationService>(sp => new ParentStatePropagationService(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IProcessConfigurationProvider>(),
            sp.GetRequiredService<ProtectedCacheWriter>()));

        // Mutation workflows — extracted orchestration shared by CLI commands and MCP tools.
        services.AddSingleton<StateTransitionWorkflow>(sp => new StateTransitionWorkflow(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IProcessConfigurationProvider>(),
            sp.GetService<ParentStatePropagationService>(),
            sp.GetService<IPromptStateWriter>(),
            sp.GetService<IProcessRuleProvider>()));

        services.AddSingleton<FieldUpdateWorkflow>(sp => new FieldUpdateWorkflow(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetService<IPromptStateWriter>()));

        services.AddSingleton<NoteWorkflow>(sp => new NoteWorkflow(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetService<IPromptStateWriter>()));

        services.AddSingleton<DiscardWorkflow>(sp => new DiscardWorkflow(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetService<IPromptStateWriter>()));

        services.AddSingleton<DeleteWorkflow>(sp => new DeleteWorkflow(
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IWorkItemLinkRepository>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetService<IPromptStateWriter>()));

        services.AddSingleton<PatchWorkflow>(sp => new PatchWorkflow(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetService<IPromptStateWriter>()));

        services.AddSingleton<RefreshOrchestrator>(sp => new RefreshOrchestrator(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<ProtectedCacheWriter>(),
            sp.GetRequiredService<WorkingSetService>(),
            sp.GetRequiredService<SyncCoordinatorFactory>(),
            sp.GetRequiredService<IIterationService>(),
            sp.GetService<ITrackingService>(),
            sp.GetService<IIterationCalendar>()));

        // Context change extension — additively hydrates parent chain + downstream graph
        services.AddSingleton<ContextChangeService>(sp => new ContextChangeService(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<SyncCoordinator>(),
            sp.GetRequiredService<ProtectedCacheWriter>(),
            sp.GetService<IWorkItemLinkRepository>()));

        // Resolves sprint expressions against ADO iterations — needed by every surface.
        services.AddSingleton<SprintIterationResolver>();

        // Cache-first / ADO-fallback work item reads, shared by every surface.
        services.AddSingleton<WorkItemFetcher>(sp => new WorkItemFetcher(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>()));

        // Shared plan-lifecycle service (twig plan native, wayfinder 0016). CLI + MCP +
        // future TUI all route through this ONE service so validation, preview, apply,
        // status and seed-descriptor semantics cannot drift between surfaces.
        services.AddSingleton<Twig.Infrastructure.Plan.PlanDocumentParser>();
        services.AddSingleton<Twig.Domain.Interfaces.IPlanLifecycleService>(sp =>
            new Twig.Infrastructure.Plan.PlanLifecycleService(
                sp.GetRequiredService<Twig.Infrastructure.Plan.PlanDocumentParser>(),
                sp.GetRequiredService<IPlanJournalRepository>(),
                sp.GetRequiredService<IPendingChangeReader>(),
                sp.GetRequiredService<IFieldDefinitionStore>(),
                sp.GetRequiredService<IAdoWorkItemService>(),
                sp.GetRequiredService<IRevisionBoundAdoWorkItemService>(),
                sp.GetRequiredService<SeedPublishOrchestrator>(),
                sp.GetRequiredService<IWorkItemRepository>(),
                sp.GetRequiredService<ISeedLinkRepository>(),
                sp.GetRequiredService<IStagedIdentityRegistry>(),
                sp.GetRequiredService<IPublishIdMapRepository>(),
                sp.GetRequiredService<IPublishIntentRepository>(),
                sp.GetRequiredService<Twig.Infrastructure.Config.TwigConfiguration>(),
                sp.GetRequiredService<Twig.Infrastructure.Config.TwigPaths>(),
                sp.GetRequiredService<TimeProvider>(),
                // Runtime process-rule gate (AB#673). Optional in the object graph — if the
                // network module has not registered a rule provider the gate no-ops and the
                // executor's strict-CAS remains the sole enforcement, as before.
                sp.GetService<Twig.Domain.Interfaces.IProcessRuleProvider>()));

        return services;
    }

    /// <summary>
    /// Registers the full Twig infrastructure stack — both core services
    /// (config, paths, SQLite persistence, repositories, process configuration,
    /// telemetry, prompt state) and network services (auth, HTTP, ADO REST
    /// clients, iteration service) — into the supplied
    /// <see cref="IServiceCollection"/>.
    /// </summary>
    /// <remarks>
    /// This is the supported public composition root for external consumers.
    /// In-repo entry points (CLI, MCP, TUI) call
    /// <see cref="AddConnectionServices"/> and
    /// <see cref="NetworkServiceModule.AddTwigNetworkServices"/> separately
    /// because they perform git auto-detection between the two phases; that
    /// split is an in-repo concern and is not the recommended external API.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="config">Pre-loaded configuration. Used for org/project/team
    /// (network registrations) and passed through as the registered singleton
    /// (core registrations).</param>
    /// <param name="twigDir">Optional explicit path to the <c>.twig</c>
    /// directory. When null, falls back to
    /// <c>Path.Combine(Directory.GetCurrentDirectory(), ".twig")</c>.</param>
    /// <param name="startDir">Optional CWD override stored on
    /// <see cref="TwigPaths.StartDir"/>.</param>
    /// <param name="resolvedGitProject">Git project resolved after
    /// auto-detection (may differ from <c>config.Project</c>). When null or
    /// whitespace, no <c>IAdoGitService</c> is registered.</param>
    /// <param name="resolvedRepository">Git repository resolved after
    /// auto-detection. Optional — when null, repository-by-id lookups return
    /// null but repository-by-name lookups still work.</param>
    public static IServiceCollection AddTwigInfrastructure(
        this IServiceCollection services,
        TwigConfiguration config,
        string? twigDir = null,
        string? startDir = null,
        string? resolvedGitProject = null,
        string? resolvedRepository = null)
    {
        services.AddConnectionServices(config, twigDir, startDir);
        services.AddTwigNetworkServices(config, resolvedGitProject, resolvedRepository);
        return services;
    }
}
