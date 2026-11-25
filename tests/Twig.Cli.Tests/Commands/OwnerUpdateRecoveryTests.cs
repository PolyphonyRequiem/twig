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
        }, UserBenchStore.ForRegistry(_bindings.RegistryPath), store, "42", "System.Title", fault);
        _transport.PatchAttempts.ShouldBe(fault == "lost-receipt" ? 1 : 0);
    }
}

public sealed partial class GitHubCoreSpineTests
{
    [Theory]
    [InlineData("lost-receipt")]
    [InlineData("closed")]
    [InlineData("open")]
    public async Task OriginalGitHubUpdateObservesRetainedEvidenceWithoutReplay(string fault)
    {
        using var f = new Fixture();
        await f.Initialize(explicitStore: false);
        await OriginalUpdateCrash.Check(f.UseWorkspace, UserBenchStore.ForRegistry(f._bindings.RegistryPath),
            (await SqliteStore.OpenAsync(f.Database)).Value, "I_opaque", GitHubProvider.Title, fault);
        f.Http.Writes.ShouldBe(fault == "lost-receipt" ? 1 : 0);
    }
}

internal static class OriginalUpdateCrash
{
    internal static async Task Check(Func<Func<CoreWorkspace,Task>,Task> reopen, UserBenchStore actions, SqliteStore mirror,
        string nativeId, string field, string fault)
    {
        var boot = Guid.NewGuid();
        var review = Guid.NewGuid();
        UpdateOrigin origin = null!;
        Guid submission = default;
        await reopen(async workspace =>
        {
            origin = new(mirror.InstanceId, workspace.Connection, workspace.NativeActor, nativeId);
            var key = ItemKey.Remote(workspace.Connection.Provider, workspace.Connection.ResourceScope, nativeId);
            var item = (await workspace.ReadAsync(key, default)).Value;
            var plan = (await workspace.PrepareAsync([new(review.ToString("D"), OpKind.Batch, key, item.Revision,
                FieldValues.Empty.Set(field, FieldValue.String("reviewed update")), concurrency: workspace.Concurrency)], default,
                retainPolicyEvidence: true, binding: new(review, origin.Store, origin.Actor, workspace.OwnerCapture(null)))).Value;
            submission = (await actions.BindUpdateAsync(origin, review, "input", plan, boot, default)).Value.Submission;
            var original = (await actions.OriginalUpdateAsync(origin, review, default)).Value;
            (await actions.MoveUpdateAsync(original, "Claimed", boot, default)).Value.ShouldBeTrue();
            original = (await actions.OriginalUpdateAsync(origin, review, default)).Value;
            if (fault == "lost-receipt") await Sql("""
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
            finally { if (fault == "lost-receipt") await Sql("DROP TRIGGER reject_core_receipt;"); }
        });
        await reopen(async workspace =>
        {
            var original = (await actions.OriginalUpdateAsync(origin, review, default)).Value;
            original.Identity.Submission.ShouldBe(submission);
            original.Identity.State.ShouldBe(fault == "closed" ? "Claimed" : "Running");
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
                (await actions.MoveUpdateAsync(original, observed.Value, Guid.NewGuid(), default)).Value.ShouldBeTrue();
                if (fault == "lost-receipt")
                {
                    original = (await actions.OriginalUpdateAsync(origin, review, default)).Value;
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
        public async Task OpenAsync(Plan plan, Guid attempt, OriginalDispatch dispatch, CancellationToken ct)
        {
            if (fault == "closed") throw new IOException("Injected exit before Open.");
            await real.OpenAsync(plan, attempt, dispatch, ct);
            if (fault == "open") throw new IOException("Injected exit after Open.");
        }
    }
}
