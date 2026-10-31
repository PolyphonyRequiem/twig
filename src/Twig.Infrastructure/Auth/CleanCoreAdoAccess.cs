using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Twig.Core;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Policy;
using Twig.Providers.Ado;
using Twig.Store.Sqlite;

namespace Twig.Infrastructure.Auth;

/// <summary>Retains native binding/generation admission across one clean-core provider operation.</summary>
internal sealed class CleanCoreAdoAccess(IAuthenticationProvider authentication, IConnectionBindingService bindings,
    TwigConfiguration configuration, TwigPaths paths, ResolvedConnectionBinding origin, Connection connection,
    string principal, IStore store) : AdoAccess
{
    private readonly IAuthenticationProvider _authentication = authentication;
    private readonly ResolvedConnectionBinding _origin = origin;
    private readonly Connection _connection = connection;
    private readonly string _principal = principal;
    private readonly IStore _store = store;
    private readonly TwigPaths _paths = paths;
    private readonly Guid _storeInstance = (store as SqliteStore)?.InstanceId ?? Guid.Empty;

    internal static string Identity(ResolvedConnectionBinding origin, string principal) => Json(writer =>
    {
        writer.WriteStartArray();
        writer.WriteStringValue(origin.Binding.BindingId);
        writer.WriteStringValue(origin.Identity.IdentityId);
        writer.WriteStringValue(origin.Identity.Method);
        writer.WriteStringValue(principal);
        writer.WriteEndArray();
    });

    public override async Task<Result<AdoSession>> AcquireAsync(Connection requested, CancellationToken ct)
    {
        if (requested != _connection || _connection.Provider.Value != "ado" || _storeInstance == Guid.Empty
            || _connection.IdentityRef != Identity(_origin, _principal)
            || configuration.Organization != _origin.Operation.Organization || configuration.Project != _origin.Operation.Project
            || _authentication is not IConnectionOperationGuard guard
            || _authentication is not IBoundAuthenticationMetadata
            || _authentication is not IConnectionRemoteWriteGuard)
            return Result<AdoSession>.Fail(ErrorCode.Conflict, "A native bound operation and immutable actor binding are required.");
        IDisposable? lease = null;
        try
        {
            lease = await guard.AcquireOperationAsync(ct).ConfigureAwait(false);
            if (lease is not IAdmittedConnectionOperation admitted
                || admitted.GetAuthority(bindings) is not { } observed
                || !ConnectionRemoteWriteAdmission.SameAuthority(observed, _origin))
                return Refuse("The native binding/generation lease does not belong to this runtime.");
            await RevalidateAsync(ct).ConfigureAwait(false);
            var token = await _authentication.GetAccessTokenAsync(ct).ConfigureAwait(false);
            var authorization = token.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? AuthenticationHeaderValue.Parse(token) : new AuthenticationHeaderValue("Bearer", token);
            var session = new Session(this, lease, token, authorization);
            lease = null;
            return Result<AdoSession>.Ok(session);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException
            or Twig.Infrastructure.Ado.Exceptions.AdoAuthenticationException)
        {
            return Refuse("Native connection/identity admission is unavailable; no fallback credential or storage generation was used.");
        }
        finally { lease?.Dispose(); }

        static Result<AdoSession> Refuse(string reason) => Result<AdoSession>.Fail(ErrorCode.Unavailable, reason);
    }

    private async Task RevalidateAsync(CancellationToken ct)
    {
        var currentConfiguration = await TwigConfiguration.LoadSplitAsync(_paths, ct).ConfigureAwait(false);
        var current = await bindings.ResolveAsync(currentConfiguration, _paths, ct).ConfigureAwait(false);
        if (!ConnectionRemoteWriteAdmission.SameAuthority(current, _origin)
            || current.Operation.Organization != _origin.Operation.Organization
            || current.Operation.Project != _origin.Operation.Project
            || current.Operation.Team != _origin.Operation.Team
            || current.Operation.AttachmentRevision != _origin.Operation.AttachmentRevision)
            throw new InvalidOperationException("clean-core-binding-changed: the original native checkout authority is no longer selected.");
    }

    private sealed class Session(CleanCoreAdoAccess access, IDisposable lease, string token,
        AuthenticationHeaderValue authorization) : AdoSession
    {
        private int _disposed;
        public override Connection Connection => access._connection;
        public override Uri OrganizationUrl => new(Twig.Infrastructure.Ado.AdoRestClient.NormalizeOrgUrl(access._origin.Operation.Organization));
        public override string PrincipalId => access._principal;
        public override AuthenticationHeaderValue Authorization => authorization;

        public override async Task<Result<AdoWriteAdmission>> AdmitAsync(AdoWriteIntent intent, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return Result<AdoWriteAdmission>.Fail(ErrorCode.Conflict, "The native operation lease has been released.");
            var proposal = intent.Proposal;
            if (proposal.Connection != Connection || intent.Operation < 0 || intent.Operation >= proposal.Operations.Length
                || intent.Attempt == Guid.Empty || intent.MediaType != "application/json-patch+json")
                return Result<AdoWriteAdmission>.Fail(ErrorCode.Conflict, "The exact retained proposal operation and attempt are required.");
            var snapshot = await access._store.ReadAsync(Connection, ct).ConfigureAwait(false);
            if (!snapshot.IsSuccess) return Result<AdoWriteAdmission>.Fail(snapshot.Error);
            var row = snapshot.Value.Journal.SingleOrDefault(j => j.Digest == proposal.Digest && j.Operation == intent.Operation);
            if (!snapshot.Value.Proposals.Any(p => p.Digest == proposal.Digest)
                || row is not { Status: OpStatus.Applying } || row.Attempt != intent.Attempt)
                return Result<AdoWriteAdmission>.Fail(ErrorCode.Conflict, "Native admission requires this exact durably Applying attempt.");
            var op = proposal.Operations[intent.Operation];
            var kind = op.Kind switch
            {
                OpKind.Create => "workitem-create",
                OpKind.Batch or OpKind.Comment => "workitem-patch",
                OpKind.Relate or OpKind.LinkResource => "link-add",
                OpKind.Unrelate => "link-remove",
                _ => null,
            };
            if (kind is null || intent.Method != (op.Kind == OpKind.Create ? "POST" : "PATCH"))
                return Result<AdoWriteAdmission>.Fail(ErrorCode.Unsupported, "This operation has no qualified native transport.");
            var prefix = OrganizationUrl.AbsolutePath.TrimEnd('/') + "/" + Connection.Destination + "/_apis/wit/workitems/";
            if (!ConnectionRemoteWriteAdmission.IsWithinAuthority(access._origin, intent.Target)
                || intent.Target.Query != "?api-version=7.1"
                || !intent.Target.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || op.Kind != OpKind.Create && ConnectionRemoteWriteAdmission.TargetWorkItemId(intent.Target.AbsoluteUri)?.ToString(CultureInfo.InvariantCulture) != op.Item.NativeId
                || !HasReviewedRevision(intent.Payload, op))
                return Result<AdoWriteAdmission>.Fail(ErrorCode.Conflict, "The native request target or revision test differs from the retained effect.");
            await access.RevalidateAsync(ct).ConfigureAwait(false);
            var policy = await new ConfiguredPolicyProjectionSource(access._paths.RepoRoot).ReadAsync(ct).ConfigureAwait(false);
            if (!policy.IsSuccess) return Result<AdoWriteAdmission>.Fail(policy.Error);
            if (proposal.PolicyIdentity != policy.Value.Identity || proposal.PolicyRevision != policy.Value.Revision)
                return Result<AdoWriteAdmission>.Fail(ErrorCode.SchemaChanged,
                    "The actual configured Twig policy appeared, disappeared or changed after preview.");
            var correlation = new CoreWriteCorrelation(ConnectionText(Connection), proposal.Digest, intent.Operation,
                intent.Attempt, ItemText(op.Item), op.Kind == OpKind.Create ? op.Item.LocalId : null,
                op.ExpectedRevision?.Token, access._storeInstance);
            var admission = await ((IConnectionRemoteWriteGuard)access._authentication).BeginRemoteWriteAsync(
                new(kind, intent.Method, intent.Target.AbsoluteUri, intent.Payload, null, CoreCorrelation: correlation), ct).ConfigureAwait(false);
            return Result<AdoWriteAdmission>.Ok(new Admission(admission, token, authorization.Parameter));
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) lease.Dispose();
        }
    }

    private sealed class Admission(IConnectionRemoteWriteAdmission admission, string token, string? parameter) : AdoWriteAdmission
    {
        public override Task ObserveAsync(int statusCode, string body, CancellationToken ct)
            => admission.RecordResponseAsync(new(statusCode,
                ConnectionOperationAdmission.RedactCredential(body, token, parameter), null, null), ct);
        public override void Dispose() { }
    }

    private static bool HasReviewedRevision(string payload, Op op)
    {
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
        if (op.Kind == OpKind.Create) return op.Item.IsLocal && op.Item.LocalId != Guid.Empty && op.ExpectedRevision is null;
        if (op.ExpectedRevision?.Token is not { } token || !token.StartsWith("ado-rev:", StringComparison.Ordinal)
            || !int.TryParse(token.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            || revision <= 0 || token != "ado-rev:" + revision.ToString(CultureInfo.InvariantCulture)
            || document.RootElement.GetArrayLength() == 0)
            return false;
        var test = document.RootElement[0];
        return test.ValueKind == JsonValueKind.Object
            && test.TryGetProperty("op", out var verb) && verb.ValueKind == JsonValueKind.String && verb.GetString() == "test"
            && test.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String && path.GetString() == "/rev"
            && test.TryGetProperty("value", out var value) && value.TryGetInt32(out var observed) && observed == revision;
    }

    private static string ConnectionText(Connection value) => Json(writer =>
    {
        writer.WriteStartArray(); writer.WriteStringValue(value.Provider.Value); writer.WriteStringValue(value.ResourceScope);
        writer.WriteStringValue(value.Destination); writer.WriteStringValue(value.IdentityRef); writer.WriteEndArray();
    });

    private static string ItemText(ItemKey value) => Json(writer =>
    {
        writer.WriteStartArray(); writer.WriteStringValue(value.IsLocal ? value.LocalId.ToString("D") : null);
        writer.WriteStringValue(value.Provider?.Value); writer.WriteStringValue(value.ResourceScope);
        writer.WriteStringValue(value.NativeId); writer.WriteEndArray();
    });

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
