using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.DependencyInjection;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;
using Twig.Providers.GitHub;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Theory]
    [InlineData("lost-receipt")]
    [InlineData("closed")]
    [InlineData("open")]
    [InlineData("create-lost-receipt")]
    public async Task OriginalAdoUpdateObservesRetainedEvidenceWithoutReplay(string fault)
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        await OriginalUpdateCrash.Check(async action =>
        {
            var registration = new ServiceCollection();
            registration.AddSingleton<IConnectionBindingService>(_bindings);
            registration.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
            registration.AddTwigWorkspaceServices(paths.StartDir, ["pending", "-o", "json"]);
            using var services = registration.BuildServiceProvider();
            using var handle = (await services.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
            await action(handle.Workspace);
        }, UserBenchStore.ForRegistry(_bindings.RegistryPath), store, "42", "System.Title", fault,
            failReadback: fail => _transport.FailReadbackAfterCommit = fail);
        _transport.PatchAttempts.ShouldBe(fault == "lost-receipt" ? 1 : 0);
        _transport.PostCount.ShouldBe(fault == "create-lost-receipt" ? 1 : 0);
    }
}

public sealed partial class GitHubCoreSpineTests
{
    [Theory]
    [InlineData("lost-receipt")]
    [InlineData("closed")]
    [InlineData("open")]
    [InlineData("create-lost-receipt")]
    public async Task OriginalGitHubUpdateObservesRetainedEvidenceWithoutReplay(string fault)
    {
        using var f = new Fixture();
        await f.Initialize(explicitStore: false);
        if (fault == "create-lost-receipt") f.Http.ResponseNote = "echo: synthetic-secret";
        await OriginalUpdateCrash.Check(f.UseWorkspace, UserBenchStore.ForRegistry(f._bindings.RegistryPath),
            (await SqliteStore.OpenAsync(f.Database)).Value, "I_opaque", GitHubProvider.Title, fault,
            title => f.Http.Issue["title"] = title, fail => f.Http.Defect = fail ? "actor" : null);
        f.Http.Writes.ShouldBe(fault.EndsWith("lost-receipt", StringComparison.Ordinal) ? 1 : 0);
    }
}

internal static class OriginalUpdateCrash
{
    internal static async Task Check(Func<Func<CoreWorkspace,Task>,Task> reopen, UserBenchStore actions, SqliteStore mirror,
        string nativeId, string field, string fault, Action<string>? changeTitle = null, Action<bool>? failReadback = null)
    {
        var boot = Guid.NewGuid();
        var review = Guid.NewGuid();
        UpdateOrigin origin = null!;
        Guid submission = default;
        var create = fault == "create-lost-receipt";
        var loseReceipt = create || fault == "lost-receipt";
        await reopen(async workspace =>
        {
            origin = new(mirror.InstanceId, workspace.Connection, workspace.NativeActor, nativeId);
            var key = ItemKey.Remote(workspace.Connection.Provider, workspace.Connection.ResourceScope, nativeId);
            var item = (await workspace.ReadAsync(key, default)).Value;
            var fields = FieldValues.Empty.Set(field, FieldValue.String("reviewed update"));
            var op = new Op(review.ToString("D"), OpKind.Batch, key, item.Revision, fields, concurrency: workspace.Concurrency);
            Seed? seed = null;
            if (create)
            {
                var stateField = (await workspace.SchemaAsync(default)).Value.Types.Single(t => t.Reference == item.Type).StateField;
                fields = fields.Set(stateField, item.Fields[stateField]);
                seed = (await workspace.StageSeedAsync(item.Type, fields, default)).Value;
                origin = origin with { Item = seed.Key.LocalId.ToString("D"), IsSeed = true };
                op = new(review.ToString("D"), OpKind.Create, seed.Key, fields: seed.Fields, type: seed.Type,
                    concurrency: ConcurrencyMode.ObservedNonAtomic);
            }
            var plan = (await workspace.PrepareAsync([op], default,
                retainPolicyEvidence: true, binding: new(review, origin.Store, origin.Actor, workspace.OwnerCapture(seed)))).Value;
            submission = (await actions.BindUpdateAsync(origin, review, "input", plan, boot, default)).Value.Submission;
            var original = (await actions.OriginalUpdateAsync(origin, review, default)).Value;
            (await actions.MoveUpdateAsync(original, "Claimed", boot, default)).Value.ShouldBeTrue();
            original = (await actions.OriginalUpdateAsync(origin, review, default)).Value;
            if (loseReceipt) await Sql("""
                CREATE TRIGGER reject_core_receipt BEFORE UPDATE ON core_snapshots WHEN instr(NEW.document,'http:')>0
                BEGIN SELECT RAISE(ABORT,'injected acknowledgement CAS conflict'); END;
                """);
            try
            {
                var result = await workspace.ApplyAsync(plan, default, original: original,
                    barrier: new Interrupt(actions.Barrier(original, boot), fault));
                result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
                result.Value.Error.ShouldNotBeNull();
            }
            finally { if (loseReceipt) await Sql("DROP TRIGGER reject_core_receipt;"); }
        });
        await reopen(async workspace =>
        {
            var original = (await actions.OriginalUpdateAsync(origin, review, default)).Value;
            original.Identity.Submission.ShouldBe(submission);
            original.Identity.State.ShouldBe(fault == "closed" ? "Claimed" : "Running");
            if (create && changeTitle is not null)
            {
                var path = Path.Combine(Path.GetDirectoryName(mirror.DatabasePath)!, "core-github-intents", original.Attempt.ToString("D") + ".json.ack");
                var authentic = await File.ReadAllTextAsync(path);
                var forged = System.Text.Json.Nodes.JsonNode.Parse(authentic)!;
                forged["statusCode"] = 403;
                await File.WriteAllTextAsync(path, forged.ToJsonString());
                (await workspace.InspectUpdateAsync(original, default)).IsSuccess.ShouldBeFalse();
                (await workspace.SnapshotAsync(default)).Value.Journal.Single().Status.ShouldBe(OpStatus.Applying);
                await File.WriteAllTextAsync(path, authentic);
            }
            var observed = await workspace.InspectUpdateAsync(original, default);
            if (fault == "open")
            {
                observed.IsSuccess.ShouldBeFalse();
                (await workspace.SnapshotAsync(default)).Value.Journal.Single().Status.ShouldBe(OpStatus.Applying);
            }
            else
            {
                observed.IsSuccess.ShouldBeTrue(observed.IsSuccess ? "" : observed.Error.Message);
                observed.Value.ShouldBe(fault == "closed" ? "NotStarted" : "Settling");
                var snapshot = (await workspace.SnapshotAsync(default)).Value;
                var reservation = create ? snapshot.Mappings.Single(m => m.Local == original.Plan.Operations[0].Item).Remote : null;
                (await actions.MoveUpdateAsync(original, observed.Value, Guid.NewGuid(), default, reservation)).Value.ShouldBeTrue();
                if (loseReceipt)
                {
                    original = (await actions.OriginalUpdateAsync(origin, review, default)).Value;
                    if (create && failReadback is not null)
                    {
                        failReadback(true);
                        for (var i = 0; i < 2; i++)
                        {
                            (await workspace.InspectUpdateAsync(original, default)).Value.ShouldBe("Settling");
                            (await workspace.SnapshotAsync(default)).Value.Journal.Single().Status.ShouldBe(OpStatus.Applied);
                        }
                        failReadback(false);
                    }
                    if (create && changeTitle is not null)
                    {
                        changeTitle("different readback");
                        (await workspace.InspectUpdateAsync(original, default)).Value.ShouldBe("Partial");
                        (await actions.MoveUpdateAsync(original, "Partial", Guid.NewGuid(), default)).Value.ShouldBeTrue();
                        var partial = (await workspace.SnapshotAsync(default)).Value;
                        partial.Mappings.Single().Remote.ShouldBe(reservation);
                        partial.VerifiedSeedTarget(workspace.Connection, original.Plan.Operations[0].Item).ShouldBeNull();
                        original = (await actions.OriginalUpdateAsync(origin, review, default)).Value;
                        changeTitle("reviewed update");
                    }
                    var result = await workspace.InspectUpdateAsync(original, default);
                    result.Value.ShouldBe("Complete");
                }
            }
        });

        async Task Sql(string text)
        {
            using var db = new SqliteConnection($"Data Source={mirror.DatabasePath};Pooling=False");
            await db.OpenAsync();
            using var command = db.CreateCommand();
            command.CommandText = text;
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class Interrupt(IOriginalUpdateBarrier real, string fault) : IOriginalUpdateBarrier
    {
        public Task WitnessCreateAsync(Plan plan, Guid attempt, OriginalResponse response, CancellationToken ct)
            => real.WitnessCreateAsync(plan, attempt, response, ct);
        public async Task OpenAsync(Plan plan, Guid attempt, OriginalDispatch dispatch, CancellationToken ct)
        {
            if (fault == "closed") throw new IOException("Injected exit before Open.");
            await real.OpenAsync(plan, attempt, dispatch, ct);
            if (fault == "open") throw new IOException("Injected exit after Open.");
        }
    }
}
