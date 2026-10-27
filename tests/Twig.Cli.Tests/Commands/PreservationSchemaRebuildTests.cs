using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Cli.Tests.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Cli.Tests.Commands;

[Trait("Category", "HostInventory")]
public sealed class PreservationSchemaRebuildTests
{
    [Fact]
    public async Task CancelledSlotNullHistoryAllowsSchema15ScratchCloneRebuild()
    {
        await using var scratch = await CreateCancelledSchema15ScratchAsync();
        var rebuildRoot = CopyTemplate(scratch, "cancelled-rebuild");

        using (var rebuilt = OpenStore(Path.Combine(rebuildRoot, "twig.db")))
        {
            ReadSchemaVersion(rebuilt.GetConnection()).ShouldBe("17");
            HasAssignedToUniqueName(rebuilt.GetConnection()).ShouldBeTrue();
            ReadMigrationHistory(rebuilt.GetConnection(), scratch.History.IntentId!).ShouldBe(scratch.History);
            TargetRowCount(rebuilt.GetConnection()).ShouldBe(0);
        }

        await AssertOriginalGenerationUnchangedAsync(scratch);
    }

    [Theory]
    [InlineData("preparing-slot", "preparing", 1, false, false)]
    [InlineData("active-slot", "active", 1, false, false)]
    [InlineData("rolled-back-slot", "rolled-back", 1, false, false)]
    [InlineData("unknown-slot-null", "unknown", null, false, true)]
    [InlineData("cancelled-slot", "cancelled", 1, false, false)]
    [InlineData("cancelled-retained-target", "cancelled", null, true, false)]
    public async Task OwningOrUnknownIntentAndRetainedTargetsRefuseSchemaRebuild(
        string name, string state, int? slot, bool retainTarget, bool bypassStateConstraint)
    {
        await using var scratch = await CreateCancelledSchema15ScratchAsync();
        var caseRoot = CopyTemplate(scratch, name);
        var mirrorPath = Path.Combine(caseRoot, "twig.db");
        var pendingPath = Path.Combine(caseRoot, "pending.db");
        var expected = scratch.History with { State = state, Slot = slot };
        using (var pending = OpenReadWrite(pendingPath))
        {
            UpdateMigrationHistory(pending, scratch.History.IntentId!, state, slot, bypassStateConstraint);
            if (retainTarget)
            {
                using var addTarget = pending.CreateCommand();
                addTarget.CommandText =
                    "INSERT INTO workspace_context(key,value) VALUES('retained-target','must remain');";
                addTarget.ExecuteNonQuery().ShouldBe(1);
            }
            ReadMigrationHistory(pending, scratch.History.IntentId!, attachedDurable: false).ShouldBe(expected);
            TargetRowCount(pending, attachedDurable: false).ShouldBe(retainTarget ? 1 : 0);
        }

        var refusal = Should.Throw<InvalidOperationException>(() => OpenStore(mirrorPath).Dispose());
        refusal.Message.ShouldStartWith("mirror-local-work-present:");

        using var unchangedMirror = OpenReadOnly(mirrorPath);
        ReadSchemaVersion(unchangedMirror).ShouldBe("15");
        HasAssignedToUniqueName(unchangedMirror).ShouldBeFalse();
        using var unchangedPending = OpenReadOnly(pendingPath);
        ReadMigrationHistory(unchangedPending, scratch.History.IntentId!, attachedDurable: false).ShouldBe(expected);
        TargetRowCount(unchangedPending, attachedDurable: false).ShouldBe(retainTarget ? 1 : 0);
        ReadIntegrity(mirrorPath).ShouldBe("ok");
        if (!bypassStateConstraint)
            ReadIntegrity(pendingPath).ShouldBe("ok");
        await AssertOriginalGenerationUnchangedAsync(scratch);
    }

    private static async Task<ScratchFixture> CreateCancelledSchema15ScratchAsync()
    {
        var fixture = new NativeBrowserFixture();
        string? templateRoot = null;
        try
        {
            await fixture.InitializeAsync();
            var runtime = await fixture.CreateRuntimeAsync();
            var store = runtime.GetRequiredService<SqliteCacheStore>();
            var items = (SqliteWorkItemRepository)runtime.GetRequiredService<Twig.Domain.Interfaces.IWorkItemRepository>();
            await items.SaveAsync(NativeBrowserFixture.Item(42, "Native source item"));
            var context = runtime.GetRequiredService<Twig.Domain.Interfaces.IContextStore>();
            await context.SetValueAsync("source.preference", "unchanged");
            var migration = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!, phase =>
            {
                if (phase == "backup-qualified")
                    throw new IOException("Leave a preparing intent after a valid backup.");
            });
            var preview = migration.Preview();
            var interruption = await Should.ThrowAsync<IOException>(() => migration.ApplyAsync(preview.Digest));
            interruption.Message.ShouldBe("Leave a preparing intent after a valid backup.");
            var preparing = migration.Preview();
            preparing.IntentId.ShouldNotBeNull();
            migration.CancelPreparation(preview.Digest, preparing.IntentId!).State.ShouldBe("cancelled");

            var generation = store.ValidateAdmittedGeneration();
            generation.ShouldNotBeNull();
            var history = ReadMigrationHistory(store.GetConnection(), preparing.IntentId!);
            history.State.ShouldBe("cancelled");
            history.Slot.ShouldBeNull();
            TargetRowCount(store.GetConnection()).ShouldBe(0);
            new PreservedWorkspaceState(store).IsActive.ShouldBeFalse();
            var sourceMirror = fixture.CurrentPaths().DbPath;
            var sourcePending = SqliteCacheStore.DeriveDurableDataSource(sourceMirror);
            runtime.Dispose();
            SqliteConnection.ClearAllPools();

            templateRoot = CanonicalTempRoot.Create("twig-preservation-schema15-");
            var templateMirror = Path.Combine(templateRoot, "twig.db");
            var templatePending = Path.Combine(templateRoot, "pending.db");
            File.Copy(sourceMirror, templateMirror);
            File.Copy(sourcePending, templatePending);
            ConvertCopyToSchema15(templateMirror);
            return new ScratchFixture(fixture, templateRoot, templateMirror, templatePending, generation, history);
        }
        catch
        {
            if (templateRoot is not null && Directory.Exists(templateRoot))
                Directory.Delete(templateRoot, recursive: true);
            await fixture.DisposeAsync();
            throw;
        }
    }

    private static async Task AssertOriginalGenerationUnchangedAsync(ScratchFixture scratch)
    {
        var runtime = await scratch.Fixture.CreateRuntimeAsync(initializeBench: false);
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        store.ValidateAdmittedGeneration().ShouldBe(scratch.Generation);
        (await runtime.GetRequiredService<Twig.Domain.Interfaces.IWorkItemRepository>()
            .GetByIdAsync(42))!.Title.ShouldBe("Native source item");
        (await runtime.GetRequiredService<Twig.Domain.Interfaces.IContextStore>()
            .GetValueAsync("source.preference")).ShouldBe("unchanged");
        ReadMigrationHistory(store.GetConnection(), scratch.History.IntentId!).ShouldBe(scratch.History);
        TargetRowCount(store.GetConnection()).ShouldBe(0);
        scratch.Fixture.Transport.WorkMutationAttempts.ShouldBe(0);
        runtime.Dispose();
    }

    private static SqliteCacheStore OpenStore(string path) =>
        new(ConnectionString(path, SqliteOpenMode.ReadWrite));

    private static SqliteConnection OpenReadWrite(string path) =>
        Open(path, SqliteOpenMode.ReadWrite);

    private static SqliteConnection OpenReadOnly(string path) =>
        Open(path, SqliteOpenMode.ReadOnly);

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(ConnectionString(path, mode));
        connection.Open();
        SqliteCacheStore.RegisterCollations(connection);
        return connection;
    }

    private static string ConnectionString(string path, SqliteOpenMode mode)
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
        return builder.ToString();
    }

    private static string CopyTemplate(ScratchFixture scratch, string name)
    {
        var root = Path.Combine(scratch.TemplateRoot, name);
        Directory.CreateDirectory(root);
        File.Copy(scratch.TemplateMirror, Path.Combine(root, "twig.db"));
        File.Copy(scratch.TemplatePending, Path.Combine(root, "pending.db"));
        return root;
    }

    private static void ConvertCopyToSchema15(string path)
    {
        using (var mirror = OpenReadWrite(path))
        {
            HasAssignedToUniqueName(mirror).ShouldBeTrue();
            using var command = mirror.CreateCommand();
            command.CommandText = """
                ALTER TABLE work_items DROP COLUMN assigned_to_unique_name;
                UPDATE metadata SET value='15' WHERE key='schema_version';
                """;
            command.ExecuteNonQuery();
            ReadSchemaVersion(mirror).ShouldBe("15");
            HasAssignedToUniqueName(mirror).ShouldBeFalse();
        }
        ReadIntegrity(path).ShouldBe("ok");
    }

    private static string ReadSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM main.metadata WHERE key='schema_version';";
        return command.ExecuteScalar() as string
            ?? throw new InvalidOperationException("Mirror schema version is missing.");
    }

    private static bool HasAssignedToUniqueName(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('work_items') WHERE name='assigned_to_unique_name';";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static int TargetRowCount(SqliteConnection connection, bool attachedDurable = true)
    {
        var schema = attachedDurable ? "pending" : "main";
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                (SELECT COUNT(*) FROM {schema}.seed_local_identities)
              + (SELECT COUNT(*) FROM {schema}.seed_work_items)
              + (SELECT COUNT(*) FROM {schema}.workspace_context)
              + (SELECT COUNT(*) FROM {schema}.workspace_navigation_history)
              + (SELECT COUNT(*) FROM {schema}.sqlite_sequence WHERE name='workspace_navigation_history');
            """;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void UpdateMigrationHistory(
        SqliteConnection connection, string intentId, string state, int? slot, bool bypassStateConstraint)
    {
        if (bypassStateConstraint)
        {
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA ignore_check_constraints=ON;";
            pragma.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE main.preservation_migrations SET state=$state,slot=$slot WHERE intent_id=$intent;";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$slot", slot is null ? DBNull.Value : slot.Value);
        command.Parameters.AddWithValue("$intent", intentId);
        command.ExecuteNonQuery().ShouldBe(1);
    }

    private static MigrationHistory ReadMigrationHistory(
        SqliteConnection connection, string intentId, bool attachedDurable = true)
    {
        var schema = attachedDurable ? "pending" : "main";
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT intent_id,slot,source_generation,source_digest,state,coverage_version,backup_directory
            FROM {schema}.preservation_migrations WHERE intent_id=$intent;
            """;
        command.Parameters.AddWithValue("$intent", intentId);
        using var reader = command.ExecuteReader();
        reader.Read().ShouldBeTrue();
        return new MigrationHistory(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetString(6));
    }

    private static string ReadIntegrity(string path)
    {
        using var connection = OpenReadOnly(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return command.ExecuteScalar() as string
            ?? throw new InvalidOperationException("SQLite integrity_check returned no result.");
    }

    private sealed class ScratchFixture(
        NativeBrowserFixture fixture,
        string templateRoot,
        string templateMirror,
        string templatePending,
        string generation,
        MigrationHistory history) : IAsyncDisposable
    {
        internal NativeBrowserFixture Fixture { get; } = fixture;
        internal string TemplateRoot { get; } = templateRoot;
        internal string TemplateMirror { get; } = templateMirror;
        internal string TemplatePending { get; } = templatePending;
        internal string Generation { get; } = generation;
        internal MigrationHistory History { get; } = history;

        public async ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(TemplateRoot))
                Directory.Delete(TemplateRoot, recursive: true);
            await Fixture.DisposeAsync();
        }
    }

    private sealed record MigrationHistory(
        string IntentId,
        int? Slot,
        string Generation,
        string Digest,
        string State,
        int CoverageVersion,
        string BackupDirectory);
}
