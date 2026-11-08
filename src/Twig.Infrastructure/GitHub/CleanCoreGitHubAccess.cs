using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Twig.Core;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Policy;
using Twig.Infrastructure.Services;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Providers.GitHub;
using Twig.Store.Sqlite;

namespace Twig.Infrastructure.GitHub;

internal sealed record CoreGitHubNativeIntent(Guid StoreInstance, Connection Connection, string Actor,
    string AcquisitionContext, string Digest, int Operation, Guid Attempt, string Method, string Target,
    string MediaType, string RequestHash, string Payload);
internal sealed record CoreGitHubNativeAcknowledgement(string RequestHash, int StatusCode, string Body);

/// <summary>Holds the selected native target generation and records attributed transport intent before dispatch.</summary>
internal sealed class CleanCoreGitHubAccess(TwigConfiguration configuration, TwigPaths paths,
    GitHubWorkspaceScope scope, GitHubBoundTargetResolver targets, GitHubBoundReadContext origin,
    IGitHubTokenSource credentials, Connection connection, string actor, SqliteStore store, string registryPath) : GitHubAccess
{
    private readonly TwigConfiguration _configuration = configuration;
    private readonly TwigPaths _paths = paths;
    private readonly GitHubWorkspaceScope _scope = scope;
    private readonly Connection _connection = connection;
    private readonly string _actor = actor;
    private readonly SqliteStore _store = store;
    private readonly string _registryPath = registryPath;
    internal static string Identity(GitHubBoundReadContext target, TwigConfiguration configuration, string actor)
        => JsonSerializer.Serialize(new[]
        {
            target.BindingId.ToString(), target.AttachmentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            target.WorktreeFingerprint, target.BindingRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            configuration.GitHubCredentialSource ?? "gh", configuration.GitHubAccount ?? "",
            configuration.GitHubAppClientId ?? "", configuration.GitHubOAuthIdentity ?? "", actor,
        }, CoreGitHubNativeJsonContext.Default.StringArray);

    public override async Task<Result<GitHubSession>> AcquireAsync(Connection requested, CancellationToken ct)
    {
        if (requested != _connection || _connection.Provider.Value != "github" || _store.InstanceId == Guid.Empty
            || _connection.IdentityRef != Identity(origin, _configuration, _actor)
            || _connection.Destination != GitHubProvider.Destination(origin.Target.OwnerId, origin.Target.RepositoryId)
            || _connection.ResourceScope != _scope.GraphQlEndpoint.GetLeftPart(UriPartial.Authority).ToLowerInvariant())
            return Result<GitHubSession>.Fail(ErrorCode.Conflict, "The immutable GitHub target, actor and store binding differ.");
        FileStream? generation = null;
        try
        {
            generation = new FileStream(Path.Combine(_paths.TwigDir, ProviderTargetAttachmentDocument.LockFileName),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await ValidateAsync(ct).ConfigureAwait(false);
            var token = await credentials.GetTokenAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
                return Result<GitHubSession>.Fail(ErrorCode.Unavailable, "The explicitly selected GitHub credential is unavailable.");
            await ValidateAsync(ct).ConfigureAwait(false);
            var state = await _store.ReadAsync(_connection, ct).ConfigureAwait(false);
            if (!state.IsSuccess) return Result<GitHubSession>.Fail(state.Error);
            var session = new Session(this, generation, token,
                state.Value.Journal.Where(row => row.Status == OpStatus.Applying).Select(row => row.Attempt).ToHashSet());
            generation = null;
            return Result<GitHubSession>.Ok(session);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or GitHubCredentialUnavailableException)
        {
            return Result<GitHubSession>.Fail(ErrorCode.Unavailable,
                "The selected GitHub target generation or credential could not be leased; no alternate source was used. " + ex.Message);
        }
        finally { generation?.Dispose(); }
    }

    private async Task ValidateAsync(CancellationToken ct)
    {
        await targets.ValidateCapturedSessionAsync(origin, ct).ConfigureAwait(false);
        var current = await TwigConfiguration.LoadSplitAsync(_paths, ct).ConfigureAwait(false);
        if (current.ResolveWorkItemProviderKind() != "github"
            || !GitHubWorkspaceScope.TryCreate(current.GitHub, out var selected, out _) || selected != _scope
            || Identity(origin, current, _actor) != _connection.IdentityRef)
            throw new InvalidOperationException("The selected GitHub target or credential acquisition context changed.");
        var pinned = await _store.CheckWorkspaceAsync(_connection, ct).ConfigureAwait(false);
        if (!pinned.IsSuccess) throw new InvalidOperationException(pinned.Error.Message);
    }

    private sealed class Session(CleanCoreGitHubAccess access, FileStream generation, string token,
        HashSet<Guid> previousAttempts) : GitHubSession
    {
        private int _disposed;
        public override Connection Connection => access._connection;
        public override Uri Endpoint => access._scope.GraphQlEndpoint;
        public override string ActorId => access._actor;
        public override string AcquisitionContext => Connection.IdentityRef;
        public override GitHubCredentialSource CredentialSource => access._configuration.GitHubCredentialSource switch
        {
            null or "gh" => GitHubCredentialSource.Gh,
            "github-app" => GitHubCredentialSource.GitHubApp,
            _ => throw new InvalidOperationException("Unsupported explicit GitHub credential source."),
        };
        public override AuthenticationHeaderValue Authorization => new("Bearer", token);
        public override async Task ValidateAsync(CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0) throw new InvalidOperationException("The GitHub target generation lease was released.");
            await access.ValidateAsync(ct).ConfigureAwait(false);
            var state = await access._store.ReadAsync(Connection, ct).ConfigureAwait(false);
            if (!state.IsSuccess) throw new InvalidOperationException(state.Error.Message);
            if (state.Value.Journal.Any(row => row.Status == OpStatus.Applying && !previousAttempts.Contains(row.Attempt)
                && state.Value.Proposals.Single(proposal => proposal.Digest == row.Digest).Operations[row.Operation].Origin == RelationshipOrigin.Local))
            {
                // Recheck newly acquired durable inputs before local CAS; older attempts do not block reads.
                var admitted = await NativeCoreExecution.PublishingAsync(access._registryPath, ct).ConfigureAwait(false);
                if (!admitted.IsSuccess) throw new InvalidOperationException(admitted.Error.Message);
            }
        }
        public override async Task<Result<GitHubRuleProjection>> ReadRuleProjectionAsync(ProcessSchema nativeProjection, CancellationToken ct)
        {
            await ValidateAsync(ct).ConfigureAwait(false);
            var captured = await new ConfiguredPolicyProjectionSource(access._paths.RepoRoot)
                .CaptureAsync(Connection, ActorId, nativeProjection, ct).ConfigureAwait(false);
            return captured.IsSuccess ? Result<GitHubRuleProjection>.Ok(captured.Value.ForGitHub())
                : Result<GitHubRuleProjection>.Fail(captured.Error);
        }
        public override async Task<Result<GitHubWriteAdmission>> AdmitAsync(GitHubWriteIntent intent, CancellationToken ct)
        {
            await ValidateAsync(ct).ConfigureAwait(false);
            if (intent.Proposal.Connection != Connection || intent.Operation < 0 || intent.Operation >= intent.Proposal.Operations.Length
                || intent.Attempt == Guid.Empty || intent.Target != Endpoint || intent.Method != "POST" || intent.MediaType != "application/json")
                return Result<GitHubWriteAdmission>.Fail(ErrorCode.Conflict, "The GitHub request does not belong to the exact retained target/attempt.");
            var snapshot = await access._store.ReadAsync(Connection, ct).ConfigureAwait(false);
            if (!snapshot.IsSuccess) return Result<GitHubWriteAdmission>.Fail(snapshot.Error);
            var row = snapshot.Value.Journal.SingleOrDefault(row => row.Digest == intent.Proposal.Digest && row.Operation == intent.Operation);
            var retained = snapshot.Value.Proposals.SingleOrDefault(proposal => proposal.Digest == intent.Proposal.Digest);
            if (retained is null
                || row is not { Status: OpStatus.Applying } || row.Attempt != intent.Attempt)
                return Result<GitHubWriteAdmission>.Fail(ErrorCode.Conflict, "GitHub admission requires this exact durably Applying operation and attempt.");
            var policy = await new ConfiguredPolicyProjectionSource(access._paths.RepoRoot).ReadAsync(ct).ConfigureAwait(false);
            if (!policy.IsSuccess) return Result<GitHubWriteAdmission>.Fail(policy.Error);
            if (intent.Proposal.PolicyIdentity != policy.Value.Identity || intent.Proposal.PolicyRevision != policy.Value.Revision)
                return Result<GitHubWriteAdmission>.Fail(ErrorCode.SchemaChanged, "The actual declared Twig policy changed after preview.");
            var op = intent.Proposal.Operations[intent.Operation];
            if (op.Concurrency != ConcurrencyMode.ObservedNonAtomic || op.Origin != RelationshipOrigin.Provider)
                return Result<GitHubWriteAdmission>.Fail(ErrorCode.Unsupported, "Only explicitly native observed-non-atomic GitHub effects are admitted.");
            var projected = GitHubWriteIntent.Project(retained, intent.Operation, intent.Attempt);
            if (!projected.IsSuccess || !Encoding.UTF8.GetBytes(projected.Value.Payload).AsSpan()
                .SequenceEqual(Encoding.UTF8.GetBytes(intent.Payload)))
                return Result<GitHubWriteAdmission>.Fail(ErrorCode.Conflict, "The exact native mutation document/inputs differ from the retained Core effect.");
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(intent.Payload)));
            var native = new CoreGitHubNativeIntent(access._store.InstanceId, Connection, ActorId, AcquisitionContext,
                intent.Proposal.Digest, intent.Operation, intent.Attempt, intent.Method, intent.Target.AbsoluteUri,
                intent.MediaType, hash, intent.Payload);
            var directory = Path.Combine(access._paths.TwigDir, "core-github-intents");
            var targets = await NativeCoreExecution.AdmitAsync(access._registryPath,
                new(access._store.InstanceId, access._store.DatabasePath, access._paths.RepoRoot, Connection),
                intent.Proposal, intent.Operation, intent.Attempt, ct).ConfigureAwait(false);
            if (!targets.IsSuccess) return Result<GitHubWriteAdmission>.Fail(targets.Error);
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, intent.Attempt.ToString("D") + ".json");
                await WriteNewAsync(path, native, CoreGitHubNativeJsonContext.Default.CoreGitHubNativeIntent, ct).ConfigureAwait(false);
                return Result<GitHubWriteAdmission>.Ok(new Admission(path + ".ack", hash, token, targets.Value));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                targets.Value.Dispose();
                return Result<GitHubWriteAdmission>.Fail(ErrorCode.StorageUnavailable,
                    "Native GitHub intent could not be durably recorded; dispatch was refused. " + ex.Message);
            }
            catch { targets.Value.Dispose(); throw; }
        }
        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) generation.Dispose();
        }
    }

    private sealed class Admission(string path, string requestHash, string token, IDisposable targets) : GitHubWriteAdmission
    {
        public override Task ObserveAsync(int statusCode, string body, CancellationToken ct)
            => WriteNewAsync(path, new CoreGitHubNativeAcknowledgement(requestHash, statusCode,
                body.Replace(token, "[redacted]", StringComparison.Ordinal)),
                CoreGitHubNativeJsonContext.Default.CoreGitHubNativeAcknowledgement, ct);
        public override void Dispose() => targets.Dispose();
    }

    private static async Task WriteNewAsync<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(file, value, type, ct).ConfigureAwait(false);
        await file.FlushAsync(ct).ConfigureAwait(false);
        file.Flush(flushToDisk: true);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CoreGitHubNativeIntent))]
[JsonSerializable(typeof(CoreGitHubNativeAcknowledgement))]
[JsonSerializable(typeof(string[]))]
internal partial class CoreGitHubNativeJsonContext : JsonSerializerContext;
