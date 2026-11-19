using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Core;
using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.Services;

public sealed record UserRepositoryBindings(string RepositoryId, long Revision,
    ImmutableArray<Connection> Connections, Guid? DefaultBench, string RepositoryRoot);

public sealed partial class UserBenchStore
{
    public async Task<Result<UserRepositoryBindings>> ReadRepositoryAsync(string root, CancellationToken ct = default)
    {
        try
        {
            var repository = RepositoryIdentity(root);
            await using var db = await Open(ct).ConfigureAwait(false);
            return Result<UserRepositoryBindings>.Ok(await ReadRepository(db, null, repository.Id, repository.Root, ct).ConfigureAwait(false));
        }
        catch (ArgumentException ex) { return Result<UserRepositoryBindings>.Fail(ErrorCode.InvalidInput, ex.Message); }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<UserRepositoryBindings>(ex); }
    }

    public async Task<Result<UserRepositoryBindings>> SaveRepositoryAsync(string root, long expectedRevision,
        IEnumerable<Connection> connections, Guid? defaultBench, CancellationToken ct = default)
    {
        try
        {
            var repository = RepositoryIdentity(root);
            var selected = connections.ToImmutableArray();
            if (expectedRevision < 0 || expectedRevision == long.MaxValue || selected.Length > 64
                || selected.Distinct().Count() != selected.Length || defaultBench == Guid.Empty)
                return Result<UserRepositoryBindings>.Fail(ErrorCode.InvalidInput, "Use at most 64 distinct registered connections and an existing default Bench.");
            await using var db = await Open(ct).ConfigureAwait(false);
            using var transaction = db.BeginTransaction(deferred: false);
            var current = await ReadRepository(db, transaction, repository.Id, repository.Root, ct).ConfigureAwait(false);
            if (current.Revision != expectedRevision)
                return Result<UserRepositoryBindings>.Fail(ErrorCode.Conflict, "The repository bindings changed. Read the current bindings before editing them.");
            foreach (var connection in selected)
            {
                await using var registered = db.CreateCommand();
                registered.Transaction = transaction;
                registered.CommandText = "SELECT 1 FROM workspaces WHERE connection=$connection LIMIT 1;";
                registered.Parameters.AddWithValue("$connection", JsonSerializer.Serialize(connection, UserBenchJson.Default.Connection));
                if (await registered.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
                    return Result<UserRepositoryBindings>.Fail(ErrorCode.NotFound, "Assign only existing registered connections; repository bindings cannot enroll credentials or replace a source.");
            }
            if (defaultBench is { } id && await Find(db, transaction, id, ct).ConfigureAwait(false) is not { Deleted: false })
                return Result<UserRepositoryBindings>.Fail(ErrorCode.NotFound, "Choose an existing default Bench.");
            var next = new UserRepositoryBindings(repository.Id, expectedRevision + 1, selected, defaultBench, repository.Root);
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO repository_bindings(repository_key,revision,document) VALUES($key,$revision,$document)
                ON CONFLICT(repository_key) DO UPDATE SET revision=excluded.revision,document=excluded.document;
                """;
            command.Parameters.AddWithValue("$key", repository.Id);
            command.Parameters.AddWithValue("$revision", next.Revision);
            command.Parameters.AddWithValue("$document", JsonSerializer.Serialize(next, UserBenchJson.Default.UserRepositoryBindings));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return Result<UserRepositoryBindings>.Ok(next);
        }
        catch (ArgumentException ex) { return Result<UserRepositoryBindings>.Fail(ErrorCode.InvalidInput, ex.Message); }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<UserRepositoryBindings>(ex); }
    }

    private static (string Id, string Root) RepositoryIdentity(string root)
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root)
            || !WorktreeAnchorDetector.TryDetect(root, out var anchor, out _))
            throw new ArgumentException("Choose an absolute path inside an existing Git worktree.");
        if (!WorktreeAnchorDetector.TryRepositoryRoot(anchor, out var repositoryRoot))
            throw new DirectoryNotFoundException("The main repository checkout is unavailable. No worktree-specific repository controls were used.");
        return (OperatingSystem.IsWindows() ? anchor.GitCommonDir.ToUpperInvariant() : anchor.GitCommonDir, repositoryRoot);
    }

    private static async Task<UserRepositoryBindings> ReadRepository(SqliteConnection db, SqliteTransaction? transaction,
        string repository, string repositoryRoot, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision,document FROM repository_bindings WHERE repository_key=$key;";
        command.Parameters.AddWithValue("$key", repository);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return new(repository, 0, [], null, repositoryRoot);
        var json = reader.GetString(1);
        if (json.Length > 128 * 1024) throw new InvalidDataException("The repository bindings exceed their document bound.");
        var value = JsonSerializer.Deserialize(json, UserBenchJson.Default.UserRepositoryBindings)
            ?? throw new JsonException("Missing repository bindings.");
        if (value.RepositoryId != repository || value.Revision != reader.GetInt64(0) || value.Revision < 1
            || value.Connections.IsDefault || value.Connections.Length > 64
            || value.Connections.Distinct().Count() != value.Connections.Length || value.DefaultBench == Guid.Empty)
            throw new InvalidDataException("Invalid repository binding identity, revision or connections.");
        return value with { RepositoryRoot = repositoryRoot };
    }
}
