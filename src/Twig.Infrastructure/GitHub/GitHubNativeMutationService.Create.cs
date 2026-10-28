using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Plan;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.GitHub;

internal sealed partial class GitHubNativeMutationService
{
    private static bool ValidCreate(ProviderCreateOperation operation) =>
        operation.CreationId != Guid.Empty && operation.Concurrency == "observed-non-atomic"
        && operation.Destination.ProviderKind == "github"
        && operation.Fields.TryGetValue("System.Title", out var title) && !string.IsNullOrWhiteSpace(title)
        && operation.Fields.Keys.All(k => k is "System.Title" or "System.Description");

    internal async Task<PlanExecutionResult> ExecuteCreateAsync(
        ProviderCreateOperation operation, PlanOrigin? expectedOrigin, CancellationToken ct)
    {
        if (!ValidCreate(operation) || credentialSource is not ("gh" or "github-app"))
            return PlanExecutionResult.Failure("Unsupported native creation shape or credential source; no alternate source was used.");
        Session session;
        try
        {
            session = await CaptureAsync(ct).ConfigureAwait(false);
            if (expectedOrigin is null || Origin(session.Reads) != expectedOrigin
                || operation.Destination != session.Reads.Target.Identity)
            {
                session.Dispose();
                return PlanExecutionResult.Failure("Creation destination or original authority changed; no write attempted.");
            }
            try
            {
                await targets.ValidateCapturedSessionAsync(session.Reads.Target, ct).ConfigureAwait(false);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (IsObservationFailure(ex))
        {
            return PlanExecutionResult.Failure("Native creation admission unavailable; no write attempted: " + SafeReason(ex));
        }
        using (session)
        {
            var fence = CreationFence(operation);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fence)!);
                // This is a local cooperating-writer fence, not GitHub idempotency or a
                // web/third-party lock. Once durable, even a cancelled attempt is not replayed.
                using var stream = new FileStream(fence, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough);
                WriteFence(stream, operation, expectedOrigin!, nativeId: null);
                stream.Flush(flushToDisk: true);
            }
            catch (IOException)
            {
                return PlanExecutionResult.Indeterminate("Creation identity is already fenced or its durable fence is unavailable. No POST was retried; preserve the original journal.");
            }
            catch (UnauthorizedAccessException)
            {
                return PlanExecutionResult.Failure("Cannot persist the creation fence; no write attempted.");
            }

            var result = await SendNativeAsync(session, "createIssue", "CreateIssueInput", "issue", writer =>
            {
                writer.WriteString("repositoryId", session.Reads.Target.Target.RepositoryId);
                writer.WriteString("title", operation.Fields["System.Title"]);
                if (operation.Fields.TryGetValue("System.Description", out var body))
                    writer.WriteString("body", body ?? string.Empty);
            }, expectedId: null, ct).ConfigureAwait(false);
            if (result.Outcome != PlanExecutionOutcome.Applied)
                return result;

            using var acknowledgment = JsonDocument.Parse(result.ResultJson!);
            var id = acknowledgment.RootElement.GetProperty("nativeId").GetString()!;
            var temporary = fence + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    WriteFence(stream, operation, expectedOrigin!, id);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, fence, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new PlanExecutionResult(PlanExecutionOutcome.Indeterminate, result.ResultJson,
                    "Creation acknowledged but durable identity recovery is unavailable; no retry.", null, null);
            }
            finally
            {
                // A failed acknowledgment write leaves the original fence in place.
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return result;
        }
    }

    internal async Task<PlanReadbackOutcome> ReadbackCreateAsync(
        ProviderCreateOperation operation, PlanOrigin? expectedOrigin, string? acknowledged, CancellationToken ct)
    {
        try
        {
            using var session = await CaptureAsync(ct).ConfigureAwait(false);
            if (expectedOrigin is null || Origin(session.Reads) != expectedOrigin
                || operation.Destination != session.Reads.Target.Identity)
                return PlanReadbackOutcome.Indeterminate("Original creation destination or actor changed; preserve its fence and journal.");
            string? id = null;
            if (acknowledged is not null)
            {
                using var result = JsonDocument.Parse(acknowledged);
                if (result.RootElement.TryGetProperty("nativeId", out var native)) id = native.GetString();
            }
            using var fence = JsonDocument.Parse(await File.ReadAllTextAsync(CreationFence(operation), ct).ConfigureAwait(false));
            var root = fence.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("intent", out var intent) || intent.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("nativeId", out var fencedId)
                || fencedId.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return PlanReadbackOutcome.Indeterminate("Creation fence is incomplete; preserve it and the original journal without replay.");
            if (intent.GetString() != CreationIntent(operation, expectedOrigin))
                return PlanReadbackOutcome.Indeterminate("Creation identity belongs to different immutable intent; no replay or guessed reconciliation.");
            if (id is not null && fencedId.ValueKind == JsonValueKind.String && id != fencedId.GetString())
                return PlanReadbackOutcome.Indeterminate("Creation acknowledgment contradicts the durable native identity; preserve both without replay.");
            id ??= fencedId.GetString();
            if (!NativeId(id))
                return PlanReadbackOutcome.Indeterminate("Creation may have landed without an attributable native identity. Preserve the durable fence; never repeat POST or infer identity from a matching title.");
            var identity = new ExternalWorkItemIdentity("github", operation.Destination.ResourceScope, id!);
            var snapshot = await session.Reads.FetchSnapshotAsync(identity, ct).ConfigureAwait(false);
            if (!HasNativeObservation(snapshot))
                return PlanReadbackOutcome.Indeterminate("Created issue timestamp observation is incomplete; preserve the creation fence.");
            if (!FieldsMatch(snapshot, operation.Fields))
                return PlanReadbackOutcome.Failed("Created issue readback does not match the reviewed initial fields; creation remains fenced.");
            var local = await identities.ResolveAsync(identity, snapshot.RevisionToken, ct).ConfigureAwait(false);
            return PlanReadbackOutcome.VerifiedWith(CreateReadbackResult(id!, local.Id));
        }
        catch (Exception ex) when (IsObservationFailure(ex) || ex is UnauthorizedAccessException)
        {
            return PlanReadbackOutcome.Indeterminate("Native creation readback unavailable; preserve the fence: " + SafeReason(ex));
        }
    }

    private string CreationFence(ProviderCreateOperation operation) =>
        Path.Combine(paths.TwigDir, "github-create-fences", operation.CreationId.ToString("D") + ".json");

    private static string CreationIntent(ProviderCreateOperation operation, PlanOrigin origin)
    {
        var canonical = new PlanDocumentParser().Parse(PlanDocumentWriter.Write(new()
        {
            Version = 2, Workspace = new() { Organization = "github", Project = "creation" },
            Operations = [operation with { Id = "create" }],
        })).CanonicalJson!;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            canonical + "\n" + JsonSerializer.Serialize(origin, TwigJsonContext.Default.PlanOrigin)))).ToLowerInvariant();
    }

    private static void WriteFence(Stream stream, ProviderCreateOperation operation, PlanOrigin origin, string? nativeId)
    {
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("intent", CreationIntent(operation, origin));
        writer.WriteString("nativeId", nativeId);
        writer.WriteEndObject();
    }

    private static string CreateReadbackResult(string id, LocalWorkItemId localId)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("nativeId", id);
            writer.WriteString("localId", localId.ToString());
            writer.WriteString("concurrency", "observed-non-atomic");
            writer.WriteBoolean("nativeReadback", true);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
