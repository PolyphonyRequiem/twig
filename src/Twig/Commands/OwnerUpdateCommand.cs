using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Twig.Core;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;

namespace Twig.Commands;

internal sealed record UpdateReply(string Status, UpdateIdentity[] Matches, bool Ambiguous = false);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdateReply))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class UpdateReplyJson : JsonSerializerContext;

internal sealed class OwnerUpdateCommand(Guid boot, string home, UserBenchStore actions, IServiceProvider? hostServices = null)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> routines = new();
    private readonly SemaphoreSlim acquisitions = new(8, 8);

    internal async Task<int> ExecuteAsync(string[] args, string directory, Func<Task<string>> readBody, Func<string,Task> acknowledgeBinding,
        TextWriter output, TextWriter errors, CancellationToken ct)
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
            string? field = null, value = null;
            var comment = false;
            switch (args)
            {
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
            if (transport is not null)
            {
                services.AddSingleton(transport);
                services.AddSingleton<Twig.Infrastructure.Auth.IConnectionBindingService>(_ =>
                    new Twig.Infrastructure.Auth.ConnectionBindingService(home, patHttpClient: identityClient));
            }
            if (credential is not null) services.AddSingleton(credential);
            services.AddCoreWorkspaceServices(config, paths, executionOwner: true);
            using var runtime = services.BuildServiceProvider();
            var factory = runtime.GetRequiredService<CoreWorkspaceFactory>();
            using var handle = Require(await factory.OpenAsync(ct));
            pinned.Dispose();
            Require(await factory.AdmitSourceAsync(handle, ct));
            var workspace = handle.Workspace;
            if (!int.TryParse(item, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var locator)
                || locator <= 0) throw new InvalidDataException("Use a positive provider item number.");
            var key = Require(await workspace.ResolveTargetAsync(locator, true, ct));
            var authorized = Require(await workspace.ObserveItemsAsync([key], ct));
            if (authorized.Items is not [var permitted] || permitted.Key != key)
                throw new InvalidDataException("The selected actor could not observe the exact item.");
            var origin = new UpdateOrigin(handle.Source.StoreInstance, workspace.Connection, handle.Source.PrincipalId, key.NativeId!);
            if (mode == "find")
            {
                var found = Require(await actions.FindUpdatesAsync(origin, ct));
                return Reply(new(found.Length switch { 0 => "NotFound", 1 => found[0].State, _ => "Ambiguous" },
                    [.. found], found.Length > 1));
            }

            if (comment || field is not null)
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
            if (mode == "prepare") return Reply(new(bound.State, [bound]));
            if (mode == "cancel" && bound.State == "Queued")
                Require(await actions.MoveUpdateAsync(original, "Cancelled", boot, ct));
            if (mode != "cancel")
            {
                if (routines.TryAdd(bound.Submission, 0))
                {
                    try
                    {
                        original = Require(await actions.OriginalUpdateAsync(origin, review, ct));
                        if (mode == "submit" && original.Identity.State == "Queued"
                            && Require(await actions.MoveUpdateAsync(original, "Claimed", boot, ct)))
                        {
                            original = Require(await actions.OriginalUpdateAsync(origin, review, ct));
                            if (!workspace.MatchesReviewCapture(original.Plan, Require(await workspace.SnapshotAsync(ct)))
                                || original.Plan.Operations[0].ExpectedRevision != permitted.Revision)
                            {
                                Require(await actions.MoveUpdateAsync(original, "ReviewRequired", boot, ct));
                                return Reply(new("ReviewRequired", [Require(await actions.OriginalUpdateAsync(origin, review, ct)).Identity]));
                            }
                            var barrier = new OwnerBarrier(actions.Barrier(original, boot), workspace, review);
                            var result = await workspace.ApplyAsync(original.Plan, ct, original: original, barrier: barrier);
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
                                if ((failure.Code == ErrorCode.SchemaChanged || barrier.CaptureChanged) && original.Dispatch is null
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
                            if (original.Identity.State is not ("Claimed" or "Running" or "Settling" or "ObservationNeeded" or "Pending")
                                || original.Identity.State == "Pending" && original.Dispatch is null) break;
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
                            if (next.Value == original.Identity.State || !Require(await actions.MoveUpdateAsync(original, next.Value, boot, ct))) break;
                        }
                    }
                    finally { routines.TryRemove(bound.Submission, out _); }
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
        public async Task OpenAsync(Plan plan, Guid attempt, OriginalDispatch request, CancellationToken ct)
        {
            using (Twig.Infrastructure.Auth.ConnectionOperationGate.Acquire(workspace.WorktreeRoot, exclusive: true))
            {
                if (!workspace.MatchesReviewCapture(plan, Require(await workspace.SnapshotAsync(ct))))
                {
                    CaptureChanged = true;
                    throw new InvalidDataException("The reviewed local capture changed before dispatch.");
                }
                await inner.OpenAsync(plan, attempt, request, ct);
            }
            if (Guid.TryParseExact(Environment.GetEnvironmentVariable("TWIG_TEST_PARK_ORIGINAL_REVIEW"), "D", out var parked)
                && parked == review) await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}
