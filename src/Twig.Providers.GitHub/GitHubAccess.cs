using System.Collections.Immutable;
using System.Net.Http.Headers;
using Twig.Core;

namespace Twig.Providers.GitHub;

public enum GitHubCredentialSource { Gh, GitHubApp }
public enum GitHubValidationScope { Unspecified, NativeApiAndDeclaredTwigPolicies }

public abstract class GitHubAccess
{
    public abstract Task<Result<GitHubSession>> AcquireAsync(Connection connection, CancellationToken ct);
}

// The host holds the selected attachment, credential-acquisition context and actor lease.
// No ambient credential lookup, token-only admission or automatic grant is provided.
public abstract class GitHubSession : IDisposable
{
    public abstract Connection Connection { get; }
    public abstract Uri Endpoint { get; }
    public abstract string ActorId { get; }
    public abstract string AcquisitionContext { get; }
    public abstract GitHubCredentialSource CredentialSource { get; }
    public abstract AuthenticationHeaderValue Authorization { get; }
    public abstract Task ValidateAsync(CancellationToken ct);
    public abstract Task<Result<GitHubRuleProjection>> ReadRuleProjectionAsync(ProcessSchema nativeProjection, CancellationToken ct);
    public abstract Task<Result<GitHubWriteAdmission>> AdmitAsync(GitHubWriteIntent intent, CancellationToken ct);
    public abstract void Dispose();
}

// The scope is a product validation boundary, not authority over external repository automation.
// The host supplies actual configured Twig policy evidence; native protocol facts come from discovery.
public sealed record GitHubRuleProjection(Connection Connection, string ActorId, string NativeFingerprint,
    GitHubValidationScope Scope, string PolicyIdentity, string PolicyRevision,
    ImmutableDictionary<string, ImmutableArray<FieldRule>> Types);

public sealed record GitHubWriteIntent(Proposal Proposal, int Operation, Guid Attempt, Uri Target,
    string Method, string MediaType, string Payload)
{
    public static Result<(string Mutation, string Payload)> Project(Proposal proposal, int ordinal, Guid attempt)
    {
        try
        {
            if (proposal.Connection.Provider.Value != "github" || ordinal < 0 || ordinal >= proposal.Operations.Length)
                return Result<(string, string)>.Fail(ErrorCode.InvalidInput, "An exact GitHub proposal operation is required.");
            var destination = Wire.Parse(System.Text.Encoding.UTF8.GetBytes(proposal.Connection.Destination));
            if (destination.ValueKind != System.Text.Json.JsonValueKind.Array || destination.GetArrayLength() != 2
                || GitHubProvider.Destination(destination[0].GetString()!, destination[1].GetString()!) != proposal.Connection.Destination)
                return Result<(string, string)>.Fail(ErrorCode.InvalidInput, "Canonical immutable GitHub destination required.");
            var repository = Wire.Id(destination[1].GetString()!);
            var op = proposal.Operations[ordinal];
            if (op.Origin != RelationshipOrigin.Provider || op.Concurrency != ConcurrencyMode.ObservedNonAtomic
                || op.Kind == OpKind.Batch && (op.Fields.Count == 0 || op.Type is not null || op.Body is not null)
                || op.Kind == OpKind.Create && (!op.Item.IsLocal || op.Type is null || op.Other is not null || op.Resource is not null || !op.Links.IsEmpty)
                || op.Kind == OpKind.Comment && op.Body?.Kind != FieldKind.Markdown
                || op.Kind is OpKind.Relate or OpKind.Unrelate && op.Other is null)
                return Result<(string, string)>.Fail(ErrorCode.Unsupported, "Unsupported one-request native GitHub effect.");
            string Id(ItemKey key) => key.BelongsTo(proposal.Connection) ? Wire.Id(key.NativeId!)
                : throw Wire.Refuse(ErrorCode.InvalidInput, "Exact GitHub resource scope and native node ID required.");
            var mutation = op.Kind switch
            {
                OpKind.Create => "createIssue", OpKind.Batch => "updateIssue", OpKind.Comment => "addComment",
                OpKind.Relate or OpKind.Unrelate when op.Relation is GitHubProvider.Hierarchy or GitHubProvider.Dependency
                    => (op.Kind == OpKind.Relate ? "add" : "remove") + (op.Relation == GitHubProvider.Hierarchy ? "SubIssue" : "BlockedBy"),
                _ => throw Wire.Refuse(ErrorCode.Unsupported, "Unsupported native effect."),
            };
            var inputType = char.ToUpperInvariant(mutation[0]) + mutation[1..] + "Input";
            var result = op.Kind switch
            {
                OpKind.Create or OpKind.Batch => "issue { id repository { id owner { id } } }",
                OpKind.Comment => "commentEdge { node { id } }", _ => "",
            };
            var document = $"mutation($input: {inputType}!) {{ {mutation}(input: $input) {{ clientMutationId {result} }} }}";
            var payload = Wire.Request(document, json =>
            {
                json.WriteStartObject("input"); json.WriteString("clientMutationId", attempt.ToString("D"));
                if (op.Kind == OpKind.Create) json.WriteString("repositoryId", repository);
                else if (op.Kind == OpKind.Batch) json.WriteString("id", Id(op.Item));
                else if (op.Kind == OpKind.Comment) json.WriteString("subjectId", Id(op.Item));
                else if (op.Relation == GitHubProvider.Hierarchy)
                {
                    json.WriteString("issueId", Id(op.Item)); json.WriteString("subIssueId", Id(op.Other!));
                    if (op.Kind == OpKind.Relate) json.WriteBoolean("replaceParent", false);
                }
                else { json.WriteString("issueId", Id(op.Other!)); json.WriteString("blockingIssueId", Id(op.Item)); }
                if (op.Kind == OpKind.Create && op.Type != GitHubProvider.Untyped) json.WriteString("issueTypeId", op.Type);
                foreach (var (field, value) in op.Fields)
                {
                    if (op.Kind == OpKind.Create && field == GitHubProvider.StateField) continue;
                    json.WritePropertyName(GitHubProvider.NativeField(field));
                    if (value.Kind == FieldKind.Null) json.WriteNullValue();
                    else if (value.Kind == FieldKind.StringList)
                    {
                        json.WriteStartArray(); foreach (var id in value.Values) json.WriteStringValue(id); json.WriteEndArray();
                    }
                    else json.WriteStringValue(value.Text);
                }
                if (op.Kind == OpKind.Comment) json.WriteString("body", op.Body!.Text);
                json.WriteEndObject();
            });
            return Result<(string, string)>.Ok((mutation, Wire.Utf8(payload)));
        }
        catch (GitHubFailure ex) { return Result<(string, string)>.Fail(ex.Error); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        { return Result<(string, string)>.Fail(ErrorCode.InvalidInput, "Malformed native GitHub projection: " + ex.Message); }
    }
}

public abstract class GitHubWriteAdmission : IDisposable
{
    public abstract Task ObserveAsync(int statusCode, string body, CancellationToken ct);
    public abstract void Dispose();
}
