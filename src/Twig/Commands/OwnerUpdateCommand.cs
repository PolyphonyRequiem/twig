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

internal sealed class OwnerUpdateCommand(Guid boot, string home, UserBenchStore actions)
{
    private readonly SemaphoreSlim prepare = new(1);
    private readonly SemaphoreSlim[] routines = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();

    internal async Task<int> ExecuteAsync(string[] args, string directory, Func<Task<string>> readBody, Func<string,Task> acknowledgeBinding,
        TextWriter output, TextWriter errors, CancellationToken ct)
    {
        UpdateIdentity? bound = null;
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
            switch (args)
            {
                case ["update", "--find", "--id", var id]: item = id; mode = "find"; break;
                case ["update", var op, var rev, "--id", var id] when op is "--observe" or "--submit" or "--cancel":
                    item = id; mode = op[2..]; review = Guid.ParseExact(rev, "D"); break;
                case ["update", var f, var v, "--id", var id, "--review", var rev, .. var tail] when tail is [] or ["--prepare"]:
                    item = id; field = f; value = v; review = Guid.ParseExact(rev, "D"); mode = tail.Length == 0 ? "submit" : "prepare"; break;
                default: throw new InvalidDataException("Expected an explicit update, review and work item, or --find --id.");
            }
            if (item.Length is 0 or > 256 || mode != "find" && review == Guid.Empty)
                throw new InvalidDataException("An exact item and nonempty review UUID are required.");
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
            services.AddCoreWorkspaceServices(config, paths);
            using var runtime = services.BuildServiceProvider();
            var factory = runtime.GetRequiredService<CoreWorkspaceFactory>();
            using var handle = Require(await factory.OpenAsync(ct));
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

            if (field is not null)
            {
                if (value == "--stdin") value = await readBody();
                var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                    new[] { field, value! }, UpdateReplyJson.Default.StringArray)));
                await prepare.WaitAsync(ct);
                try
                {
                    var prior = await actions.OriginalUpdateAsync(origin, review, ct);
                    if (prior.IsSuccess)
                    {
                        if (prior.Value.InputHash != hash) throw new InvalidDataException("Review already binds different intent.");
                        bound = prior.Value.Identity;
                    }
                    else
                    {
                        if (prior.Error.Code != ErrorCode.NotFound) throw new InvalidDataException(prior.Error.Message);
                        var observed = Require(await workspace.ReadAsync(key, ct, skipUnchanged: true));
                        var schema = Require(await workspace.SchemaAsync(ct));
                        var definition = schema.Types.Single(t => t.Reference == observed.Type).Fields.SingleOrDefault(f => f.Reference == field);
                        if (definition is null || definition.ReadOnly || !CoreCommandAdapter.TryParse(definition.Kind, value!, null, out var typed))
                            throw new InvalidDataException("Unavailable field or invalid typed value.");
                        var plan = Require(await workspace.PrepareAsync([new(review.ToString("D"), OpKind.Batch, key, observed.Revision,
                            FieldValues.Empty.Set(field, typed), concurrency: workspace.Concurrency)], ct, retainPolicyEvidence: true));
                        bound = Require(await actions.BindUpdateAsync(origin, review, hash, plan, boot, ct));
                    }
                }
                finally { prepare.Release(); }
            }
            var original = Require(await actions.OriginalUpdateAsync(origin, review, ct));
            bound = original.Identity;
            await acknowledgeBinding(JsonSerializer.Serialize(new UpdateReply(bound.State, [bound]), UpdateReplyJson.Default.UpdateReply));
            if (mode == "prepare") return Reply(new(bound.State, [bound]));
            if (mode == "cancel" && bound.State == "Queued")
                Require(await actions.MoveUpdateAsync(original, "Cancelled", boot, ct));
            if (mode != "cancel")
            {
                var slot = routines[(uint)bound.Submission.GetHashCode() % (uint)routines.Length];
                if (await slot.WaitAsync(0, ct))
                {
                    try
                    {
                        original = Require(await actions.OriginalUpdateAsync(origin, review, ct));
                        if (mode == "submit" && original.Identity.State == "Queued"
                            && Require(await actions.MoveUpdateAsync(original, "Claimed", boot, ct)))
                        {
                            original = Require(await actions.OriginalUpdateAsync(origin, review, ct));
                            var result = await workspace.ApplyAsync(original.Plan, ct, original: original,
                                barrier: actions.Barrier(original, boot));
                            if (!result.IsSuccess || result.Value.Error is not null)
                                errors.WriteLine(result.IsSuccess ? result.Value.Error!.Message : result.Error.Message);
                        }
                        for (var i = 0; i < 2; i++)
                        {
                            original = Require(await actions.OriginalUpdateAsync(origin, review, ct));
                            if (original.Identity.State is not ("Claimed" or "Running" or "Settling" or "ObservationNeeded")) break;
                            var next = await workspace.InspectUpdateAsync(original, ct);
                            if (!next.IsSuccess)
                            {
                                errors.WriteLine(next.Error.Message);
                                if (original.Identity.State == "Running")
                                    Require(await actions.MoveUpdateAsync(original, "ObservationNeeded", boot, ct));
                                break;
                            }
                            if (next.Value == original.Identity.State || !Require(await actions.MoveUpdateAsync(original, next.Value, boot, ct))) break;
                        }
                    }
                    finally { slot.Release(); }
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

        int Reply(UpdateReply reply)
        {
            output.WriteLine(JsonSerializer.Serialize(reply, UpdateReplyJson.Default.UpdateReply));
            return reply.Status is "Complete" or "Cancelled" ? 0 : 1;
        }
    }

    private static T Require<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new InvalidDataException(result.Error.Message);
}
