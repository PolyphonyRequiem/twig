using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Cli.Tests.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Cli.Tests.Commands;

[Trait("Category", "HostInventory")]
public sealed class PreservationBackupProofTests
{
    private const string StopBeforeActivation = "test-stop-before-activation";

    [Theory]
    [InlineData("workspace-context")]
    [InlineData("navigation-sequence")]
    public async Task CoherentBackupOwnedRowsCannotBeCertifiedOrActivated(string mutation)
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var items = (SqliteWorkItemRepository)runtime.GetRequiredService<Twig.Domain.Interfaces.IWorkItemRepository>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Source item"));
        var context = runtime.GetRequiredService<Twig.Domain.Interfaces.IContextStore>();
        await context.SetValueAsync("source.preference", "unchanged");
        var history = runtime.GetRequiredService<Twig.Domain.Interfaces.INavigationHistoryStore>();
        await history.RecordVisitAsync(42);
        var sourceHistory = await history.GetHistoryAsync();
        var before = new StoreParityHarness(store, items.DurableScope!, preserveOpaqueContext: true).Inspect();
        before.IsConsistent.ShouldBeTrue();
        TargetRowCount(store).ShouldBe(0);
        new PreservedWorkspaceState(store).IsActive.ShouldBeFalse();
        var generation = store.ValidateAdmittedGeneration();
        generation.ShouldNotBeNull();

        var preview = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!).Preview();
        var backupRoot = Path.GetFullPath(preview.BackupDirectory);
        var mutationWrites = 0;
        var migration = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!, phase =>
        {
            if (phase == "backup-durable-copied")
            {
                var snapshot = Directory.GetDirectories(backupRoot, "snapshot-*").ShouldHaveSingleItem();
                var pendingBackup = Path.Combine(snapshot, "pending.db");
                ReadIntegrity(pendingBackup).ShouldBe("ok");
                using (var backup = OpenReadWrite(pendingBackup))
                {
                    using var command = backup.CreateCommand();
                    command.CommandText = mutation switch
                    {
                        "workspace-context" =>
                            "INSERT INTO workspace_context(key,value) VALUES('backup-only','valid');",
                        "navigation-sequence" =>
                            "INSERT INTO sqlite_sequence(name,seq) VALUES('workspace_navigation_history',1);",
                        _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
                    };
                    mutationWrites = command.ExecuteNonQuery();
                    mutationWrites.ShouldBe(1);
                    ReadMutation(backup, mutation).ShouldBe(1);
                    ReadIntegrity(backup).ShouldBe("ok");
                }
            }
            else if (phase == "before-activation")
            {
                throw new InvalidOperationException(StopBeforeActivation);
            }
        });

        var error = await Should.ThrowAsync<InvalidOperationException>(() => migration.ApplyAsync(preview.Digest));

        error.Message.ShouldStartWith("preservation-target-unclassified:");
        mutationWrites.ShouldBe(1);
        File.Exists(Path.Combine(backupRoot, "manifest.json")).ShouldBeFalse();
        new PreservedWorkspaceState(store).IsActive.ShouldBeFalse();
        store.ValidateAdmittedGeneration().ShouldBe(generation);
        (await items.GetByIdAsync(42))!.Title.ShouldBe("Source item");
        (await context.GetValueAsync("source.preference")).ShouldBe("unchanged");
        var afterHistory = await history.GetHistoryAsync();
        afterHistory.Entries.ShouldBe(sourceHistory.Entries);
        afterHistory.CursorEntryId.ShouldBe(sourceHistory.CursorEntryId);

        var after = new StoreParityHarness(store, items.DurableScope!, preserveOpaqueContext: true).Inspect();
        foreach (var original in before.Classes.Where(entry => !IsMigrationOwned(entry.ClassName)))
        {
            var retained = after.Classes.Single(entry => entry.ClassName == original.ClassName);
            retained.RowCount.ShouldBe(original.RowCount);
            retained.CanonicalDigest.ShouldBe(original.CanonicalDigest);
        }
        TargetRowCount(store).ShouldBe(0);
        MigrationState(store, preview.Digest).ShouldBe("preparing");

        var snapshot = Directory.GetDirectories(backupRoot, "snapshot-*").ShouldHaveSingleItem();
        var pendingBackupPath = Path.Combine(snapshot, "pending.db");
        ReadIntegrity(pendingBackupPath).ShouldBe("ok");
        using var verifiedBackup = OpenReadOnly(pendingBackupPath);
        ReadMutation(verifiedBackup, mutation).ShouldBe(1);
        ReadIntegrity(verifiedBackup).ShouldBe("ok");
        fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task RetainedManifestIsRevalidatedAgainstOwnedBackupRowsBeforeActivation()
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var items = (SqliteWorkItemRepository)runtime.GetRequiredService<Twig.Domain.Interfaces.IWorkItemRepository>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Source item"));
        var context = runtime.GetRequiredService<Twig.Domain.Interfaces.IContextStore>();
        await context.SetValueAsync("source.preference", "unchanged");
        var history = runtime.GetRequiredService<Twig.Domain.Interfaces.INavigationHistoryStore>();
        await history.RecordVisitAsync(42);
        var sourceHistory = await history.GetHistoryAsync();
        TargetRowCount(store).ShouldBe(0);
        var generation = store.ValidateAdmittedGeneration();
        generation.ShouldNotBeNull();
        var preview = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!).Preview();
        var backupRoot = Path.GetFullPath(preview.BackupDirectory);
        var migration = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!, phase =>
        {
            if (phase == "before-activation")
                throw new IOException("Leave a qualified manifest for revalidation.");
        });

        var interruption = await Should.ThrowAsync<IOException>(() => migration.ApplyAsync(preview.Digest));
        interruption.Message.ShouldBe("Leave a qualified manifest for revalidation.");
        var manifestPath = Path.Combine(backupRoot, "manifest.json");
        File.Exists(manifestPath).ShouldBeTrue();
        new PreservedWorkspaceState(store).IsActive.ShouldBeFalse();
        TargetRowCount(store).ShouldBe(0);

        var snapshot = Directory.GetDirectories(backupRoot, "snapshot-*").ShouldHaveSingleItem();
        var pendingBackupPath = Path.Combine(snapshot, "pending.db");
        using (var backup = OpenReadWrite(pendingBackupPath))
        using (var command = backup.CreateCommand())
        {
            command.CommandText = "INSERT INTO workspace_context(key,value) VALUES('backup-only','valid');";
            command.ExecuteNonQuery().ShouldBe(1);
            ReadMutation(backup, "workspace-context").ShouldBe(1);
        }
        ReadIntegrity(pendingBackupPath).ShouldBe("ok");

        var error = await Should.ThrowAsync<InvalidOperationException>(() => migration.ApplyAsync(preview.Digest));

        error.Message.ShouldStartWith("preservation-target-unclassified:");
        new PreservedWorkspaceState(store).IsActive.ShouldBeFalse();
        store.ValidateAdmittedGeneration().ShouldBe(generation);
        (await items.GetByIdAsync(42))!.Title.ShouldBe("Source item");
        (await context.GetValueAsync("source.preference")).ShouldBe("unchanged");
        var afterHistory = await history.GetHistoryAsync();
        afterHistory.Entries.ShouldBe(sourceHistory.Entries);
        afterHistory.CursorEntryId.ShouldBe(sourceHistory.CursorEntryId);
        TargetRowCount(store).ShouldBe(0);
        MigrationState(store, preview.Digest).ShouldBe("preparing");
        File.Exists(manifestPath).ShouldBeTrue();
        using var verifiedBackup = OpenReadOnly(pendingBackupPath);
        ReadMutation(verifiedBackup, "workspace-context").ShouldBe(1);
        ReadIntegrity(verifiedBackup).ShouldBe("ok");
        fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    private static SqliteConnection OpenReadWrite(string path)
    {
        return Open(path, SqliteOpenMode.ReadWrite);
    }

    private static SqliteConnection OpenReadOnly(string path) =>
        Open(path, SqliteOpenMode.ReadOnly);

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var logical = Path.GetFullPath(path);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = logical,
            Mode = mode,
            Pooling = false,
        };
        if (OperatingSystem.IsWindows())
        {
            builder.Vfs = "win32-longpath";
            builder.DataSource = logical.StartsWith(@"\\?\", StringComparison.Ordinal)
                ? logical
                : logical.StartsWith(@"\\", StringComparison.Ordinal)
                    ? @"\\?\UNC\" + logical[2..]
                    : @"\\?\" + logical;
        }
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        SqliteCacheStore.RegisterCollations(connection);
        return connection;
    }

    private static int ReadMutation(SqliteConnection connection, string mutation)
    {
        using var command = connection.CreateCommand();
        command.CommandText = mutation switch
        {
            "workspace-context" =>
                "SELECT COUNT(*) FROM workspace_context WHERE key='backup-only' AND value='valid';",
            "navigation-sequence" =>
                "SELECT COUNT(*) FROM sqlite_sequence WHERE name='workspace_navigation_history' AND seq=1;",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ReadIntegrity(string path)
    {
        using var connection = OpenReadOnly(path);
        return ReadIntegrity(connection);
    }

    private static string ReadIntegrity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return command.ExecuteScalar() as string
            ?? throw new InvalidOperationException("SQLite integrity_check returned no result.");
    }

    private static int TargetRowCount(SqliteCacheStore store)
    {
        using var command = store.GetConnection().CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM pending.seed_local_identities)
              + (SELECT COUNT(*) FROM pending.seed_work_items)
              + (SELECT COUNT(*) FROM pending.workspace_context)
              + (SELECT COUNT(*) FROM pending.workspace_navigation_history)
              + (SELECT COUNT(*) FROM pending.sqlite_sequence WHERE name='workspace_navigation_history');
            """;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? MigrationState(SqliteCacheStore store, string digest)
    {
        using var command = store.GetConnection().CreateCommand();
        command.CommandText = "SELECT state FROM pending.preservation_migrations WHERE source_digest=$digest;";
        command.Parameters.AddWithValue("$digest", digest);
        return command.ExecuteScalar() as string;
    }

    private static bool IsMigrationOwned(string className) => className is
        "pending.seed_work_items" or "pending.workspace_context" or
        "pending.workspace_navigation_history" or "pending.preservation_migrations" or
        "pending.workspace-navigation-sequence" or "pending.seed_local_identities";
}
