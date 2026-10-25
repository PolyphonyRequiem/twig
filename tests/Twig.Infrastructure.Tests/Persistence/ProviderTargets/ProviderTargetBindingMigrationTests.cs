using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Claims;
using Twig.Domain.Services.Attachment;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence.ProviderTargets;

public sealed class ProviderTargetBindingMigrationTests : IDisposable
{
    private readonly string _directory;
    private readonly string _dbPath;

    public ProviderTargetBindingMigrationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "twig-provider-target-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _dbPath = Path.Combine(_directory, "system.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task V9_upgrade_adds_only_target_table_and_preserves_existing_system_rows()
    {
        var now = DateTimeOffset.UnixEpoch;
        using (var registry = new SqliteSystemWorktreeRegistry(_dbPath, TimeProvider.System))
        {
            (await registry.UpsertConnectionAsync("connection-ref", "org", "project", null)).IsSuccess.ShouldBeTrue();
            (await registry.UpsertWorktreeAsync("worktree-fingerprint", "connection-ref", "/checkout")).IsSuccess.ShouldBeTrue();
            (await registry.InsertClaimAsync("claim-id", "connection-ref", "worktree-fingerprint",
                PrimaryScopeKinds.AdoWorkItem, 42, "active", "cas-token", "{}")).IsSuccess.ShouldBeTrue();
            var identity = new IdentityRow("identity-id", "identity-name", "tenant-id", "object-id",
                "issuer", "authority", "credential-ref", null, now, now);
            (await registry.InsertIdentityAsync(identity)).IsSuccess.ShouldBeTrue();
            var binding = new BindingRow("ado-binding-id", "connection-ref", "identity-id", 1, now, now);
            (await registry.InsertOrGetBindingAsync(binding)).IsSuccess.ShouldBeTrue();
            (await registry.UpsertDefaultBindingAsync(
                new DefaultBindingRow("connection-ref", "ado-binding-id", 1, now))).IsSuccess.ShouldBeTrue();
        }

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE provider_target_bindings;
                UPDATE layout_meta SET version = 9 WHERE id = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        using var reopened = new SqliteSystemWorktreeRegistry(_dbPath, TimeProvider.System);
        var worktree = await reopened.FindWorktreeAsync("worktree-fingerprint");
        worktree.IsSuccess.ShouldBeTrue(worktree.Error);
        worktree.Value.ShouldNotBeNull();
        worktree.Value!.ConnectionRef.ShouldBe("connection-ref");
        (await reopened.FindClaimAsync("claim-id")).Value!.CasToken.ShouldBe("cas-token");
        (await reopened.FindIdentityByIdAsync("identity-id")).Value!.CredentialRef.ShouldBe("credential-ref");
        (await reopened.FindBindingByIdAsync("ado-binding-id")).Value!.IdentityId.ShouldBe("identity-id");
        (await reopened.FindDefaultBindingAsync("connection-ref")).Value!.BindingId.ShouldBe("ado-binding-id");

        var newTarget = await reopened.GetOrCreateAsync(
            new ProviderTargetIdentity("github", "https://api.github.com", "github.repository/v1", "repo:42:73"),
            null, null);
        newTarget.IsSuccess.ShouldBeTrue(newTarget.Error);

        await using var verification = new SqliteConnection($"Data Source={_dbPath}");
        await verification.OpenAsync();
        await using var verifyCommand = verification.CreateCommand();
        verifyCommand.CommandText = "SELECT version, (SELECT COUNT(*) FROM provider_target_bindings) FROM layout_meta WHERE id = 1;";
        await using var reader = await verifyCommand.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetInt32(0).ShouldBe(10);
        reader.GetInt32(1).ShouldBe(1);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task Supported_legacy_schema_upgrades_preserve_existing_rows_and_reach_v10(int version)
    {
        var now = DateTimeOffset.UnixEpoch;
        using (var registry = new SqliteSystemWorktreeRegistry(_dbPath, TimeProvider.System))
        {
            (await registry.UpsertConnectionAsync("legacy-connection", "org", "project", null)).IsSuccess.ShouldBeTrue();
            (await registry.UpsertWorktreeAsync("legacy-fingerprint", "legacy-connection", "/legacy-checkout")).IsSuccess.ShouldBeTrue();
            (await registry.InsertClaimAsync("legacy-claim", "legacy-connection", "legacy-fingerprint",
                PrimaryScopeKinds.AdoWorkItem, 314, "active", "legacy-cas", "{}")).IsSuccess.ShouldBeTrue();
            if (version >= 4)
            {
                var identity = new IdentityRow("legacy-identity", "legacy-name", "tenant", "object",
                    "issuer", "authority", "legacy-credential", null, now, now);
                (await registry.InsertIdentityAsync(identity)).IsSuccess.ShouldBeTrue();
                (await registry.InsertOrGetBindingAsync(
                    new BindingRow("legacy-binding", "legacy-connection", "legacy-identity", 1, now, now))).IsSuccess.ShouldBeTrue();
            }
        }

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = BuildLegacySchema(version);
            await command.ExecuteNonQueryAsync();
        }

        using var upgraded = new SqliteSystemWorktreeRegistry(_dbPath, TimeProvider.System);
        var worktree = await upgraded.FindWorktreeAsync("legacy-fingerprint");
        worktree.IsSuccess.ShouldBeTrue(worktree.Error);
        worktree.Value!.ConnectionRef.ShouldBe("legacy-connection");
        var claim = await upgraded.FindClaimAsync("legacy-claim");
        claim.IsSuccess.ShouldBeTrue(claim.Error);
        claim.Value!.CasToken.ShouldBe("legacy-cas");
        if (version >= 4)
        {
            (await upgraded.FindIdentityByIdAsync("legacy-identity")).Value!.CredentialRef.ShouldBe("legacy-credential");
            (await upgraded.FindBindingByIdAsync("legacy-binding")).Value!.IdentityId.ShouldBe("legacy-identity");
        }
        var target = await upgraded.GetOrCreateAsync(
            new ProviderTargetIdentity("github", "https://api.github.com", "github.repository/v1", $"repo:legacy-{version}"),
            null, null);
        target.IsSuccess.ShouldBeTrue(target.Error);

        await using var verification = new SqliteConnection($"Data Source={_dbPath}");
        await verification.OpenAsync();
        await using var verifyCommand = verification.CreateCommand();
        verifyCommand.CommandText = "SELECT version FROM layout_meta WHERE id = 1;";
        (await verifyCommand.ExecuteScalarAsync()).ShouldBe(10L);
    }

    [Fact]
    public async Task Newer_schema_version_is_rejected_without_dropping_provider_target_table()
    {
        using (var registry = new SqliteSystemWorktreeRegistry(_dbPath, TimeProvider.System))
            (await registry.GetOrCreateAsync(
                new ProviderTargetIdentity("github", "https://api.github.com", "github.repository/v1", "repo:rollback"),
                null, null)).IsSuccess.ShouldBeTrue();

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE layout_meta SET version = 11 WHERE id = 1;";
            await command.ExecuteNonQueryAsync();
        }

        using var reopened = new SqliteSystemWorktreeRegistry(_dbPath, TimeProvider.System);
        var read = await reopened.FindByIdentityAsync(
            new ProviderTargetIdentity("github", "https://api.github.com", "github.repository/v1", "repo:rollback"));
        read.IsSuccess.ShouldBeFalse();
        read.Error.ShouldBe(Twig.Domain.Services.Attachment.AttachmentStorageFailure.SystemStoreSchemaMismatch);

        await using var verification = new SqliteConnection($"Data Source={_dbPath}");
        await verification.OpenAsync();
        await using var verifyCommand = verification.CreateCommand();
        verifyCommand.CommandText = "SELECT version FROM layout_meta WHERE id = 1;";
        (await verifyCommand.ExecuteScalarAsync()).ShouldBe(11L);
        verifyCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='provider_target_bindings';";
        (await verifyCommand.ExecuteScalarAsync()).ShouldBe(1L);
    }

    private static string BuildLegacySchema(int version)
    {
        var sql = new List<string>
        {
            "DROP TABLE provider_target_bindings;",
        };
        if (version <= 8)
            sql.Add("DROP TABLE connection_default_transition_members; DROP TABLE connection_default_transitions;");
        if (version <= 7)
            sql.Add("DROP TABLE connection_remote_write_receipts; DROP TABLE connection_remote_write_observations; DROP TABLE connection_remote_write_intents;");
        if (version <= 6)
            sql.Add("DROP TABLE connection_binding_transitions;");
        if (version <= 5)
            sql.Add("DROP TABLE connection_migrations;");
        if (version <= 4)
            sql.Add("DROP TABLE pat_principals;");
        if (version <= 3)
            sql.Add("DROP TABLE connection_defaults; DROP TABLE connection_bindings; DROP TABLE identities;");
        sql.Add($"UPDATE layout_meta SET version = {version} WHERE id = 1;");
        return string.Join(Environment.NewLine, sql);
    }
}
