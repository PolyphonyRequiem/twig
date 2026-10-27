using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Plan;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.GitHub;

/// <summary>
/// Executes only reviewed native relationship shapes. Observation equality is a preflight
/// check, not GitHub CAS; authorization and crash recovery belong to the proposal journal.
/// </summary>
internal sealed class GitHubNativeRelationshipService(
    IGitHubTokenSource credentials,
    GitHubBoundTargetResolver targets,
    GitHubWorkspaceScope scope,
    TwigPaths paths,
    IExternalIdentityMap identities,
    ILocalWorkItemStore localItems,
    string? account,
    GitHubTransportOverride? transport = null,
    string credentialSource = "gh") : IPlanOriginProvider
{
    internal static bool Supports(PlanOperationDefinition operation) =>
        operation is ProviderMutationOperation
        {
            Concurrency: "observed-non-atomic",
            OperationKind: PlanOperationKind.AddLink or PlanOperationKind.RemoveLink,
            Relation: "parent" or "predecessor" or "successor",
            Other: not null, Fields: null,
        };

    public async Task<PlanOrigin> GetOriginAsync(CancellationToken ct = default)
    {
        using var session = await CaptureAsync(ct).ConfigureAwait(false);
        return Origin(session.Reads);
    }

    internal async Task<PlanExecutionResult> ExecuteAsync(
        ProviderMutationOperation operation, PlanOrigin? expectedOrigin, CancellationToken ct)
    {
        if (credentialSource != "gh")
            return PlanExecutionResult.Failure(
                "Native GitHub writes are unavailable for this credential source; read-only admission is unchanged.");
        if (!Supports(operation))
            return PlanExecutionResult.Failure(
                "GitHub relationships require explicit observed-non-atomic concurrency; exact-revision writes are unavailable.");

        Session session;
        GitHubIssueGraphQlRequest shape;
        try
        {
            shape = GitHubRelationshipMutations.Create(operation,
                scope.GraphQlEndpoint.GetLeftPart(UriPartial.Authority).ToLowerInvariant());
            session = await CaptureAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsObservationFailure(ex))
        {
            return PlanExecutionResult.Failure("Native relationship admission unavailable; no write attempted: " + SafeReason(ex));
        }
        using (session)
        {
            try
            {
                if (expectedOrigin is null || Origin(session.Reads) != expectedOrigin)
                    return PlanExecutionResult.Failure("Proposal target, attachment, credential selection or authenticated actor changed; no write attempted.");
                await ValidateMappingAsync(operation.Target.Item, ct).ConfigureAwait(false);
                await ValidateMappingAsync(operation.Other!, ct).ConfigureAwait(false);
                // Peer membership is checked first so the final native read is the token preflight.
                await session.Reads.FetchSnapshotAsync(operation.Other!.ExternalIdentity, ct).ConfigureAwait(false);
                var before = await session.Reads.FetchSnapshotAsync(operation.Target.Item.ExternalIdentity, ct).ConfigureAwait(false);
                if (before.RevisionToken != operation.Target.ExpectedProviderRevision)
                    return PlanExecutionResult.Failure("Observed item token changed; no write attempted. Review a fresh proposal.");
                await targets.ValidateCapturedSessionAsync(session.Reads.Target, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsObservationFailure(ex))
            {
                return PlanExecutionResult.Failure("Native preflight unavailable; no write attempted: " + SafeReason(ex));
            }

            // Only this private path possesses the captured bearer credential. The caller
            // cannot provide a URL, document, headers, or arbitrary variables.
            using var http = new HttpClient(transport?.CreateHandler()
                ?? new SocketsHttpHandler { AllowAutoRedirect = false });
            using var request = new HttpRequestMessage(HttpMethod.Post, scope.GraphQlEndpoint);
            request.Headers.UserAgent.ParseAdd("twig-cli");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            request.Content = new StringContent(JsonSerializer.Serialize(shape,
                TwigJsonContext.Default.GitHubIssueGraphQlRequest), Encoding.UTF8, "application/json");
            try
            {
                using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return PlanExecutionResult.Indeterminate(
                        $"GitHub relationship response HTTP {(int)response.StatusCode}; no redirect or retry. Native readback is required.");
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                var root = body.RootElement;
                var name = (operation.OperationKind == PlanOperationKind.AddLink ? "add" : "remove")
                    + (operation.Relation == "parent" ? "SubIssue" : "BlockedBy");
                if (root.ValueKind != JsonValueKind.Object
                    || root.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1)
                    || (root.TryGetProperty("errors", out var errors)
                        && !(errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() == 0))
                    || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                    || data.EnumerateObject().Count() != 1
                    || !data.TryGetProperty(name, out var payload) || payload.ValueKind != JsonValueKind.Object)
                    return PlanExecutionResult.Indeterminate(
                        "GitHub relationship acknowledgment is incomplete; no retry. Native readback is required.");
                return PlanExecutionResult.Success("""{"nativeAcknowledged":true,"concurrency":"observed-non-atomic"}""");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException)
            {
                return PlanExecutionResult.Indeterminate(
                    "GitHub relationship response unavailable; the write may have landed. No retry; native readback is required.");
            }
        }
    }

    internal async Task<PlanReadbackOutcome> ReadbackAsync(
        ProviderMutationOperation operation, PlanOrigin? expectedOrigin, CancellationToken ct)
    {
        if (!Supports(operation))
            return PlanReadbackOutcome.Indeterminate("Unsupported native relationship readback.");
        try
        {
            using var session = await CaptureAsync(ct).ConfigureAwait(false);
            if (expectedOrigin is null || Origin(session.Reads) != expectedOrigin)
                return PlanReadbackOutcome.Indeterminate("Original target or authenticated actor changed; native effect remains unresolved.");
            var item = await session.Reads.FetchSnapshotAsync(operation.Target.Item.ExternalIdentity, ct).ConfigureAwait(false);
            var peer = await session.Reads.FetchSnapshotAsync(operation.Other!.ExternalIdentity, ct).ConfigureAwait(false);
            var edge = ExpectedEdge(operation);
            var expectedPresence = operation.OperationKind == PlanOperationKind.AddLink;
            if (!item.RelationshipsComplete || !peer.RelationshipsComplete)
                return PlanReadbackOutcome.Indeterminate("Native relationship readback is incomplete.");
            if (item.Relationships.Contains(edge) != expectedPresence
                || peer.Relationships.Contains(edge) != expectedPresence)
                return PlanReadbackOutcome.Failed("Native relationship readback does not confirm the requested direction and presence.");
            return PlanReadbackOutcome.VerifiedWith(
                """{"nativeReadback":true,"concurrency":"observed-non-atomic"}""");
        }
        catch (Exception ex) when (IsObservationFailure(ex))
        {
            return PlanReadbackOutcome.Indeterminate("Native relationship readback unavailable: " + SafeReason(ex));
        }
    }

    private async Task ValidateMappingAsync(ProviderWorkItemReference item, CancellationToken ct)
    {
        if (await identities.FindAsync(item.ExternalIdentity, ct).ConfigureAwait(false) != item.LocalId)
            throw new InvalidOperationException("The provider endpoint no longer matches its durable local identity.");
        if (await localItems.GetAsync(item.LocalId, ct).ConfigureAwait(false) is not { IsDeleted: false })
            throw new InvalidOperationException("A native relationship requires two existing live local identities.");
    }

    private PlanOrigin Origin(GitHubCapturedReadSession session) => new(
        paths.RepoRoot, session.Target.WorktreeFingerprint, session.Target.AttachmentRevision,
        session.Target.Identity.ResourceScope, session.Target.BindingId.ToString(), session.Target.BindingRevision,
        "provider-target-attachment", session.Target.AttachmentRevision,
        session.Actor.ActorId, "github-" + credentialSource, account ?? string.Empty,
        string.Empty, string.Empty, string.Empty, string.Empty, null)
    {
        ProviderAuthority = new(session.Target.Identity, session.Actor.ActorId,
            account ?? string.Empty, scope.GraphQlEndpoint.AbsoluteUri),
    };

    private async Task<Session> CaptureAsync(CancellationToken ct)
    {
        var target = await targets.ResolveAsync(ct).ConfigureAwait(false);
        var token = await credentials.GetTokenAsync(ct).ConfigureAwait(false);
        var reads = await new GitHubCapturedReadSessionFactory(
            new CapturedToken(token), targets, scope, account, transport).CaptureAsync(ct).ConfigureAwait(false);
        if (reads.Target != target)
        {
            reads.Dispose();
            throw new InvalidOperationException("Provider target changed while capturing relationship authority.");
        }
        return new(reads, token);
    }

    private static ProviderWorkItemRelationship ExpectedEdge(ProviderMutationOperation operation) =>
        operation.Relation switch
        {
            "parent" => new(operation.Other!.ExternalIdentity, operation.Target.Item.ExternalIdentity, LinkKind.ParentChild),
            "predecessor" => new(operation.Other!.ExternalIdentity, operation.Target.Item.ExternalIdentity, LinkKind.PredecessorSuccessor),
            "successor" => new(operation.Target.Item.ExternalIdentity, operation.Other!.ExternalIdentity, LinkKind.PredecessorSuccessor),
            _ => throw new InvalidOperationException("Unsupported native relationship."),
        };

    private static bool IsObservationFailure(Exception ex) =>
        ex is InvalidOperationException or ArgumentException or IOException or InvalidDataException
            or HttpRequestException or JsonException or GitHubCredentialUnavailableException;

    // Transport/credential exception messages can carry provider details; never journal them.
    private static string SafeReason(Exception ex) => ex switch
    {
        HttpRequestException => "provider HTTP observation failed",
        JsonException => "provider returned incomplete native evidence",
        _ => "target, credential or identity evidence is unavailable",
    };

    private sealed class CapturedToken(string token) : IGitHubTokenSource
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(token);
        }
    }

    private sealed class Session(GitHubCapturedReadSession reads, string token) : IDisposable
    {
        internal GitHubCapturedReadSession Reads { get; } = reads;
        internal string Token { get; } = token;
        public void Dispose() => Reads.Dispose();
    }
}
