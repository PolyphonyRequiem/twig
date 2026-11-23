using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Twig.Core;
using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.Services;

public sealed record UserRepositoryBindings(string RepositoryId, long Revision,
    ImmutableArray<Connection> Connections, Guid? DefaultBench, string RepositoryRoot)
{
    [JsonIgnore] public Connection? DeclaredConnection { get; init; }
    [JsonIgnore] public string? DeclaredSourceStatus { get; init; }
    [JsonIgnore] public string? DeclaredSourceLabel { get; init; }
    public bool AttachmentsConfigured { get; init; }
    public ImmutableArray<Guid> AttachedBenches { get; init; } = [];
    public ImmutableArray<Connection> ExcludedConnections { get; init; } = [];
    public ImmutableDictionary<Guid, string> SetupReceipts { get; init; } = ImmutableDictionary<Guid, string>.Empty;
}

public sealed partial class UserBenchStore
{
    private const int RepositorySetupReceiptLimit = 512;

    public async Task<Result<UserRepositoryBindings>> ReadRepositoryAsync(string root, CancellationToken ct = default)
    {
        try
        {
            var repository = RepositoryIdentity(root);
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path))
                return Result<UserRepositoryBindings>.Ok(await ProjectDeclaration(
                    new(repository.Id, 0, [], null, repository.Root), null, null, ct).ConfigureAwait(false));
            await using var db = await Open(ct, readOnly: true).ConfigureAwait(false);
            using var transaction = db.BeginTransaction(deferred: true);
            var stored = await ReadRepository(db, transaction, repository.Id, repository.Root, ct).ConfigureAwait(false);
            return Result<UserRepositoryBindings>.Ok(await ProjectDeclaration(stored, db, transaction, ct).ConfigureAwait(false));
        }
        catch (ArgumentException ex) { return Result<UserRepositoryBindings>.Fail(ErrorCode.InvalidInput, ex.Message); }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<UserRepositoryBindings>(ex); }
    }

    public async Task<Result<UserRepositoryBindings>> SaveRepositoryAsync(string root, long expectedRevision,
        IEnumerable<Connection> connections, Guid? defaultBench, CancellationToken ct = default,
        Guid[]? attachedBenches = null, Connection[]? excludedConnections = null,
        Guid? expectedBenchId = null, long? expectedBenchRevision = null, UserBenchWorkspace? expectedWorkspace = null,
        Guid? operationId = null, string? operationDigest = null)
    {
        try
        {
            var repository = RepositoryIdentity(root);
            var selected = connections.ToImmutableArray();
            var attached = attachedBenches?.ToImmutableArray();
            var excluded = excludedConnections?.ToImmutableArray();
            if (expectedRevision < 0 || expectedRevision == long.MaxValue || selected.Length > 64
                || selected.Distinct().Count() != selected.Length || defaultBench == Guid.Empty
                || (attached is { } ids && (ids.Length > 64 || ids.Contains(Guid.Empty)
                    || ids.Distinct().Count() != ids.Length || (defaultBench is { } chosen && !ids.Contains(chosen))))
                || operationId == Guid.Empty || (operationId is null) != (operationDigest is null)
                || (operationDigest is not null && (operationDigest.Length != 64 || !operationDigest.All(Uri.IsHexDigit)))
                || (excluded is { } denied && (denied.Length > 64 || denied.Distinct().Count() != denied.Length
                    || denied.Intersect(selected).Any())))
                return Result<UserRepositoryBindings>.Fail(ErrorCode.InvalidInput, "Choose distinct attachments and connection decisions, and a default from the attachments.");
            await using var db = await Open(ct).ConfigureAwait(false);
            using var transaction = db.BeginTransaction(deferred: false);
            var current = await ReadRepository(db, transaction, repository.Id, repository.Root, ct).ConfigureAwait(false);
            if (operationId is { } replay && current.SetupReceipts.TryGetValue(replay, out var digest))
                return digest == operationDigest ? Result<UserRepositoryBindings>.Ok(current)
                    : Result<UserRepositoryBindings>.Fail(ErrorCode.Conflict, "The setup operation ID is already bound to different intent.");
            if (operationId is not null && current.SetupReceipts.Count >= RepositorySetupReceiptLimit)
                return Result<UserRepositoryBindings>.Fail(ErrorCode.Unavailable, "The repository setup receipt limit was reached; no setup was recorded.");
            if (current.Revision != expectedRevision)
                return Result<UserRepositoryBindings>.Fail(ErrorCode.Conflict, "The repository bindings changed. Read the current bindings before editing them.");
            if (expectedWorkspace is { } exact)
            {
                if (expectedBenchId is not { } target || !current.AttachedBenches.Contains(target)
                    || !selected.ToHashSet().SetEquals(current.Connections.Append(exact.Connection))
                    || attached is not null || excluded is not null || defaultBench != current.DefaultBench
                    || current.ExcludedConnections.Contains(exact.Connection)
                    || await Find(db, transaction, target, ct).ConfigureAwait(false) is not { Deleted: false } stored
                    || stored.Bench.Revision != expectedBenchRevision
                    || (!stored.Bench.Connections.Contains(exact.Connection)
                        && (await ProjectDeclaration(current, db, transaction, ct).ConfigureAwait(false)).DeclaredConnection != exact.Connection))
                    return Result<UserRepositoryBindings>.Fail(ErrorCode.Conflict, "The attached Bench or connection decision changed during admission.");
                await using var verify = db.CreateCommand();
                verify.Transaction = transaction;
                verify.CommandText = "SELECT root, store_instance FROM workspaces WHERE connection=$connection;";
                verify.Parameters.AddWithValue("$connection", JsonSerializer.Serialize(exact.Connection, UserBenchJson.Default.Connection));
                await using var candidates = await verify.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await candidates.ReadAsync(ct).ConfigureAwait(false)
                    || candidates.GetString(0) != exact.Root || !Guid.TryParse(candidates.GetString(1), out var store)
                    || store != exact.StoreInstance || await candidates.ReadAsync(ct).ConfigureAwait(false))
                    return Result<UserRepositoryBindings>.Fail(ErrorCode.Conflict, "The exact workspace registration changed during admission.");
            }
            var nextAttached = attached ?? current.AttachedBenches;
            var nextExcluded = excluded ?? current.ExcludedConnections;
            var configured = attached.HasValue || current.AttachmentsConfigured;
            if (nextExcluded.Intersect(selected).Any()
                || (configured && defaultBench is { } nextDefault && !nextAttached.Contains(nextDefault)))
                return Result<UserRepositoryBindings>.Fail(ErrorCode.InvalidInput, "An excluded connection cannot be assigned, and a default must be attached.");
            if (current.Revision != 0 && current.AttachmentsConfigured == configured
                && current.DefaultBench == defaultBench && current.Connections.SequenceEqual(selected)
                && current.AttachedBenches.SequenceEqual(nextAttached) && current.ExcludedConnections.SequenceEqual(nextExcluded))
                return Result<UserRepositoryBindings>.Ok(current);
            foreach (var connection in selected)
            {
                await using var registered = db.CreateCommand();
                registered.Transaction = transaction;
                registered.CommandText = "SELECT 1 FROM workspaces WHERE connection=$connection LIMIT 1;";
                registered.Parameters.AddWithValue("$connection", JsonSerializer.Serialize(connection, UserBenchJson.Default.Connection));
                if (await registered.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
                    return Result<UserRepositoryBindings>.Fail(ErrorCode.NotFound, "Assign only existing registered connections; repository bindings cannot enroll credentials or replace a source.");
            }
            foreach (var id in nextAttached.Concat(defaultBench is { } legacyDefault ? [legacyDefault] : []).Distinct())
                if (await Find(db, transaction, id, ct).ConfigureAwait(false) is not { Deleted: false })
                    return Result<UserRepositoryBindings>.Fail(ErrorCode.NotFound, "Choose existing Benches.");
            var next = new UserRepositoryBindings(repository.Id, expectedRevision + 1, selected, defaultBench, repository.Root)
            {
                AttachmentsConfigured = configured, AttachedBenches = nextAttached, ExcludedConnections = nextExcluded,
                SetupReceipts = operationId is { } applied
                    ? current.SetupReceipts.Add(applied, operationDigest!)
                    : current.SetupReceipts,
            };
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
            || value.Connections.Distinct().Count() != value.Connections.Length || value.DefaultBench == Guid.Empty
            || value.AttachedBenches.IsDefault || value.AttachedBenches.Length > 64 || value.AttachedBenches.Contains(Guid.Empty)
            || value.AttachedBenches.Distinct().Count() != value.AttachedBenches.Length
            || (!value.AttachmentsConfigured && value.AttachedBenches.Length != 0)
            || (value.AttachmentsConfigured && value.DefaultBench is { } id && !value.AttachedBenches.Contains(id))
            || value.ExcludedConnections.IsDefault || value.ExcludedConnections.Length > 64
            || value.ExcludedConnections.Distinct().Count() != value.ExcludedConnections.Length
            || value.ExcludedConnections.Intersect(value.Connections).Any()
            || value.SetupReceipts is null || value.SetupReceipts.Count > RepositorySetupReceiptLimit
            || value.SetupReceipts.Any(pair => pair.Key == Guid.Empty || pair.Value is not { Length: 64 } || !pair.Value.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Invalid repository binding identity, revision or connections.");
        return value with { RepositoryRoot = repositoryRoot };
    }
}
