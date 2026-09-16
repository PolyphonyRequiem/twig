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
            await ShowUncachedIdWithRefreshFailurePropagatesAsync(),
            await ShowBatchMixedFoundMissingAsync(),
            await ShowExplicitFoundAsync(),
            await ShowExplicitFoundAsync(selected: true),
            await NewEmptyCatalogRejectsValidRefAsync(),
            await NewIncompleteCatalogRejectsValidRefAsync(),
            await NewWarmCatalogAcceptsKnownRefAsync(),
            await NewWarmCatalogRejectsUnknownRefAsync(),
            await MetadataPredicateNegativeControlAsync(),
            await ProcessListBroadAsync(),
            await ProcessTypeDetailAsync(),
        ];
    }

    // ── Case 1: Show — explicit id, cache miss, refresh requested ────────────
    //
    // Wayfinder read: with --refresh on a valid work item that ADO has, a user
    // expects Twig to fetch the item and render it. Since AB#879 the cold
    // explicit-refresh path calls IAdoWorkItemService.FetchWithLinksAsync
    // (there is no fallback FetchAsync-root), and the observation binds to
    // that seam so an accidental regression to a root-only fetch surfaces as
    // FetchWithLinksAsyncCalls == 0.
    private static async Task<Observation> ShowUncachedIdWithRefreshAsync()
    {
        const int missingFromCache = 707;
        var remoteItem = new WorkItemBuilder(missingFromCache, "Sprint retro follow-up")
            .AsType(WorkItemType.Task)
            .WithAreaPath("Prototype")
            .WithIterationPath("Prototype\\Sprint 3")
            .Build();
        var verifiedAt = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

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
        reads.WorkItemRepo.GetChildrenAsync(missingFromCache, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        reads.LinkRepo.GetLinksAsync(missingFromCache, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());
        reads.LinkRepo.GetLinksVerifiedAtAsync(missingFromCache, Arg.Any<CancellationToken>())
            .Returns((DateTimeOffset?)verifiedAt);
        reads.AdoService.FetchWithLinksAsync(missingFromCache, Arg.Any<CancellationToken>())
            .Returns((remoteItem, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));

        var stderr = new StringWriter();
        var cmd = reads.NewShowCommand(stderr);

        var (exit, stdout) = await CaptureStdoutAsync(() => cmd.ExecuteAsync(missingFromCache, outputFormat: "json", refresh: true));

        var fetchWithLinksCalls = reads.AdoService.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IAdoWorkItemService.FetchWithLinksAsync));
        var fetchAsyncCalls = reads.AdoService.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IAdoWorkItemService.FetchAsync));
        var saveCalls = reads.WorkItemRepo.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IWorkItemRepository.SaveAsync));
        var contextWrites = reads.ContextStore.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IContextStore.SetActiveWorkItemIdAsync));
        var (parsedId, parsedTitle, hasVerifiedLinks) = ParseShowJson(stdout);

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
            Notes: "--refresh must reach IAdoWorkItemService.FetchWithLinksAsync (post-AB#879); a root-only FetchAsync fallback is a regression.");

        var output = new OutcomeEvidence(
            ExitCode: exit,
            Stdout: stdout,
            Stderr: stderr.ToString(),
            FetchAsyncCalls: fetchAsyncCalls,
            CreateAsyncCalls: 0,
            SaveAsyncCalls: saveCalls,
            SetActiveWorkItemIdCalls: contextWrites,
            WorkItemsInOutput: parsedId is null ? null : 1,
            MissingIdsSurfaced: null,
            ContextInvariant: contextWrites == 0 ? "preserved" : "mutated",
            Notes: $"FetchWithLinksAsyncCalls={fetchWithLinksCalls}; parsed id={parsedId?.ToString() ?? "null"}; parsed title present={parsedTitle}; verified-links stamp visible={hasVerifiedLinks}.");

        // Post-AB#879 positive predicate: exit 0, FetchWithLinksAsync observed,
        // rendered payload carries the id and title verbatim (not a cache-only
        // fake shape), and the link-verified-at stamp landed. FetchAsync on the
        // root is now a regression signal and MUST stay at zero.
        var desiredMet = exit == 0
            && fetchWithLinksCalls >= 1
            && fetchAsyncCalls == 0
            && parsedId == missingFromCache
            && parsedTitle
            && hasVerifiedLinks;
        var classification = desiredMet ? "already fixed" : "current defect";

        return new Observation(
            Id: "read.show.uncached-refresh",
            Classification: classification,
            DesiredSatisfied: desiredMet,
            Input: JsonData.Serialize(input),
            Output: JsonData.Serialize(output),
            SafetyPassed: contextWrites == 0 && fetchAsyncCalls == 0);
    }

    // ── Case 1b: Show — cold explicit refresh, root fetch fails (AB#879) ─────
    //
    // The parallel machine-format guarantee: a FetchWithLinks failure on the
    // root MUST NOT be papered over with a cache-only fake success. The
    // command emits a format-aware error and exits 1; no SaveAsync fires; no
    // renderable payload appears on stdout. This is the "no fake cache
    // success" pair to Case 1.
    private static async Task<Observation> ShowUncachedIdWithRefreshFailurePropagatesAsync()
    {
        const int missingFromCache = 4242;

        using var reads = BuildReadHarness();
        reads.WorkItemRepo.GetByIdAsync(missingFromCache, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);
        reads.AdoService.FetchWithLinksAsync(missingFromCache, Arg.Any<CancellationToken>())
            .Returns<(WorkItem Item, IReadOnlyList<WorkItemLink> Links)>(_ => throw new InvalidOperationException("simulated ADO 500"));

        var stderr = new StringWriter();
        var cmd = reads.NewShowCommand(stderr);

        var (exit, stdout) = await CaptureStdoutAsync(() => cmd.ExecuteAsync(missingFromCache, outputFormat: "json", refresh: true));

        var fetchWithLinksCalls = reads.AdoService.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IAdoWorkItemService.FetchWithLinksAsync));
        var saveCalls = reads.WorkItemRepo.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IWorkItemRepository.SaveAsync));
        var contextWrites = reads.ContextStore.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IContextStore.SetActiveWorkItemIdAsync));
        var stderrText = stderr.ToString();
        var mentionsFailure = stderrText.Contains("Refresh failed", StringComparison.Ordinal)
            && stderrText.Contains($"#{missingFromCache}", StringComparison.Ordinal);
        var stdoutIsMachineEmpty = string.IsNullOrWhiteSpace(stdout) || stdout.Trim() == "{}" || stdout.Trim() == "null";

        var input = new InvocationEvidence(
            Command: "show",
            Scenario: "uncached-id-refresh-fetch-failure",
            OutputFormat: "json",
            Refresh: true,
            WorkItemId: missingFromCache,
            WorkItemType: null,
            RequestedFields: null,
            KnownCatalogFields: null,
            BatchIds: null,
            CacheHasItem: false,
            RemoteHasItem: false,
            Notes: "Simulated FetchWithLinksAsync exception must surface as format-aware exit 1, never a fake cache success.");

        var output = new OutcomeEvidence(
            ExitCode: exit,
            Stdout: stdout,
            Stderr: stderrText,
            FetchAsyncCalls: 0,
            CreateAsyncCalls: 0,
            SaveAsyncCalls: saveCalls,
            SetActiveWorkItemIdCalls: contextWrites,
            WorkItemsInOutput: 0,
            MissingIdsSurfaced: null,
            ContextInvariant: contextWrites == 0 ? "preserved" : "mutated",
            Notes: $"FetchWithLinksAsyncCalls={fetchWithLinksCalls}; stderr mentions '#id Refresh failed'={mentionsFailure}; stdout empty-for-machine={stdoutIsMachineEmpty}.");

        var desiredMet = exit == 1
            && fetchWithLinksCalls >= 1
            && saveCalls == 0
            && mentionsFailure
            && stdoutIsMachineEmpty;
        var classification = desiredMet ? "already fixed" : "current defect";

        return new Observation(
            Id: "read.show.uncached-refresh-failure",
            Classification: classification,
            DesiredSatisfied: desiredMet,
            Input: JsonData.Serialize(input),
            Output: JsonData.Serialize(output),
            SafetyPassed: contextWrites == 0 && saveCalls == 0);
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
        // AB#880 preserves the successful full-output array and discloses cache misses
        // as structured errors on stderr, with a nonzero exit. Both channels are evidence.
        var missingSurfaced = false;
        if (!string.IsNullOrWhiteSpace(stderr.ToString()))
        {
            using var errorPayload = JsonDocument.Parse(stderr.ToString());
            missingSurfaced = errorPayload.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString()!.Contains("#909", StringComparison.Ordinal);
        }
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

        var desiredMet = exit == 1 && found && missingSurfaced;
        var classification = desiredMet ? "already fixed" : "current defect";

        return new Observation(
            Id: "read.show.batch-mixed",
            Classification: classification,
            DesiredSatisfied: desiredMet,
            Input: JsonData.Serialize(input),
            Output: JsonData.Serialize(output),
            SafetyPassed: exit == 1 && found && missingSurfaced && fetchCalls == 0 && saves == 0 && contextWrites == 0);
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

        // Measure the substitute surfaces rather than reporting a constant.
        // Review §2 flagged this envelope's hard-coded zeros as a future
        // false-reporting risk; the fixture now surfaces the same numbers the
        // batch case already interrogates, so a regression that starts
        // spending fetch/create/save calls on the cache-hit path is visible.
        var fetchWithLinksCalls = reads.AdoService.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAdoWorkItemService.FetchWithLinksAsync));
        var fetchAsyncCalls = reads.AdoService.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAdoWorkItemService.FetchAsync));
        var createCalls = reads.AdoService.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAdoWorkItemService.CreateAsync));
        var saveCalls = reads.WorkItemRepo.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IWorkItemRepository.SaveAsync));
        var contextWrites = reads.ContextStore.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IContextStore.SetActiveWorkItemIdAsync));
        var (parsedId, parsedTitle, _) = ParseShowJson(stdout);

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
            FetchAsyncCalls: fetchAsyncCalls + fetchWithLinksCalls,
            CreateAsyncCalls: createCalls,
            SaveAsyncCalls: saveCalls,
            SetActiveWorkItemIdCalls: contextWrites,
            WorkItemsInOutput: parsedId is null ? null : 1,
            MissingIdsSurfaced: null,
            ContextInvariant: contextWrites == 0 ? "preserved" : "mutated",
            Notes: $"Observed via NSubstitute: FetchAsync={fetchAsyncCalls}, FetchWithLinksAsync={fetchWithLinksCalls}, CreateAsync={createCalls}, SaveAsync={saveCalls}. WorkItemsInOutput parsed from JSON payload (parsed id={parsedId?.ToString() ?? "null"}, title present={parsedTitle}).");

        // Cache-hit MUST NOT reach ADO at all — a positive count here is a
        // regression, not a happy path.
        var networkTouched = fetchAsyncCalls > 0 || fetchWithLinksCalls > 0 || createCalls > 0;
        var desiredMet = exit == 0
            && parsedId == 42
            && parsedTitle
            && !networkTouched;

        return new Observation(
            Id: selected ? "read.show.selected-cache-hit" : "read.show.explicit-cache-hit",
            Classification: desiredMet ? "already fixed" : "current defect",
            DesiredSatisfied: desiredMet,
            Input: JsonData.Serialize(input),
            Output: JsonData.Serialize(output),
            SafetyPassed: desiredMet && contextWrites == 0 && !networkTouched);
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
        var writes = BuildWriteHarness(knownFields, authoritativeCatalog: id == "read.new.warm-catalog-rejects-unknown");

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
            // Safety-critical: never punch through to the network on an unknown ref.
            safetyPassed = createCalls == 0 && saveCalls == 0 && contextWrites == 0;
            var diagnostics = stdoutCapture.ToString() + stderrCapture;
            var metadataIncomplete = id is "read.new.empty-catalog-rejects" or "read.new.incomplete-catalog-rejects";

            // Semantic diagnosis, not the absence of a bad phrase (AB#879 review §1):
            //  * empty/incomplete catalog → "Metadata not ready" + "twig process --refresh"
            //  * targeted metadata sync failed → "Metadata refresh failed" (+ original reason)
            //  * catalog present, ref genuinely absent → "Unknown field reference name(s)"
            // A cold path that emits only "Unknown field" (or nothing meaningful) is
            // NOT a fix — it is still the AB#879 defect, and this predicate refuses it.
            var metadataNotReady = IsMetadataNotReadyDiagnostic(diagnostics);
            var metadataRefreshFailed = IsMetadataRefreshFailedDiagnostic(diagnostics);
            var genuineUnknownField = IsGenuineUnknownFieldDiagnostic(diagnostics);

            if (metadataIncomplete)
            {
                // Either the targeted sync ran and the catalog is still empty,
                // or the sync itself failed with an actionable reason. Both are
                // valid semantic outcomes on the cold path; neither may claim
                // the caller's ref is genuinely unknown.
                desiredMet = exit == desiredExit && safetyPassed
                    && (metadataNotReady || metadataRefreshFailed)
                    && !genuineUnknownField;
            }
            else
            {
                // Warm catalog, ref really is absent: retain the crisp
                // "Unknown field reference name(s)" diagnostic and refuse if
                // the cold-path phrasing leaked into a hot-path refusal.
                desiredMet = exit == desiredExit && safetyPassed
                    && genuineUnknownField
                    && !(metadataNotReady || metadataRefreshFailed);
            }
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

    // ── Semantic diagnostic predicates (AB#879) ─────────────────────────────
    //
    // The revised metadata contract (Metadata879):
    //   * empty-or-incomplete catalog with the requested ref/type still absent
    //     after a targeted metadata-only sync → stderr carries "Metadata not
    //     ready" AND "twig process --refresh" (the recovery is a *metadata*
    //     refresh, not a full workspace 'twig refresh' — that would pull work
    //     items and is the wrong actionable);
    //   * the targeted sync itself failed (auth/network) → stderr carries
    //     "Metadata refresh failed" plus the propagated exception text;
    //   * catalog is present, the ref genuinely does not exist → stderr keeps
    //     the existing "Unknown field reference name(s)" wording, and the
    //     cold-path "Metadata not ready" / "Metadata refresh failed" MUST NOT
    //     appear.
    // Everything is a case-sensitive substring check per Metadata879's contract.
    private const string MetadataNotReadyHeader = "Metadata not ready";
    private const string MetadataNotReadyRecoveryHint = "twig process --refresh";
    private const string MetadataRefreshFailedHeader = "Metadata refresh failed";
    private const string GenuineUnknownFieldHeader = "Unknown field reference name(s)";

    private static bool IsMetadataNotReadyDiagnostic(string diagnostics) =>
        diagnostics.Contains(MetadataNotReadyHeader, StringComparison.Ordinal)
        && diagnostics.Contains(MetadataNotReadyRecoveryHint, StringComparison.Ordinal);

    private static bool IsMetadataRefreshFailedDiagnostic(string diagnostics) =>
        diagnostics.Contains(MetadataRefreshFailedHeader, StringComparison.Ordinal);

    private static bool IsGenuineUnknownFieldDiagnostic(string diagnostics) =>
        diagnostics.Contains(GenuineUnknownFieldHeader, StringComparison.Ordinal);

    // Negative-control fixtures for the predicates above. These prove the
    // predicate rejects meaningless or half-formed diagnostics, so a future
    // "current defect" → "expected refusal" flip cannot be produced by an
    // empty stderr or an unrelated error message; the predicate itself is
    // held to the same evidence bar as the case that consumes it.
    private static readonly (string Label, string Text, bool ExpectMetadataNotReady, bool ExpectMetadataRefreshFailed, bool ExpectGenuineUnknownField)[]
        MetadataPredicateSamples =
        [
            ("empty-diagnostic", "", false, false, false),
            ("unrelated-network-error", "error: connection reset by peer", false, false, false),
            ("only-header-no-actionable", "Metadata not ready: field catalog is empty after a targeted refresh.", false, false, false),
            ("only-actionable-no-header", "Run twig process --refresh and retry.", false, false, false),
            ("legacy-unknown-only", "Unknown field: Custom.NotAField.", false, false, false),
            ("legacy-recovery-with-wrong-command", "Metadata not ready: catalog is empty. Run 'twig refresh' to populate.", false, false, false),
            ("full-not-ready", "Metadata not ready: field catalog is empty after a targeted refresh. Run 'twig process --refresh' to force a metadata-only sync.", true, false, false),
            ("refresh-failure-with-reason", "Metadata refresh failed: HTTP 401 Unauthorized while contacting the fields endpoint.", false, true, false),
            ("genuine-unknown-field", "Unknown field reference name(s): Custom.NotAField.", false, false, true),
        ];

    // ── Case 8: Process — broad list, all types rendered ─────────────────────
    private static async Task<Observation> ProcessListBroadAsync()
    {
        var harness = BuildProcessHarness();
        harness.ProcessTypeStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<ProcessTypeRecord>
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

        var (exit, stdout) = await CaptureStdoutAsync(() => harness.Cmd.ExecuteAsync(typeName: null, outputFormat: "json"));

        var fetchCalls = harness.AdoService.ReceivedCalls().Count(c => c.GetMethodInfo().Name is nameof(IAdoWorkItemService.FetchAsync) or nameof(IAdoWorkItemService.FetchWithLinksAsync));
        var createCalls = harness.AdoService.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAdoWorkItemService.CreateAsync));
        var saveCalls = harness.WorkItemRepo.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IWorkItemRepository.SaveAsync));
        var contextWrites = harness.ContextStore.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IContextStore.SetActiveWorkItemIdAsync));
        var typeCount = CountProcessTypes(stdout);

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
            Stderr: harness.Stderr.ToString(),
            FetchAsyncCalls: fetchCalls,
            CreateAsyncCalls: createCalls,
            SaveAsyncCalls: saveCalls,
            SetActiveWorkItemIdCalls: contextWrites,
            WorkItemsInOutput: typeCount,
            MissingIdsSurfaced: null,
            ContextInvariant: contextWrites == 0 ? "preserved" : "mutated",
            Notes: $"Observed via NSubstitute; typeCount parsed from JSON payload = {typeCount?.ToString() ?? "null"}. Process list is a metadata read; any ADO/save/context activity here is a regression.");

        var desiredMet = exit == 0
            && typeCount == 2
            && fetchCalls == 0
            && createCalls == 0
            && saveCalls == 0
            && contextWrites == 0
            && ProcessListContainsType(stdout, "Task")
            && ProcessListContainsType(stdout, "Investigation");

        return new Observation(
            Id: "read.process.broad-list",
            Classification: desiredMet ? "already fixed" : "current defect",
            DesiredSatisfied: desiredMet,
            Input: JsonData.Serialize(input),
            Output: JsonData.Serialize(output),
            SafetyPassed: exit == 0 && fetchCalls == 0 && createCalls == 0 && saveCalls == 0 && contextWrites == 0);
    }

    // ── Case 9: Process <type> detail ────────────────────────────────────────
    private static async Task<Observation> ProcessTypeDetailAsync()
    {
        var harness = BuildProcessHarness();
        var type = new ProcessTypeRecord
        {
            TypeName = "Investigation",
            States =
            [
                new StateEntry("Open", StateCategory.Proposed, null),
                new StateEntry("Analyzing", StateCategory.InProgress, "007acc"),
                new StateEntry("Closed", StateCategory.Completed, "339933"),
            ],
            CategoryReferenceNames = new[] { "Microsoft.RequirementCategory" },
        };
        harness.ProcessTypeStore.GetByNameAsync("Investigation", Arg.Any<CancellationToken>()).Returns(type);
        harness.ProcessTypeStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { type });
        harness.FieldDefStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<FieldDefinition>
        {
            new("System.Title", "Title", "String", false),
            new("System.State", "State", "String", true),
        });

        var (exit, stdout) = await CaptureStdoutAsync(() => harness.Cmd.ExecuteAsync(typeName: "Investigation", outputFormat: "json"));

        var fetchCalls = harness.AdoService.ReceivedCalls().Count(c => c.GetMethodInfo().Name is nameof(IAdoWorkItemService.FetchAsync) or nameof(IAdoWorkItemService.FetchWithLinksAsync));
        var createCalls = harness.AdoService.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAdoWorkItemService.CreateAsync));
        var saveCalls = harness.WorkItemRepo.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IWorkItemRepository.SaveAsync));
        var contextWrites = harness.ContextStore.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IContextStore.SetActiveWorkItemIdAsync));
        var (typeInPayload, analyzingPresent) = ParseProcessTypeDetail(stdout, expectedType: "Investigation", expectedState: "Analyzing");

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
            Stderr: harness.Stderr.ToString(),
            FetchAsyncCalls: fetchCalls,
            CreateAsyncCalls: createCalls,
            SaveAsyncCalls: saveCalls,
            SetActiveWorkItemIdCalls: contextWrites,
            WorkItemsInOutput: typeInPayload ? 1 : (int?)null,
            MissingIdsSurfaced: null,
            ContextInvariant: contextWrites == 0 ? "preserved" : "mutated",
            Notes: $"Observed via NSubstitute; typeInPayload={typeInPayload}, analyzingPresent={analyzingPresent}. Parsed semantically, not via indent-coupled substring.");

        var desiredMet = exit == 0
            && typeInPayload
            && analyzingPresent
            && fetchCalls == 0
            && createCalls == 0
            && saveCalls == 0
            && contextWrites == 0;

        return new Observation(
            Id: "read.process.type-detail",
            Classification: desiredMet ? "already fixed" : "current defect",
            DesiredSatisfied: desiredMet,
            Input: JsonData.Serialize(input),
            Output: JsonData.Serialize(output),
            SafetyPassed: exit == 0 && fetchCalls == 0 && createCalls == 0 && saveCalls == 0 && contextWrites == 0);
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
        workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

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

    private static WriteHarness BuildWriteHarness(IReadOnlyList<FieldDefinition> knownFields, bool authoritativeCatalog)
    {
        var adoService = Substitute.For<IAdoWorkItemService>();
        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var contextStore = Substitute.For<IContextStore>();
        var fieldDefStore = Substitute.For<IFieldDefinitionStore>();
        fieldDefStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(knownFields);
        var metadata = authoritativeCatalog ? Substitute.For<IIterationService>() : null;
        if (metadata is not null)
            metadata.GetFieldDefinitionsStrictAsync(Arg.Any<CancellationToken>()).Returns(knownFields);
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
            ReferenceProfileBuilder.UnpinnedSprintPolicy(), iterationService: metadata);

        return new WriteHarness
        {
            AdoService = adoService,
            WorkItemRepo = workItemRepo,
            ContextStore = contextStore,
            FieldDefStore = fieldDefStore,
            NewCmd = cmd,
        };
    }

    private sealed record ProcessHarness(
        ProcessCommand Cmd,
        StringWriter Stderr,
        IProcessTypeStore ProcessTypeStore,
        IFieldDefinitionStore FieldDefStore,
        IAdoWorkItemService AdoService,
        IWorkItemRepository WorkItemRepo,
        IContextStore ContextStore);

    private static ProcessHarness BuildProcessHarness()
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
        return new ProcessHarness(cmd, stderr, processTypeStore, fieldDefStore, adoService, workItemRepo, contextStore);
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

    // ── Semantic JSON parsers (AB#879) ───────────────────────────────────────
    //
    // The frozen assertions used indent-coupled substring probes on `show`
    // and `process` JSON. AB#879 rewrites the ShowCommand cold path and the
    // process presenter both may reformat their output; parsing semantically
    // keeps the acceptance bar bound to observable content, not spelling.
    private static (int? Id, bool TitlePresent, bool HasVerifiedLinks) ParseShowJson(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return (null, false, false);
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            // ShowCommand emits an object for a single item; batch is an array.
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0) return (null, false, false);
                root = root[0];
            }
            int? id = null;
            if (root.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var idValue)) id = idValue;
            var title = root.TryGetProperty("title", out var titleProp)
                && titleProp.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(titleProp.GetString());
            var hasVerifiedLinks = root.TryGetProperty("linksVerifiedAt", out var verifiedProp)
                && verifiedProp.ValueKind is JsonValueKind.String or JsonValueKind.Number;
            return (id, title, hasVerifiedLinks);
        }
        catch (JsonException)
        {
            return (null, false, false);
        }
    }

    private static int? CountProcessTypes(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array) return root.GetArrayLength();
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("types", out var typesProp)
                && typesProp.ValueKind == JsonValueKind.Array)
            {
                return typesProp.GetArrayLength();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ProcessListContainsType(string stdout, string typeName)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return false;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array) array = root;
            else if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("types", out var typesProp)
                && typesProp.ValueKind == JsonValueKind.Array) array = typesProp;
            else return false;

            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                foreach (var propName in new[] { "typeName", "type", "name" })
                {
                    if (element.TryGetProperty(propName, out var nameProp)
                        && nameProp.ValueKind == JsonValueKind.String
                        && string.Equals(nameProp.GetString(), typeName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static (bool TypeInPayload, bool StatePresent) ParseProcessTypeDetail(string stdout, string expectedType, string expectedState)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return (false, false);
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (false, false);

            var typeMatch = false;
            foreach (var propName in new[] { "type", "typeName", "name" })
            {
                if (root.TryGetProperty(propName, out var typeProp)
                    && typeProp.ValueKind == JsonValueKind.String
                    && string.Equals(typeProp.GetString(), expectedType, StringComparison.Ordinal))
                {
                    typeMatch = true;
                    break;
                }
            }

            var stateMatch = false;
            if (root.TryGetProperty("states", out var statesProp) && statesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var state in statesProp.EnumerateArray())
                {
                    if (state.ValueKind == JsonValueKind.Object
                        && state.TryGetProperty("name", out var nameProp)
                        && nameProp.ValueKind == JsonValueKind.String
                        && string.Equals(nameProp.GetString(), expectedState, StringComparison.Ordinal))
                    {
                        stateMatch = true;
                        break;
                    }
                }
            }
            return (typeMatch, stateMatch);
        }
        catch (JsonException)
        {
            return (false, false);
        }
    }

    // ── Negative-control observation for the metadata predicates ─────────────
    //
    // Explicit red-team check: replay every synthetic diagnostic in
    // MetadataPredicateSamples through the three predicates and verify each
    // sample is classified exactly as expected. The observation only passes
    // when all samples classify correctly, so an accidental predicate widening
    // (e.g. dropping the recovery-hint conjunct) reports SafetyPassed=false.
    private static Task<Observation> MetadataPredicateNegativeControlAsync()
    {
        var perSample = new List<MetadataPredicateSampleResult>();
        var allMatch = true;
        foreach (var sample in MetadataPredicateSamples)
        {
            var notReady = IsMetadataNotReadyDiagnostic(sample.Text);
            var refreshFailed = IsMetadataRefreshFailedDiagnostic(sample.Text);
            var genuineUnknown = IsGenuineUnknownFieldDiagnostic(sample.Text);
            var matches = notReady == sample.ExpectMetadataNotReady
                && refreshFailed == sample.ExpectMetadataRefreshFailed
                && genuineUnknown == sample.ExpectGenuineUnknownField;
            if (!matches) allMatch = false;
            perSample.Add(new MetadataPredicateSampleResult(
                Label: sample.Label,
                Text: sample.Text,
                ObservedMetadataNotReady: notReady,
                ObservedMetadataRefreshFailed: refreshFailed,
                ObservedGenuineUnknownField: genuineUnknown,
                ExpectMetadataNotReady: sample.ExpectMetadataNotReady,
                ExpectMetadataRefreshFailed: sample.ExpectMetadataRefreshFailed,
                ExpectGenuineUnknownField: sample.ExpectGenuineUnknownField,
                Matches: matches));
        }

        var input = new InvocationEvidence(
            Command: "meta",
            Scenario: "metadata-predicate-negative-control",
            OutputFormat: "json",
            Refresh: null,
            WorkItemId: null,
            WorkItemType: null,
            RequestedFields: null,
            KnownCatalogFields: null,
            BatchIds: null,
            CacheHasItem: false,
            RemoteHasItem: false,
            Notes: $"Replay of {MetadataPredicateSamples.Length} synthetic diagnostics against IsMetadataNotReadyDiagnostic / IsMetadataRefreshFailedDiagnostic / IsGenuineUnknownFieldDiagnostic. Includes empty stderr, unrelated network error, header-only, hint-only, and legacy-recovery-with-wrong-command samples. If any sample misclassifies, the metadata cases downstream cannot claim 'expected refusal' honestly.");

        var output = new OutcomeEvidence(
            ExitCode: allMatch ? 0 : 1,
            Stdout: JsonData.Serialize(perSample),
            Stderr: string.Empty,
            FetchAsyncCalls: 0,
            CreateAsyncCalls: 0,
            SaveAsyncCalls: 0,
            SetActiveWorkItemIdCalls: 0,
            WorkItemsInOutput: null,
            MissingIdsSurfaced: null,
            ContextInvariant: "preserved",
            Notes: allMatch
                ? "All samples classified as expected; predicates are not vacuously permissive."
                : "At least one sample misclassified. See stdout for per-sample decisions.");

        return Task.FromResult(new Observation(
            Id: "read.meta.predicate-negative-control",
            Classification: allMatch ? "already fixed" : "current defect",
            DesiredSatisfied: allMatch,
            Input: JsonData.Serialize(input),
            Output: JsonData.Serialize(output),
            SafetyPassed: allMatch));
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
/// Per-sample decision for the metadata-predicate negative control (AB#879).
/// Captured on <see cref="Observation.Output"/> so a broken predicate reports
/// exactly which sample it misclassified.
/// </summary>
internal sealed record MetadataPredicateSampleResult(
    string Label,
    string Text,
    bool ObservedMetadataNotReady,
    bool ObservedMetadataRefreshFailed,
    bool ObservedGenuineUnknownField,
    bool ExpectMetadataNotReady,
    bool ExpectMetadataRefreshFailed,
    bool ExpectGenuineUnknownField,
    bool Matches);

[JsonSerializable(typeof(InvocationEvidence))]
[JsonSerializable(typeof(OutcomeEvidence))]
[JsonSerializable(typeof(List<MetadataPredicateSampleResult>))]
internal partial class BaselineJsonContext;
