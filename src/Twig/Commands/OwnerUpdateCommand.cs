using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Twig.Core;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;

namespace Twig.Commands;

internal sealed record ReplacementReview(Guid PreviousReview, Guid PreviousAttempt, string PreviousDigest, string Digest,
    string PreviousCapture, string Capture, string PreviousRevision, string Revision,
    string PreviousSchema, string Schema, string? PreviousPolicyRevision, string? PolicyRevision);
internal sealed record UpdateReply(string Status, UpdateIdentity[] Matches, bool Ambiguous = false)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReplacementReview? Replacement { get; init; }
}
internal sealed record CanvasUpdateGuard(NativeSourceStamp Source, string? Baseline, Func<bool> Authorized);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdateReply))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class UpdateReplyJson : JsonSerializerContext;

internal sealed class OwnerUpdateCommand(Guid boot, string home, UserBenchStore actions, IServiceProvider? hostServices = null,
    CancellationToken stopping = default)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> routines = new();
    private readonly SemaphoreSlim acquisitions = new(8, 8);
    private readonly SemaphoreSlim localBatches = new(1, 1);
    private sealed class BatchLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
    private TaskCompletionSource admissionChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Task<int>> detached = new();

    private void WakeWaiters() => Interlocked.Exchange(ref admissionChanged,
        new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    internal Task DrainAsync() => Task.WhenAll(detached.Values);

    internal async Task<int> ExecuteAsync(string[] args, string directory, Func<Task<string>> readBody, Func<string,Task> acknowledgeBinding,
        TextWriter output, TextWriter errors, CancellationToken ct, CanvasUpdateGuard? guard = null)
    {
        var yielded = new TaskCompletionSource<UpdateReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var run = RunAsync(args, directory, readBody, acknowledgeBinding, stdout, stderr, yielded, ct, guard);
        if (await Task.WhenAny(run, yielded.Task) == yielded.Task)
        {
            var reply = await yielded.Task;
            var submission = reply.Matches.Single().Submission;
            detached[submission] = run;
            _ = FinishAsync();
            output.WriteLine(JsonSerializer.Serialize(reply, UpdateReplyJson.Default.UpdateReply));
            return 1;

            async Task FinishAsync()
            {
                try { await run; }
                finally { detached.TryRemove(new KeyValuePair<Guid, Task<int>>(submission, run)); }
            }
        }
        var code = await run;
        output.Write(stdout.ToString());
        errors.Write(stderr.ToString());
        return code;
    }

    private async Task<int> RunAsync(string[] args, string directory, Func<Task<string>> readBody, Func<string,Task> acknowledgeBinding,
        TextWriter output, TextWriter errors, TaskCompletionSource<UpdateReply> yielded, CancellationToken ct, CanvasUpdateGuard? guard)
    {
        UpdateIdentity? bound = null;
        var reading = false;
        try
        {
            // The qualification runtime selects its own workspace; neither a pipe frame nor
            // a CLI option can import an installed workspace/provider into this owner.
            var root = Path.Combine(Path.GetDirectoryName(home)!, "work");
            if (!OwnerFixtureSelection.Same(directory, root)) throw new InvalidDataException("Not the selected qualification workspace.");
            if (args is [.., "-o", "json"]) args = args[..^2];
            string item, mode;
            Guid review = default;
            Guid? predecessorReview = null;
            string? field = null, value = null;
            var comment = false;
            var create = false;
            switch (args)
            {
                case ["update", "--re-review", var predecessor, "--review", var successor, "--id", var id]:
                    item = id; predecessorReview = Guid.ParseExact(predecessor, "D"); review = Guid.ParseExact(successor, "D");
                    mode = "prepare"; break;
                case ["seed", "publish", "--local-id", var id, "--review", var rev, .. var tail] when tail is [] or ["--prepare"]:
                    create = true; item = id; review = Guid.ParseExact(rev, "D"); mode = tail.Length == 0 ? "submit" : "prepare"; break;
                case ["seed", "--find", "--local-id", var id]:
                    create = true; item = id; mode = "find"; break;
                case ["seed", var op, var rev, "--local-id", var id] when op is "--submit" or "--observe" or "--cancel":
                    create = true; item = id; mode = op[2..]; review = Guid.ParseExact(rev, "D"); break;
                case ["update", "--find", "--id", var id]: item = id; mode = "find"; break;
                case ["note", "--id", var id, "--text", var text, "--review", var rev, .. var tail] when tail is [] or ["--prepare"]:
                    item = id; value = text; comment = true; review = Guid.ParseExact(rev, "D"); mode = tail.Length == 0 ? "submit" : "prepare"; break;
                case ["update", var f, var v, "--id", var id, "--local"]:
                    item = id; field = f; value = v; mode = "local"; break;
                case ["update", var op, var rev, "--id", var id] when op is "--observe" or "--submit" or "--cancel":
                    item = id; mode = op[2..]; review = Guid.ParseExact(rev, "D"); break;
                case ["update", var f, var v, "--id", var id, "--review", var rev, .. var tail] when tail is [] or ["--prepare"]:
                    item = id; field = f; value = v; review = Guid.ParseExact(rev, "D"); mode = tail.Length == 0 ? "submit" : "prepare"; break;
                default: throw new InvalidDataException("Expected an explicit update, review and work item, or --find --id.");
            }
            if (item.Length is 0 or > 256 || mode is not ("find" or "local") && review == Guid.Empty)
                throw new InvalidDataException("An exact item and nonempty review UUID are required.");
            await acquisitions.WaitAsync(ct);
            reading = true;
            var twig = Path.Combine(root, ".twig");
            var paths = TwigPaths.BuildPaths(twig, new TwigConfiguration(), root);
            var config = await TwigConfiguration.LoadSplitAsync(paths, ct);
            var marker = Path.Combine(twig, CoreWorkspaceFactory.FileName);
            if (new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint)
                || new DirectoryInfo(twig).Attributes.HasFlag(FileAttributes.ReparsePoint)
                || new FileInfo(marker).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("The fixture workspace cannot redirect its authority.");
            using var pinned = File.Open(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = await JsonSerializer.DeserializeAsync(pinned, CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument, ct);
            if (!OwnerFixtureSelection.Same(document?.DatabasePath, Path.Combine(twig, "core.db"))
                || new FileInfo(document!.DatabasePath).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("The fixture requires its own physical Core store.");
            var services = new ServiceCollection();
            if (hostServices?.GetService<Func<IStore,IStore>>() is { } mirror) services.AddSingleton(mirror);
            var transport = hostServices?.GetService<CoreTransportOverride>();
            var credential = hostServices?.GetService<Twig.Infrastructure.GitHub.IGitHubTokenSource>();
            using var identityClient = transport is null ? null : new HttpClient(transport.CreateHandler());
            if (transport is not null) services.AddSingleton(transport);
            services.AddSingleton<Twig.Infrastructure.Auth.IConnectionBindingService>(_ =>
                new Twig.Infrastructure.Auth.ConnectionBindingService(home, patHttpClient: identityClient));
            if (credential is not null) services.AddSingleton(credential);
            services.AddCoreWorkspaceServices(config, paths, executionOwner: true);
            using var runtime = services.BuildServiceProvider();
            var factory = runtime.GetRequiredService<CoreWorkspaceFactory>();
            using var handle = Require(await factory.OpenAsync(ct));
            pinned.Dispose();
            Require(await factory.AdmitSourceAsync(handle, ct));
            var workspace = handle.Workspace;
            if (guard is not null && (handle.Source != guard.Source || !guard.Authorized()))
                throw new InvalidDataException("The authorized Canvas origin changed.");
            ItemKey key;
            WorkItem? permitted = null;
            if (create) key = ItemKey.Local(Guid.ParseExact(item, "D"));
            else
            {
                if (!int.TryParse(item, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var locator)
                    || locator <= 0) throw new InvalidDataException("Use a positive provider item number.");
                key = Require(await workspace.ResolveTargetAsync(locator, true, ct));
                var authorized = Require(await workspace.ObserveItemsAsync([key], ct));
                if (authorized.Items is not [var observed] || observed.Key != key)
                    throw new InvalidDataException("The selected actor could not observe the exact item.");
                permitted = observed;
            }
            var origin = new UpdateOrigin(handle.Source.StoreInstance, workspace.Connection, handle.Source.PrincipalId,
                create ? key.LocalId.ToString("D") : key.NativeId!, IsSeed: create);
            OriginalUpdate? replaced = null;
            if (predecessorReview is { } previous)
            {
                var prior = Require(await actions.OriginalUpdateAsync(origin, previous, ct));
                replaced = prior;
                if (previous == review || prior.Identity.State is not ("NotStarted" or "ReviewRequired" or "RejectedNoEffect"))
                    throw new InvalidDataException("Only an exact predecessor with attributable no-effect proof may be re-reviewed.");
                var proof = Require(await workspace.InspectUpdateAsync(prior, ct));
                if (proof is not ("NotStarted" or "RejectedNoEffect")) throw new InvalidDataException("No attributable no-effect proof.");
                var opId = UserBenchStore.ReplacementOperationId(review, prior);
                var existing = await actions.OriginalUpdateAsync(origin, review, ct);
                if (existing.IsSuccess)
                {
                    if (existing.Value.Plan.Operations.Single().Id != opId) throw new InvalidDataException("Replacement identity already bound.");
                }
                else
                {
                    if (existing.Error.Code != ErrorCode.NotFound) throw new InvalidDataException(existing.Error.Message);
                    var observed = Require(await workspace.ReadAsync(key, ct, skipUnchanged: true));
                    if (guard is not null && (guard.Baseline != observed.Revision?.Token || !guard.Authorized()))
                        throw new InvalidDataException("The authorized Canvas re-review observation changed.");
                    var state = Require(await workspace.SnapshotAsync(ct));
                    var capture = workspace.OwnerCapture(state.RetainedItems.SingleOrDefault(draft => draft.Key == key));
                    var old = prior.Plan.Operations.Single();
                    var op = new Op(opId, old.Kind, key, observed.Revision, old.Fields, body: old.Body, concurrency: old.Concurrency);
                    var plan = Require(await workspace.PrepareAsync([op], ct, retainPolicyEvidence: true,
                        binding: new(review, origin.Store, origin.Actor, capture)));
                    Require(await actions.BindUpdateAsync(origin, review, prior.InputHash, plan, boot, ct, prior));
                }
            }
            if (mode == "find")
            {
                var found = Require(await actions.FindUpdatesAsync(origin, ct));
                return Reply(new(found.Length switch { 0 => "NotFound", 1 => found[0].State, _ => "Ambiguous" },
                    [.. found], found.Length > 1));
            }

            if (create && args is ["seed", "publish", ..])
            {
                var prior = await actions.OriginalUpdateAsync(origin, review, ct);
                if (!prior.IsSuccess)
                {
                    if (prior.Error.Code != ErrorCode.NotFound) throw new InvalidDataException(prior.Error.Message);
                    var state = Require(await workspace.SnapshotAsync(ct));
                    if (state.RetainedItems.SingleOrDefault(d => d.Key == key) is not Seed seed)
                        throw new InvalidDataException("The reviewed seed is unavailable.");
                    var capture = workspace.OwnerCapture(seed);
                    foreach (var endpoint in seed.Links.Select(link => link.Other)
                        .Concat(seed.Parent is null ? [] : new[] { seed.Parent }).Distinct())
                    {
                        var published = state.ResolveVerified(endpoint);
                        if (published.IsLocal) throw new InvalidDataException("A creation endpoint has no verified native identity.");
                        Require(await workspace.ReadAsync(published, ct, skipUnchanged: true));
                    }
                    var op = new Op(review.ToString("D"), OpKind.Create, seed.Key, fields: seed.Fields, type: seed.Type,
                        other: seed.Parent, relation: seed.ParentRelation, resource: seed.Resource,
                        concurrency: ConcurrencyMode.ObservedNonAtomic, links: seed.Links);
                    var plan = Require(await workspace.PrepareAsync([op], ct, retainPolicyEvidence: true,
                        binding: new(review, origin.Store, origin.Actor, capture)));
                    bound = Require(await actions.BindUpdateAsync(origin, review, capture, plan, boot, ct));
                }
            }
            else if (comment || field is not null)
            {
                if (value == "--stdin") value = await readBody();
                var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                    comment ? ["comment", "", value!] : new[] { field!, value! }, UpdateReplyJson.Default.StringArray)));
                var prior = mode == "local" ? null : await actions.OriginalUpdateAsync(origin, review, ct);
                if (prior?.IsSuccess == true)
                {
                    if (prior.Value.InputHash != hash) throw new InvalidDataException("Review already binds different intent.");
                    bound = prior.Value.Identity;
                }
                else
                {
                    if (prior is not null && prior.Error.Code != ErrorCode.NotFound) throw new InvalidDataException(prior.Error.Message);
                    var observed = Require(await workspace.ReadAsync(key, ct, skipUnchanged: true));
                    if (guard is not null && (guard.Baseline != observed.Revision?.Token || !guard.Authorized()))
                        throw new InvalidDataException("The authorized Canvas observation changed.");
                    var snapshot = Require(await workspace.SnapshotAsync(ct));
                    var capture = workspace.OwnerCapture(snapshot.RetainedItems.SingleOrDefault(draft => draft.Key == key));
                    Op op;
                    if (comment)
                    {
                        if (string.IsNullOrWhiteSpace(value) || !workspace.Capabilities.Operations.Contains(OpKind.Comment))
                            throw new InvalidDataException("A nonempty note and a provider with comment support are required.");
                        var body = factory.IsGitHub ? FieldValue.RichText(value, FieldKind.Markdown) :
                            FieldValue.RichText(Twig.Infrastructure.Content.HtmlFieldFormatter.ResolveComment(value, null).EffectiveValue, FieldKind.Html);
                        op = new(review.ToString("D"), OpKind.Comment, key, observed.Revision, body: body, concurrency: workspace.Concurrency);
                    }
                    else
                    {
                        var schema = Require(await workspace.SchemaAsync(ct));
                        var definition = schema.Types.Single(t => t.Reference == observed.Type).Fields.SingleOrDefault(f => f.Reference == field);
                        if (definition is null || definition.ReadOnly || !CoreCommandAdapter.TryParse(definition.Kind, value!, null, out var typed))
                            throw new InvalidDataException("Unavailable field or invalid typed value.");
                        var fields = FieldValues.Empty.Set(field!, typed);
                        if (mode == "local")
                        {
                            Require(await workspace.StageEditAsync(key, fields, ct, capturedBaseline: observed, expectedCapture: capture));
                            return Reply(new("Retained", []));
                        }
                        op = new(review.ToString("D"), OpKind.Batch, key, observed.Revision, fields, concurrency: workspace.Concurrency);
                    }
                    var binding = new ReviewBinding(review, origin.Store, origin.Actor, capture);
                    var plan = Require(await workspace.PrepareAsync([op], ct, retainPolicyEvidence: true, binding: binding));
                    bound = Require(await actions.BindUpdateAsync(origin, review, hash, plan, boot, ct));
                }
            }
            var original = Require(await actions.OriginalUpdateAsync(origin, review, ct));
            bound = original.Identity;
            acquisitions.Release();
            reading = false;
            await acknowledgeBinding(JsonSerializer.Serialize(new UpdateReply(bound.State, [bound]), UpdateReplyJson.Default.UpdateReply));
            if (mode == "prepare") return Reply(new(bound.State, [bound]) {
                Replacement = replaced is null ? null : new(replaced.Identity.Review, replaced.Attempt, replaced.Plan.Digest, original.Plan.Digest,
                    replaced.Plan.Review!.Capture, original.Plan.Review!.Capture, replaced.Plan.Operations.Single().ExpectedRevision!.Token,
                    original.Plan.Operations.Single().ExpectedRevision!.Token, replaced.Plan.SchemaFingerprint, original.Plan.SchemaFingerprint,
                    replaced.Plan.PolicyRevision, original.Plan.PolicyRevision)
            });
            if (mode == "cancel" && bound.State == "Queued")
            {
                Require(await actions.MoveUpdateAsync(original, "Cancelled", boot, ct));
                WakeWaiters();
            }
            if (mode != "cancel" && !(mode == "observe" && bound.State == "Queued"))
            {
                if (routines.TryAdd(bound.Submission, 0))
                {
                    try
                    {
                        original = Require(await actions.OriginalUpdateAsync(origin, review, ct));
                        if (mode == "submit" && original.Identity.State == "Queued" && original.Owner == boot)
                        {
                            ct = stopping;
                            while (true)
                            {
                                var wake = Volatile.Read(ref admissionChanged).Task;
                                if (Require(await actions.MoveUpdateAsync(original, "Claimed", boot, ct))) break;
                                original = Require(await actions.OriginalUpdateAsync(origin, review, ct));
                                if (original.Identity.State != "Queued" || original.Owner != boot)
                                    return Reply(new(original.Identity.State, [original.Identity]));
                                yielded.TrySetResult(new("Queued", [original.Identity]));
                                await wake.WaitAsync(ct);
                            }
                            original = Require(await actions.OriginalUpdateAsync(origin, review, ct));
                            if (yielded.Task.IsCompleted && !create)
                                permitted = Require(await workspace.ObserveItemsAsync([key], ct)).Items.Single(i => i.Key == key);
                            if (!workspace.MatchesReviewCapture(original.Plan, Require(await workspace.SnapshotAsync(ct)))
                                || original.Plan.Operations[0].ExpectedRevision != permitted?.Revision)
                            {
                                Require(await actions.MoveUpdateAsync(original, "ReviewRequired", boot, ct));
                                return Reply(new("ReviewRequired", [Require(await actions.OriginalUpdateAsync(origin, review, ct)).Identity]));
                            }
                            var barrier = new OwnerBarrier(actions.Barrier(original, boot), workspace, review);
                            var delay = 100;
                            async Task<IDisposable> ResumeLocal()
                            {
                                var row = Require(await actions.OriginalUpdateAsync(origin, review, ct));
                                if (row.Owner != boot || !Require(await actions.MoveUpdateAsync(row, "Pending", boot, ct)))
                                    throw new InvalidDataException("The original local continuation is no longer owned.");
                                row = Require(await actions.OriginalUpdateAsync(origin, review, ct));
                                yielded.TrySetResult(new("Pending", [row.Identity]));
                                WakeWaiters();
                                await Task.Delay(delay, ct);
                                delay = Math.Min(2000, delay * 2);
                                if (row.Dispatch is null)
                                {
                                    while (true)
                                    {
                                        var wake = Volatile.Read(ref admissionChanged).Task;
                                        if (Require(await actions.MoveUpdateAsync(row, "Claimed", boot, ct))) break;
                                        await wake.WaitAsync(ct);
                                    }
                                }
                                await localBatches.WaitAsync(ct);
                                return new BatchLease(localBatches);
                            }
                            var result = await LocalCommit.WithContinuationAsync(ResumeLocal, ct,
                                () => workspace.ApplyAsync(original.Plan, ct, original: original, barrier: barrier));
                            var failure = result.IsSuccess ? result.Value.Error : result.Error;
                            if (failure is not null)
                            {
                                errors.WriteLine(failure.Message);
                                original = Require(await actions.OriginalUpdateAsync(origin, review, ct));
                                if (failure.StoreVersionChanged)
                                {
                                    Require(await actions.MoveUpdateAsync(original, "Pending", boot, ct));
                                    return Reply(new("Pending", [Require(await actions.OriginalUpdateAsync(origin, review, ct)).Identity]));
                                }
                                if (original.Dispatch is null && (failure.Code == ErrorCode.SchemaChanged || barrier.CaptureChanged
                                    || failure.Code == ErrorCode.Conflict && await workspace.OriginalDependenciesAsync(original.Plan, ct)
                                        is { IsSuccess: false, Error.Code: ErrorCode.SchemaChanged })
                                    && (await workspace.InspectUpdateAsync(original, ct)) is { IsSuccess: true, Value: "NotStarted" })
                                {
                                    Require(await actions.MoveUpdateAsync(original, "ReviewRequired", boot, ct));
                                    return Reply(new("ReviewRequired", [Require(await actions.OriginalUpdateAsync(origin, review, ct)).Identity]));
                                }
                            }
                        }
                        for (var i = 0; i < 2; i++)
                        {
                            original = Require(await actions.OriginalUpdateAsync(origin, review, ct));
                            if (!(original.Identity.State is "Claimed" or "Running" or "Settling" or "ObservationNeeded" or "Pending"
                                || create && original.Identity.State == "Partial")
                                || original.Identity.State == "Pending" && original.Dispatch is null && original.Owner == boot) break;
                            var next = await workspace.InspectUpdateAsync(original, ct);
                            if (!next.IsSuccess)
                            {
                                errors.WriteLine(next.Error.Message);
                                if (next.Error.StoreVersionChanged)
                                    Require(await actions.MoveUpdateAsync(original, "Pending", boot, ct));
                                else if (original.Identity.State == "Running")
                                    Require(await actions.MoveUpdateAsync(original, "ObservationNeeded", boot, ct));
                                break;
                            }
                            if (next.Value == "Complete")
                            {
                                Require(await workspace.DeliverOriginalUpdateAsync(actions, original, boot, ct));
                                break;
                            }
                            if (next.Value == original.Identity.State) break;
                            if (create && next.Value == "Settling")
                            {
                                var snapshot = Require(await workspace.SnapshotAsync(ct));
                                var mapping = snapshot.Mappings.Single(m => m.PlanDigest == original.Plan.Digest && m.Operation == 0 && m.Local == key);
                                var op = original.Plan.Operations[0];
                                var endpoints = op.Links.Select(l => l.Other).Concat(op.Other is null ? [] : new[] { op.Other })
                                    .Select(k => snapshot.VerifiedSeedTarget(workspace.Connection, k) ?? k);
                                if (!Require(await actions.MoveUpdateAsync(original, "Settling", boot, ct, mapping.Remote, endpoints))) break;
                                WakeWaiters();
                            }
                            else if (!Require(await actions.MoveUpdateAsync(original, next.Value, boot, ct))) break;
                        }
                    }
                    finally { routines.TryRemove(bound.Submission, out _); WakeWaiters(); }
                }
            }
            bound = Require(await actions.OriginalUpdateAsync(origin, review, ct)).Identity;
            return Reply(new(bound.State, [bound]));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or FormatException
            or ArgumentException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {
            errors.WriteLine(error.Message);
            return bound is null ? 2 : Reply(new("ObservationNeeded", [bound]));
        }
        finally { if (reading) acquisitions.Release(); }

        int Reply(UpdateReply reply)
        {
            output.WriteLine(JsonSerializer.Serialize(reply, UpdateReplyJson.Default.UpdateReply));
            return reply.Status is "Complete" or "Cancelled" or "Retained" ? 0 : 1;
        }
    }

    private static T Require<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new InvalidDataException(result.Error.Message);

    // This command is constructed only by the isolated qualification owner. Inject
    // latency after durable permission, before native SQLite starts its transaction.
    private sealed class OwnerBarrier(IOriginalUpdateBarrier inner, CoreWorkspace workspace, Guid review) : IOriginalUpdateBarrier
    {
        internal bool CaptureChanged { get; private set; }
        public Task WitnessCreateAsync(Plan plan, Guid attempt, OriginalResponse response, CancellationToken ct)
            => inner.WitnessCreateAsync(plan, attempt, response, ct);
        public async Task OpenAsync(Plan plan, Guid attempt, OriginalDispatch request, CancellationToken ct)
        {
            var dependencies = await workspace.OriginalDependenciesAsync(plan, ct);
            if (!dependencies.IsSuccess)
            {
                CaptureChanged = dependencies.Error.Code == ErrorCode.SchemaChanged;
                throw new IOException(dependencies.Error.Message);
            }
            using (Twig.Infrastructure.Auth.ConnectionOperationGate.Acquire(workspace.WorktreeRoot, exclusive: true))
            {
                if (!workspace.MatchesReviewCapture(plan, Require(await workspace.SnapshotAsync(ct))))
                {
                    CaptureChanged = true;
                    throw new IOException("The reviewed local capture changed before dispatch.");
                }
                await inner.OpenAsync(plan, attempt, request, ct);
            }
            if (Guid.TryParseExact(Environment.GetEnvironmentVariable("TWIG_TEST_PARK_ORIGINAL_REVIEW"), "D", out var parked)
                && parked == review) await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}
