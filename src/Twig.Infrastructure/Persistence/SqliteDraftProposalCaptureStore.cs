using System.Text;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Plan;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence;

/// <summary>Local exact capture using the draft and journal's shared pending.db connection.</summary>
internal sealed class SqliteDraftProposalCaptureStore(SqliteCacheStore store) : IDraftProposalCaptureStore
{
    public Task<PlanJournal> CaptureAsync(
        RecoverableWorkItemDraft expectedDraft,
        WorkItemSnapshot expectedSnapshot,
        PlanOrigin origin,
        PlanWorkspace workspace,
        string proposalDirectory,
        CancellationToken ct = default)
        => CaptureCoreAsync(expectedDraft, expectedSnapshot, origin, workspace, proposalDirectory, null, ct);

    public Task<PlanJournal> CaptureObservedAsync(RecoverableWorkItemDraft expectedDraft,
        WorkItemSnapshot expectedSnapshot, PlanOrigin origin, PlanWorkspace workspace,
        string proposalDirectory, string evidenceFingerprint, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceFingerprint);
        return CaptureCoreAsync(expectedDraft, expectedSnapshot, origin, workspace, proposalDirectory, evidenceFingerprint, ct);
    }

    private async Task<PlanJournal> CaptureCoreAsync(RecoverableWorkItemDraft expectedDraft,
        WorkItemSnapshot expectedSnapshot, PlanOrigin origin, PlanWorkspace workspace,
        string proposalDirectory, string? evidenceFingerprint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expectedDraft);
        ArgumentNullException.ThrowIfNull(expectedSnapshot);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalDirectory);
        var key = expectedDraft.ItemKey;
        if (key.WorkItemId <= 0 || expectedSnapshot.Id != key.WorkItemId || expectedSnapshot.IsSeed
            || expectedSnapshot.IsDirty || expectedSnapshot.Revision <= 0 || expectedDraft.HasConflicts
            || expectedDraft.BaselineRevision != expectedSnapshot.Revision || expectedDraft.Changes.Count == 0
            || key.CheckoutRoot != origin.WorktreeRoot || key.ConnectionId != origin.ConnectionRef
            || key.BindingId != origin.BindingId)
            throw new InvalidOperationException("Capture requires exact qualified, clean, conflict-free published intent.");
        if (expectedDraft.Changes.Select(change => change.FieldRef).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != expectedDraft.Changes.Count)
            throw new InvalidOperationException("Capture refuses duplicate field identities.");
        ct.ThrowIfCancellationRequested();

        var operationId = "draft-" + IdentityDigest(expectedDraft, expectedSnapshot.Revision, origin, workspace, evidenceFingerprint);
        var parsed = new PlanDocumentParser().Parse(WriteProposal(expectedDraft, workspace, operationId));
        if (!parsed.IsValid || parsed.Plan is null || parsed.CanonicalJson is null || parsed.Digest is null)
            throw new InvalidOperationException("Native draft capture did not produce a valid proposal: "
                + string.Join("; ", parsed.Issues.Select(issue => issue.Message)));
        var capture = new DraftProposalCapture(evidenceFingerprint is null ? 1 : 2, expectedDraft,
            expectedSnapshot.Revision, origin, operationId, parsed.Digest, evidenceFingerprint);
        var sourcePath = Path.Combine(Path.GetFullPath(proposalDirectory), $"draft-{parsed.Digest}.json");

        using var operation = store.AcquireOperation();
        if (store.ActiveTransaction is not null)
            throw new InvalidOperationException("Draft capture must own its non-deferred transaction.");
        // BEGIN IMMEDIATE fences cross-process writers BEFORE any draft/snapshot read.
        // Busy/locked fails normally; no retry, snapshot upgrade, or silent rebase.
        using var transaction = store.GetConnection().BeginTransaction(deferred: false);
        store.ActiveTransaction = transaction;
        try
        {
            ct.ThrowIfCancellationRequested();
            var actualDraft = await new SqliteRecoverableWorkItemDraftStore(store).GetAsync(key, ct).ConfigureAwait(false);
            if (actualDraft is null || DraftJson(actualDraft) != DraftJson(expectedDraft))
                throw new InvalidOperationException("Draft identity, generation, baseline, conflicts, or effects changed. Capture refused.");
            var actualSnapshot = await new SqliteWorkItemRepository(store, new WorkItemMapper())
                .GetSnapshotByIdAsync(key.WorkItemId, ct).ConfigureAwait(false);
            if (actualSnapshot is null || SnapshotJson(actualSnapshot) != SnapshotJson(expectedSnapshot))
                throw new InvalidOperationException("Cached native observation changed. Capture refused.");
            if (evidenceFingerprint is not null)
                await new SqliteDraftCaptureEvidenceStore(store).ValidateAsync(actualDraft, actualSnapshot,
                    origin, evidenceFingerprint, ct).ConfigureAwait(false);
            using (var pending = store.GetConnection().CreateCommand())
            {
                pending.Transaction = transaction;
                pending.CommandText = "SELECT EXISTS(SELECT 1 FROM pending.pending_changes);";
                if (Convert.ToInt64(pending.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
                    throw new InvalidOperationException("Capture refuses pending changes; nothing was flushed.");
            }

            var journals = new SqlitePlanJournalRepository(store);
            var retained = await journals.GetAsync(parsed.Digest, ct).ConfigureAwait(false);
            PlanJournal result;
            if (retained is not null)
            {
                SqlitePlanJournalRepository.AssertCaptureMatch(retained, capture, parsed.CanonicalJson, sourcePath);
                result = retained;
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                result = await journals.ImportCaptureAsync(parsed.Plan, parsed.CanonicalJson, parsed.Digest,
                    sourcePath, DateTimeOffset.UtcNow, capture, ct).ConfigureAwait(false);
                if (evidenceFingerprint is not null) journals.SetCaptureExportState(parsed.Digest, complete: false);
            }
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return result;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
        finally
        {
            store.ActiveTransaction = null;
        }
    }

    internal static string IdentityDigest(
        RecoverableWorkItemDraft draft, int revision, PlanOrigin origin, PlanWorkspace workspace,
        string? evidenceFingerprint = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("captureVersion", evidenceFingerprint is null ? 1 : 2);
            if (evidenceFingerprint is not null) writer.WriteString("evidenceFingerprint", evidenceFingerprint);
            writer.WritePropertyName("draft");
            JsonSerializer.Serialize(writer, draft, TwigJsonContext.Default.RecoverableWorkItemDraft);
            writer.WriteNumber("currentRevision", revision);
            writer.WritePropertyName("origin");
            JsonSerializer.Serialize(writer, origin, TwigJsonContext.Default.PlanOrigin);
            WriteWorkspace(writer, workspace);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return PlanCanonicalizer.Canonicalize(document.RootElement).Digest;
    }

    private static string WriteProposal(RecoverableWorkItemDraft draft, PlanWorkspace workspace, string operationId)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            WriteWorkspace(writer, workspace);
            writer.WriteStartArray("operations");
            writer.WriteStartObject();
            writer.WriteString("id", operationId);
            writer.WriteString("kind", "batch");
            writer.WriteNumber("workItemId", draft.ItemKey.WorkItemId);
            writer.WriteNumber("expectedRevision", draft.BaselineRevision);
            writer.WriteStartObject("fields");
            foreach (var change in draft.Changes)
                writer.WriteString(change.FieldRef, change.ProposedValue);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteWorkspace(Utf8JsonWriter writer, PlanWorkspace workspace)
    {
        writer.WriteStartObject("workspace");
        writer.WriteString("organization", workspace.Organization);
        writer.WriteString("project", workspace.Project);
        writer.WriteEndObject();
    }

    private static string DraftJson(RecoverableWorkItemDraft draft)
        => PlanCanonicalizer.CanonicalizeToString(
            JsonSerializer.SerializeToElement(draft, TwigJsonContext.Default.RecoverableWorkItemDraft));

    internal static string SnapshotJson(WorkItemSnapshot snapshot)
        => PlanCanonicalizer.CanonicalizeToString(
            JsonSerializer.SerializeToElement(snapshot, TwigJsonContext.Default.WorkItemSnapshot));
}
