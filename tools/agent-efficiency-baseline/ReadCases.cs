using System.Text.Json;
using System.Text.Json.Serialization;
using NSubstitute;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;

namespace Twig.AgentEfficiencyBaseline;

/// <summary>
/// Runs the AB#878 offline read-side baseline: each case exercises a real command
/// (<see cref="ShowCommand"/>, <see cref="NewCommand"/>, <see cref="ProcessCommand"/>) against
/// NSubstitute-driven fixtures, captures the sanitized invocation + observed outcome as JSON
/// strings on <see cref="Observation"/>, and classifies the result from what was measured.
/// A recorded defect never re-classifies itself as a pass; a fix flips the classification
/// on the next run without editing this file.
/// </summary>
internal static class ReadCases
{
    public static async Task<IReadOnlyList<Observation>> RunAsync()
    {
        return
        [
            await ShowUncachedIdWithRefreshAsync(),
            await ShowBatchMixedFoundMissingAsync(),
            await ShowExplicitFoundAsync(),
            await ShowExplicitFoundAsync(selected: true),
            await NewEmptyCatalogRejectsValidRefAsync(),
            await NewIncompleteCatalogRejectsValidRefAsync(),
            await NewWarmCatalogAcceptsKnownRefAsync(),
            await NewWarmCatalogRejectsUnknownRefAsync(),
            await ProcessListBroadAsync(),
            await ProcessTypeDetailAsync(),
        ];
    }

    // ── Case 1: Show — explicit id, cache miss, refresh requested ────────────
    //
    // Wayfinder read: with --refresh on a valid work item that ADO has, a user
    // expects Twig to fetch the item and render it. Today ShowCommand's cache
    // gate short-circuits on cache miss BEFORE consulting the refresh flag,
    // so an uncached id fails with exit=1 even when refresh=true and the
    // remote service knows the item.
    private static async Task<Observation> ShowUncachedIdWithRefreshAsync()
    {
        const int missingFromCache = 707;
        var remoteItem = new WorkItemBuilder(missingFromCache, "Sprint retro follow-up")
            .AsType(WorkItemType.Task)
            .WithAreaPath("Prototype")
            .WithIterationPath("Prototype\\Sprint 3")
            .Build();

        using var reads = BuildReadHarness();
        WorkItem? cached = null;
        reads.WorkItemRepo.GetByIdAsync(missingFromCache, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(cached));
        reads.WorkItemRepo.SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                cached = call.Arg<WorkItem>();
                return Task.CompletedTask;
            });
        reads.AdoService.FetchAsync(missingFromCache, Arg.Any<CancellationToken>()).Returns(remoteItem);

        var stderr = new StringWriter();
        var cmd = reads.NewShowCommand(stderr);

        var (exit, stdout) = await CaptureStdoutAsync(() => cmd.ExecuteAsync(missingFromCache, outputFormat: "json", refresh: true));

        var fetchCalls = reads.AdoService.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAdoWorkItemService.FetchAsync));
        var saves = reads.WorkItemRepo.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IWorkItemRepository.SaveAsync));
        var contextWrites = reads.ContextStore.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IContextStore.SetActiveWorkItemIdAsync));

        var input = new InvocationEvidence(
            Command: "show",
            Scenario: "uncached-id-with-refresh",
            OutputFormat: "json",
            Refresh: true,
            WorkItemId: missingFromCache,
            WorkItemType: null,
            RequestedFields: null,
            KnownCatalogFields: null,
            BatchIds: null,
            CacheHasItem: false,
            RemoteHasItem: true,
            Notes: "--refresh should let ShowCommand fetch through IAdoWorkItemService when the cache is empty.");

        var output = new OutcomeEvidence(
            ExitCode: exit,
            Stdout: stdout,
            Stderr: stderr.ToString(),
            FetchAsyncCalls: fetchCalls,
            CreateAsyncCalls: 0,
            SaveAsyncCalls: saves,
            SetActiveWorkItemIdCalls: contextWrites,
            WorkItemsInOutput: null,
            MissingIdsSurfaced: null,
            ContextInvariant: contextWrites == 0 ? "preserved" : "mutated",
            Notes: "Desired: exit=0, FetchAsyncCalls>=1, item rendered. Observed exit encodes the fix state.");

        // Classify from what actually happened. When the defect is fixed, the same
        // fixture will observe exit=0 + a real fetch and flip its own classification.
        var desiredMet = exit == 0 && fetchCalls >= 1 && stdout.Contains("Sprint retro follow-up", StringComparison.Ordinal);
        var classification = desiredMet ? "already fixed" : "current defect";

        return new Observation(
            Id: "read.show.uncached-refresh",
            Classification: classification,
            DesiredSatisfied: desiredMet,
            Input: JsonData.Serialize(input),
            Output: JsonData.Serialize(output),
            SafetyPassed: contextWrites == 0);
    }

    // ── Case 2: Show — batch of ids, some missing from cache ─────────────────
    //
    // A batch caller asks Twig for a fixed ID list. Silently dropping the
    // unfound rows loses the "did you know 909 was missing?" signal; the
    // desired shape surfaces those ids so the caller can retry them.
    private static async Task<Observation> ShowBatchMixedFoundMissingAsync()
    {
        using var reads = BuildReadHarness();
        var known = new WorkItemBuilder(101, "Investigation ticket").AsType(WorkItemType.Task).Build();
        reads.WorkItemRepo.GetByIdAsync(101, Arg.Any<CancellationToken>()).Returns(known);
        reads.WorkItemRepo.GetByIdAsync(909, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        reads.LinkRepo.GetLinksForSetAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());
        reads.LinkRepo.GetLinksVerifiedAtForSetAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, DateTimeOffset>());

        var stderr = new StringWriter();
        var cmd = reads.NewShowCommand(stderr);

        var (exit, stdout) = await CaptureStdoutAsync(() => cmd.ExecuteBatchAsync("101,909", "json"));

        using var payload = JsonDocument.Parse(stdout);
        var found = payload.RootElement.ValueKind == JsonValueKind.Array
            && payload.RootElement.EnumerateArray().Any(row => row.TryGetProperty("id", out var id) && id.GetInt32() == 101);
        var missingSurfaced = stdout.Contains("909", StringComparison.Ordinal);
        var fetchCalls = reads.AdoService.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAdoWorkItemService.FetchAsync));
        var saves = reads.WorkItemRepo.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IWorkItemRepository.SaveAsync));
        var contextWrites = reads.ContextStore.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IContextStore.SetActiveWorkItemIdAsync));

        var input = new InvocationEvidence(
            Command: "show-batch",
            Scenario: "batch-mixed-found-missing",
            OutputFormat: "json",
            Refresh: false,
            WorkItemId: null,
            WorkItemType: null,
            RequestedFields: null,
            KnownCatalogFields: null,
            BatchIds: "101,909",
            CacheHasItem: true,
            RemoteHasItem: false,
            Notes: "Caller supplied two IDs; 101 present, 909 absent from local cache.");

        var output = new OutcomeEvidence(
            ExitCode: exit,
            Stdout: stdout,
            Stderr: stderr.ToString(),
            FetchAsyncCalls: fetchCalls,
            CreateAsyncCalls: 0,
            SaveAsyncCalls: saves,
            SetActiveWorkItemIdCalls: contextWrites,
            WorkItemsInOutput: found ? 1 : 0,
            MissingIdsSurfaced: missingSurfaced ? new[] { 909 } : Array.Empty<int>(),
            ContextInvariant: contextWrites == 0 ? "preserved" : "mutated",
            Notes: "Desired: response distinguishes present rows from requested-but-absent IDs.");

        var desiredMet = found && missingSurfaced;
        var classification = desiredMet ? "already fixed" : "current defect";

        return new Observation(
            Id: "read.show.batch-mixed",
            Classification: classification,
            DesiredSatisfied: desiredMet,
            Input: JsonData.Serialize(input),
            Output: JsonData.Serialize(output),
            SafetyPassed: exit == 0 && found && fetchCalls == 0 && saves == 0 && contextWrites == 0);
    }

    // ── Case 3: Show — explicit id present in cache renders successfully ─────
    //
    // The straight-line happy path. If this fails the whole read surface is
    // broken; kept in the baseline so a regression is captured as a valid
    // supported case flipping SafetyPassed=false.
    private static async Task<Observation> ShowExplicitFoundAsync(bool selected = false)
    {
        using var reads = BuildReadHarness();
        var item = new WorkItemBuilder(42, "Design proposal review").AsType(WorkItemType.Task).Build();
        reads.WorkItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        reads.WorkItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        reads.LinkRepo.GetLinksAsync(42, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItemLink>());
        reads.ContextStore.GetActiveWorkItemIdAsync().Returns((int?)42);

        var stderr = new StringWriter();
        var cmd = reads.NewShowCommand(stderr);

        var (exit, stdout) = await CaptureStdoutAsync(() => cmd.ExecuteAsync(selected ? null : 42, outputFormat: "json", refresh: false));

        var contextWrites = reads.ContextStore.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IContextStore.SetActiveWorkItemIdAsync));

        var input = new InvocationEvidence(
            Command: "show",
            Scenario: selected ? "active-id-cache-hit" : "explicit-id-cache-hit",
            OutputFormat: "json",
            Refresh: false,
            WorkItemId: selected ? null : 42,
            WorkItemType: null,
            RequestedFields: null,
            KnownCatalogFields: null,
            BatchIds: null,
            CacheHasItem: true,
            RemoteHasItem: true,
            Notes: null);

        var output = new OutcomeEvidence(
            ExitCode: exit,
            Stdout: stdout,
            Stderr: stderr.ToString(),
            FetchAsyncCalls: 0,
            CreateAsyncCalls: 0,
            SaveAsyncCalls: 0,
            SetActiveWorkItemIdCalls: contextWrites,
            WorkItemsInOutput: 1,
            MissingIdsSurfaced: null,
            ContextInvariant: contextWrites == 0 ? "preserved" : "mutated",
            Notes: null);

        var desiredMet = exit == 0 && stdout.Contains("Design proposal review", StringComparison.Ordinal);

        return new Observation(
            Id: selected ? "read.show.selected-cache-hit" : "read.show.explicit-cache-hit",
            Classification: desiredMet ? "already fixed" : "current defect",
            DesiredSatisfied: desiredMet,
            Input: JsonData.Serialize(input),
            Output: JsonData.Serialize(output),
            SafetyPassed: desiredMet && contextWrites == 0);
    }

    // ── Case 4: New — empty field catalog rejects a valid Custom.* ref ───────
    //
    // Safety-critical guard: ADO silently drops unknown reference names on
    // create. When the local catalog is empty (freshly-cloned workspace before
    // 'twig refresh') NewCommand MUST refuse rather than emit a partial create
    // whose Custom.* value never lands. Expected refusal → SafetyPassed.
    private static async Task<Observation> NewEmptyCatalogRejectsValidRefAsync()
        => await NewFieldGuardAsync(
            id: "read.new.empty-catalog-rejects",
            scenario: "empty-catalog-rejects-valid-ref",
            knownFields: Array.Empty<FieldDefinition>(),
            requested: new[] { "Custom.SprintNote=carry over" },
            desiredExit: 1,
            requestedIsUnknownAtBoundary: true);

    // ── Case 5: New — partial catalog rejects a ref not in the catalog ───────
    //
    // Warm workspace, but the caller typed a Custom.* reference name the
    // catalog does not carry. Same expected refusal as the empty-catalog case.
    private static async Task<Observation> NewIncompleteCatalogRejectsValidRefAsync()
        => await NewFieldGuardAsync(
            id: "read.new.incomplete-catalog-rejects",
            scenario: "incomplete-catalog-rejects-uncatalogued-ref",
            knownFields:
            [
                new FieldDefinition("System.Title", "Title", "String", false),
                new FieldDefinition("System.Description", "Description", "HTML", false),
                new FieldDefinition("Custom.Priority", "Priority", "String", false),
            ],
            requested: new[] { "Custom.SprintNote=carry over" },
            desiredExit: 1,
            requestedIsUnknownAtBoundary: true);

    // ── Case 6: New — catalog contains the ref, create succeeds ──────────────
    //
    // Positive path. Verifies the guard is not so pessimistic that it blocks
    // real catalogued fields — a regression here would silently break every
    // create-with-fields caller.
    private static async Task<Observation> NewWarmCatalogAcceptsKnownRefAsync()
        => await NewFieldGuardAsync(
            id: "read.new.warm-catalog-accepts",
            scenario: "warm-catalog-accepts-known-ref",
            knownFields:
            [
                new FieldDefinition("System.Title", "Title", "String", false),
                new FieldDefinition("System.Description", "Description", "HTML", false),
                new FieldDefinition("Custom.Priority", "Priority", "String", false),
                new FieldDefinition("Custom.SprintNote", "Sprint Note", "String", false),
            ],
            requested: new[] { "Custom.SprintNote=carry over" },
            desiredExit: 0,
            requestedIsUnknownAtBoundary: false);

    // ── Case 7: New — catalog present, ref not in it, rejected ───────────────
    //
    // Explicit unknown-field guard on a warm workspace. The ref reads like a
    // typo of Custom.Priority; ADO would drop it silently, so Twig fails
    // locally with a diagnosable exit.
    private static async Task<Observation> NewWarmCatalogRejectsUnknownRefAsync()
        => await NewFieldGuardAsync(
            id: "read.new.warm-catalog-rejects-unknown",
            scenario: "warm-catalog-rejects-genuinely-unknown-ref",
            knownFields:
            [
                new FieldDefinition("System.Title", "Title", "String", false),
                new FieldDefinition("Custom.Priority", "Priority", "String", false),
            ],
            requested: new[] { "Custom.NotAField=x" },
            desiredExit: 1,
            requestedIsUnknownAtBoundary: true);

    private static async Task<Observation> NewFieldGuardAsync(
        string id, string scenario, IReadOnlyList<FieldDefinition> knownFields,
        IReadOnlyList<string> requested, int desiredExit, bool requestedIsUnknownAtBoundary)
    {
        var writes = BuildWriteHarness(knownFields);

        var stderrCapture = new StringWriter();
        var stdoutCapture = new StringWriter();
        var priorOut = Console.Out;
        var priorErr = Console.Error;
        int exit;
        try
        {
            Console.SetOut(stdoutCapture);
            Console.SetError(stderrCapture);
            exit = await writes.NewCmd.ExecuteAsync(
                title: "Investigation notes",
                type: "Task",
                fields: requested.ToArray());
        }
        finally
        {
            Console.SetOut(priorOut);
            Console.SetError(priorErr);
        }

        var createCalls = writes.AdoService.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAdoWorkItemService.CreateAsync));
        var saveCalls = writes.WorkItemRepo.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IWorkItemRepository.SaveAsync));
        var contextWrites = writes.ContextStore.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IContextStore.SetActiveWorkItemIdAsync));

        var input = new InvocationEvidence(
            Command: "new",
            Scenario: scenario,
            OutputFormat: "human",
            Refresh: null,
            WorkItemId: null,
            WorkItemType: "Task",
            RequestedFields: requested,
            KnownCatalogFields: knownFields.Select(f => f.ReferenceName).ToArray(),
            BatchIds: null,
            CacheHasItem: false,
            RemoteHasItem: false,
            Notes: requestedIsUnknownAtBoundary
                ? "Requested ref is not in the local field catalog; NewCommand must refuse before CreateAsync."
                : "Requested ref is a known cataloged field; NewCommand should create + persist.");

        var output = new OutcomeEvidence(
            ExitCode: exit,
            Stdout: stdoutCapture.ToString(),
            Stderr: stderrCapture.ToString(),
            FetchAsyncCalls: writes.AdoService.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAdoWorkItemService.FetchAsync)),
            CreateAsyncCalls: createCalls,
            SaveAsyncCalls: saveCalls,
            SetActiveWorkItemIdCalls: contextWrites,
            WorkItemsInOutput: null,
            MissingIdsSurfaced: null,
            ContextInvariant: contextWrites == 0 ? "preserved" : "mutated",
            Notes: requestedIsUnknownAtBoundary
                ? "Guard is offline: no CreateAsync/SaveAsync must occur, exit=1."
                : "Positive case: exit=0 and one CreateAsync + one SaveAsync.");

        bool safetyPassed;
        bool desiredMet;
        string classification;

        if (requestedIsUnknownAtBoundary)
        {
            // Safety-critical: never punch through to the network on unknown ref.
            safetyPassed = createCalls == 0 && saveCalls == 0 && contextWrites == 0;
            var metadataIncomplete = id is "read.new.empty-catalog-rejects" or "read.new.incomplete-catalog-rejects";
            var diagnostics = stdoutCapture.ToString() + stderrCapture;
            // Safe refusal is required, but calling a valid server field "unknown"
            // is the separate metadata-readiness defect this baseline measures.
            desiredMet = exit == desiredExit && safetyPassed
                && (!metadataIncomplete || !diagnostics.Contains("Unknown field", StringComparison.OrdinalIgnoreCase));
            classification = desiredMet ? "expected refusal" : "current defect";
        }
        else
        {
            desiredMet = exit == desiredExit && createCalls == 1 && saveCalls == 1;
            safetyPassed = desiredMet && contextWrites == 0;
            classification = desiredMet ? "already fixed" : "current defect";
        }

        return new Observation(
            Id: id,
            Classification: classification,
            DesiredSatisfied: desiredMet,
            Input: JsonData.Serialize(input),
            Output: JsonData.Serialize(output),
            SafetyPassed: safetyPassed);
    }

    // ── Case 8: Process — broad list, all types rendered ─────────────────────
    private static async Task<Observation> ProcessListBroadAsync()
    {
        var (cmd, stderr, processTypeStore, _) = BuildProcessHarness();
        processTypeStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<ProcessTypeRecord>
        {
            new()
            {
                TypeName = "Task",
                States =
                [
                    new StateEntry("To Do", StateCategory.Proposed, null),
                    new StateEntry("Doing", StateCategory.InProgress, null),
                    new StateEntry("Done", StateCategory.Completed, null),
                ],
                ValidChildTypes = Array.Empty<string>(),
                CategoryReferenceNames = new[] { "Microsoft.RequirementCategory" },
            },
            new()
            {
                TypeName = "Investigation",
                States =
                [
                    new StateEntry("Open", StateCategory.Proposed, null),
                    new StateEntry("Closed", StateCategory.Completed, null),
                ],
                ValidChildTypes = Array.Empty<string>(),
                CategoryReferenceNames = new[] { "Microsoft.RequirementCategory" },
            },
        });

        var (exit, stdout) = await CaptureStdoutAsync(() => cmd.ExecuteAsync(typeName: null, outputFormat: "json"));

        var input = new InvocationEvidence(
            Command: "process",
            Scenario: "broad-list",
            OutputFormat: "json",
            Refresh: null,
            WorkItemId: null,
            WorkItemType: null,
            RequestedFields: null,
            KnownCatalogFields: null,
            BatchIds: null,
            CacheHasItem: true,
            RemoteHasItem: false,
            Notes: "Two fixture types with non-Hyperbright vocabulary (Task, Investigation).");

        var output = new OutcomeEvidence(
            ExitCode: exit,
            Stdout: stdout,
            Stderr: stderr.ToString(),
            FetchAsyncCalls: 0,
            CreateAsyncCalls: 0,
            SaveAsyncCalls: 0,
            SetActiveWorkItemIdCalls: 0,
            WorkItemsInOutput: null,
            MissingIdsSurfaced: null,
            ContextInvariant: "preserved",
            Notes: null);

        var desiredMet = exit == 0
            && stdout.Contains("\"typeName\": \"Task\"", StringComparison.Ordinal)
            && stdout.Contains("\"typeName\": \"Investigation\"", StringComparison.Ordinal);

        return new Observation(
            Id: "read.process.broad-list",
            Classification: desiredMet ? "already fixed" : "current defect",
            DesiredSatisfied: desiredMet,
            Input: JsonData.Serialize(input),
            Output: JsonData.Serialize(output),
            SafetyPassed: true);
    }

    // ── Case 9: Process <type> detail ────────────────────────────────────────
    private static async Task<Observation> ProcessTypeDetailAsync()
    {
        var (cmd, stderr, processTypeStore, fieldDefStore) = BuildProcessHarness();
        processTypeStore.GetByNameAsync("Investigation", Arg.Any<CancellationToken>()).Returns(new ProcessTypeRecord
        {
            TypeName = "Investigation",
            States =
            [
                new StateEntry("Open", StateCategory.Proposed, null),
                new StateEntry("Analyzing", StateCategory.InProgress, "007acc"),
                new StateEntry("Closed", StateCategory.Completed, "339933"),
            ],
            CategoryReferenceNames = new[] { "Microsoft.RequirementCategory" },
        });
        fieldDefStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<FieldDefinition>
        {
            new("System.Title", "Title", "String", false),
            new("System.State", "State", "String", true),
        });

        var (exit, stdout) = await CaptureStdoutAsync(() => cmd.ExecuteAsync(typeName: "Investigation", outputFormat: "json"));

        var input = new InvocationEvidence(
            Command: "process",
            Scenario: "type-detail-explicit",
            OutputFormat: "json",
            Refresh: null,
            WorkItemId: null,
            WorkItemType: "Investigation",
            RequestedFields: null,
            KnownCatalogFields: new[] { "System.Title", "System.State" },
            BatchIds: null,
            CacheHasItem: true,
            RemoteHasItem: false,
            Notes: "Explicit type detail with a three-state fixture (Open → Analyzing → Closed).");

        var output = new OutcomeEvidence(
            ExitCode: exit,
            Stdout: stdout,
            Stderr: stderr.ToString(),
            FetchAsyncCalls: 0,
            CreateAsyncCalls: 0,
            SaveAsyncCalls: 0,
            SetActiveWorkItemIdCalls: 0,
            WorkItemsInOutput: null,
            MissingIdsSurfaced: null,
            ContextInvariant: "preserved",
            Notes: null);

        var desiredMet = exit == 0
            && stdout.Contains("\"type\": \"Investigation\"", StringComparison.Ordinal)
            && stdout.Contains("\"name\": \"Analyzing\"", StringComparison.Ordinal);

        return new Observation(
            Id: "read.process.type-detail",
            Classification: desiredMet ? "already fixed" : "current defect",
            DesiredSatisfied: desiredMet,
            Input: JsonData.Serialize(input),
            Output: JsonData.Serialize(output),
            SafetyPassed: true);
    }


    // ── Harness assembly ─────────────────────────────────────────────────────

    private sealed class ReadHarness : IDisposable
    {
        public required string TempDirectory { get; init; }
        public void Dispose() => Directory.Delete(TempDirectory, recursive: true);
        public required IWorkItemRepository WorkItemRepo { get; init; }
        public required IWorkItemLinkRepository LinkRepo { get; init; }
        public required IAdoWorkItemService AdoService { get; init; }
        public required IContextStore ContextStore { get; init; }
        public required IPendingChangeStore PendingChangeStore { get; init; }
        public required SyncCoordinatorFactory SyncCoordinatorFactory { get; init; }
        public required OutputFormatterFactory FormatterFactory { get; init; }
        public required StatusFieldConfigReader StatusFieldReader { get; init; }
        public required ActiveItemResolver ActiveItemResolver { get; init; }
        public required WorkingSetService WorkingSetService { get; init; }
        public required IFieldDefinitionStore FieldDefinitionStore { get; init; }
        public required IProcessConfigurationProvider ProcessConfigurationProvider { get; init; }

        public ShowCommand NewShowCommand(TextWriter stderr)
        {
            var pipeline = new RenderingPipelineFactory(FormatterFactory, asyncRenderer: null!, isOutputRedirected: () => true);
            var ctx = new CommandContext(pipeline, FormatterFactory,
                new HintEngine(new DisplayConfig { Hints = false }),
                new TwigConfiguration(), Stderr: stderr);
            return new ShowCommand(
                ctx, WorkItemRepo, LinkRepo, SyncCoordinatorFactory, StatusFieldReader,
                fieldDefinitionStore: FieldDefinitionStore,
                processConfigProvider: ProcessConfigurationProvider,
                contextStore: ContextStore,
                activeItemResolver: ActiveItemResolver,
                pendingChangeStore: PendingChangeStore,
                workingSetService: WorkingSetService);
        }
    }

    private static ReadHarness BuildReadHarness()
    {
        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var linkRepo = Substitute.For<IWorkItemLinkRepository>();
        var adoService = Substitute.For<IAdoWorkItemService>();
        var contextStore = Substitute.For<IContextStore>();
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        pendingChangeStore.GetChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var iterationService = Substitute.For<IIterationService>();
        var fieldDefStore = Substitute.For<IFieldDefinitionStore>();
        fieldDefStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<FieldDefinition>());
        var processConfigProvider = Substitute.For<IProcessConfigurationProvider>();

        var protectedCacheWriter = new ProtectedCacheWriter(workItemRepo, pendingChangeStore);
        var syncFactory = new SyncCoordinatorFactory(workItemRepo, adoService, protectedCacheWriter,
            pendingChangeStore, linkRepo, 30, 30);
        var formatterFactory = new OutputFormatterFactory(new HumanOutputFormatter());
        var tempDir = Path.Combine(Path.GetTempPath(), "twig-baseline-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var statusFieldReader = new StatusFieldConfigReader(
            new TwigPaths(tempDir, Path.Combine(tempDir, "config"), Path.Combine(tempDir, "twig.db")));
        var activeResolver = new ActiveItemResolver(contextStore, workItemRepo, adoService);
        var workingSet = new WorkingSetService(contextStore, workItemRepo, pendingChangeStore, iterationService, null);

        return new ReadHarness
        {
            TempDirectory = tempDir,
            WorkItemRepo = workItemRepo,
            LinkRepo = linkRepo,
            AdoService = adoService,
            ContextStore = contextStore,
            PendingChangeStore = pendingChangeStore,
            SyncCoordinatorFactory = syncFactory,
            FormatterFactory = formatterFactory,
            StatusFieldReader = statusFieldReader,
            ActiveItemResolver = activeResolver,
            WorkingSetService = workingSet,
            FieldDefinitionStore = fieldDefStore,
            ProcessConfigurationProvider = processConfigProvider,
        };
    }

    private sealed class WriteHarness
    {
        public required IAdoWorkItemService AdoService { get; init; }
        public required IWorkItemRepository WorkItemRepo { get; init; }
        public required IContextStore ContextStore { get; init; }
        public required IFieldDefinitionStore FieldDefStore { get; init; }
        public required NewCommand NewCmd { get; init; }
    }

    private static WriteHarness BuildWriteHarness(IReadOnlyList<FieldDefinition> knownFields)
    {
        var adoService = Substitute.For<IAdoWorkItemService>();
        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var contextStore = Substitute.For<IContextStore>();
        var fieldDefStore = Substitute.For<IFieldDefinitionStore>();
        fieldDefStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(knownFields);
        fieldDefStore.GetByReferenceNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var name = callInfo.Arg<string>();
                return Task.FromResult<FieldDefinition?>(
                    knownFields.FirstOrDefault(f =>
                        string.Equals(f.ReferenceName, name, StringComparison.OrdinalIgnoreCase)));
            });
        var editorLauncher = Substitute.For<IEditorLauncher>();
        var stagedRegistry = Substitute.For<IStagedIdentityRegistry>();
        stagedRegistry.MintAsync(Arg.Any<CancellationToken>())
            .Returns(new StagedSeedIdentity(StagedIdentity.New(), StagedAlias.Below(0)));

        // Deterministic accept-any-title happy path when the guard lets things through.
        adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(1234);
        adoService.FetchAsync(1234, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var built = new WorkItemBuilder(1234, "Investigation notes")
                    .AsType(WorkItemType.Task)
                    .WithAreaPath("Prototype")
                    .WithIterationPath("Prototype\\Sprint 1")
                    .Build();
                return Task.FromResult(built);
            });

        var config = new TwigConfiguration
        {
            Project = "Prototype",
            Organization = "prototype-org",
            User = new UserConfig { DisplayName = "Baseline User" },
            Defaults = new DefaultsConfig
            {
                AreaPath = "Prototype",
                IterationPath = "Prototype\\Sprint 1",
            },
        };
        var cmd = new NewCommand(
            adoService, workItemRepo, contextStore, fieldDefStore, editorLauncher,
            new OutputFormatterFactory(new HumanOutputFormatter()),
            new HintEngine(new DisplayConfig { Hints = false }),
            config,
            new SeedFactory(),
            stagedRegistry,
            ReferenceProfileBuilder.UnpinnedSprintPolicy());

        return new WriteHarness
        {
            AdoService = adoService,
            WorkItemRepo = workItemRepo,
            ContextStore = contextStore,
            FieldDefStore = fieldDefStore,
            NewCmd = cmd,
        };
    }

    private static (ProcessCommand Cmd, StringWriter Stderr, IProcessTypeStore ProcessTypeStore, IFieldDefinitionStore FieldDefStore) BuildProcessHarness()
    {
        var contextStore = Substitute.For<IContextStore>();
        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var adoService = Substitute.For<IAdoWorkItemService>();
        var processTypeStore = Substitute.For<IProcessTypeStore>();
        var fieldDefStore = Substitute.For<IFieldDefinitionStore>();
        var stderr = new StringWriter();

        var activeResolver = new ActiveItemResolver(contextStore, workItemRepo, adoService);
        var cmd = new ProcessCommand(
            activeResolver,
            processTypeStore,
            fieldDefStore,
            new OutputFormatterFactory(new HumanOutputFormatter()),
            new RendererFactory(),
            stderr: stderr);
        return (cmd, stderr, processTypeStore, fieldDefStore);
    }

    // ── Shared JSON evidence shapes ──────────────────────────────────────────

    private static async Task<(int Exit, string Stdout)> CaptureStdoutAsync(Func<Task<int>> action)
    {
        var priorOut = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            var exit = await action();
            return (exit, writer.ToString());
        }
        finally
        {
            Console.SetOut(priorOut);
        }
    }
}

/// <summary>
/// Sanitized invocation record captured verbatim on <see cref="Observation.Input"/>.
/// Only field reference names and generic path segments are recorded — no ADO tenant
/// identifiers, user names, work-item titles beyond the fixture strings, or repository
/// metadata leaks in here.
/// </summary>
internal sealed record InvocationEvidence(
    string Command,
    string Scenario,
    string OutputFormat,
    bool? Refresh,
    int? WorkItemId,
    string? WorkItemType,
    IReadOnlyList<string>? RequestedFields,
    IReadOnlyList<string>? KnownCatalogFields,
    string? BatchIds,
    bool CacheHasItem,
    bool RemoteHasItem,
    string? Notes);

/// <summary>
/// Observed effect of running a case. Captures the exact exit code, the byte-accurate
/// stdout/stderr the command emitted, and the number of fake-service calls each
/// mutating surface received. <see cref="ContextInvariant"/> encodes whether the
/// active-item context was touched — reads must leave it alone.
/// </summary>
internal sealed record OutcomeEvidence(
    int ExitCode,
    string Stdout,
    string Stderr,
    int FetchAsyncCalls,
    int CreateAsyncCalls,
    int SaveAsyncCalls,
    int SetActiveWorkItemIdCalls,
    int? WorkItemsInOutput,
    IReadOnlyList<int>? MissingIdsSurfaced,
    string ContextInvariant,
    string? Notes);

/// <summary>
/// Registers the <see cref="ReadCases"/> DTOs with the source-generated context so
/// <see cref="JsonData"/> can round-trip them without reflection. Combines with the
/// declaration in <c>Program.cs</c>; C# merges the partial attribute lists.
/// </summary>
[JsonSerializable(typeof(InvocationEvidence))]
[JsonSerializable(typeof(OutcomeEvidence))]
internal partial class BaselineJsonContext;
