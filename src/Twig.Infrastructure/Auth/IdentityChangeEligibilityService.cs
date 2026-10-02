using System.Globalization;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Claims;
using Twig.Domain.Services.Plan;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Auth;

internal interface IIdentityChangeEligibilityService
{
    Task<IdentityChangeEligibility> InspectAsync(TwigConfiguration configuration, TwigPaths paths, CancellationToken ct = default);
}

/// <summary>Reads every native blocker. A clean snapshot is advisory; transition admission must revalidate and fence it.</summary>
internal sealed class IdentityChangeEligibilityService(
    ISystemWorktreeRegistry registry,
    IPrimaryScopeAttachmentStore attachment,
    IPendingChangeReader pendingReader,
    IWorkItemRepository workItems,
    IPublishIntentRepository publishIntents,
    IPlanJournalRepository planJournals,
    IConnectionBindingService bindings) : IIdentityChangeEligibilityService
{
    public async Task<IdentityChangeEligibility> InspectAsync(TwigConfiguration configuration, TwigPaths paths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);
        var guards = new List<WorktreeGuardBlocker>();
        var unknown = new List<UnknownRowBlocker>();
        var connection = ConnectionRefResolver.Compute(configuration);
        if (!WorktreeAnchorDetector.TryDetect(paths.StartDir ?? paths.TwigDir, out var anchor, out var failure))
            throw new InvalidOperationException($"Managed worktree inspection refused: {failure}. Attach explicitly with twig init.");
        var fingerprint = WorktreeFingerprintProvider.CanonicalJson(anchor);
        var initial = await attachment.ReadWithRevisionAsync(ct).ConfigureAwait(false);
        if (!initial.IsSuccess) throw new InvalidOperationException($"Attachment inspection refused: {initial.Error}");
        var document = initial.Value.Attachment;
        if (document.ConnectionRef != connection)
            guards.Add(new("attachment-connection-ref-mismatch", document.ConnectionRef, connection));
        var worktree = await registry.FindWorktreeAsync(fingerprint, ct).ConfigureAwait(false);
        if (!worktree.IsSuccess) unknown.Add(new("registry.worktree", worktree.Error!));
        else if (worktree.Value is not { } registered) guards.Add(new("worktree-not-registered", anchor.WorktreeRoot, "registered attached worktree"));
        else
        {
            if (registered.ConnectionRef != connection) guards.Add(new("registry-connection-ref-mismatch", registered.ConnectionRef, connection));
            if (registered.RetiredAt is { } retired) guards.Add(new("worktree-retired", retired.ToString("O", CultureInfo.InvariantCulture), "non-retired attachment"));
        }

        ResolvedConnectionBinding? selection = null;
        await ReadAsync("binding-selection", async () =>
        {
            var live = await TwigConfiguration.LoadSplitAsync(paths, ct).ConfigureAwait(false);
            if (ConnectionRefResolver.Compute(live) != connection)
            {
                guards.Add(new("portable-endpoint-changed", ConnectionRefResolver.Compute(live), connection));
                return;
            }
            selection = await bindings.ResolveAsync(live, paths, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var pending = new List<PendingEditBlocker>();
        await ReadAsync("pending", async () =>
        {
            foreach (var row in await pendingReader.GetAllChangesAsync(ct).ConfigureAwait(false))
            {
                pending.Add(new(row.PendingChangeId, row.WorkItemId, row.Kind, row.WorkItemId < 0));
                if (row.Kind is not ("field" or "state" or "set_field" or "note" or "add_note"))
                    unknown.Add(new("pending.kind", $"Pending row {row.PendingChangeId} has unrecognized kind '{row.Kind}'."));
                if (row.WorkItemId < 0 && row.SeedRemap is null)
                    unknown.Add(new("pending.seed-identity", $"Pending row {row.PendingChangeId} has no durable staged identity for alias {row.WorkItemId}."));
            }
        }).ConfigureAwait(false);
        var seeds = new List<LocalSeedBlocker>();
        await ReadAsync("seeds", async () =>
        {
            foreach (var seed in await workItems.GetSeedsAsync(ct).ConfigureAwait(false))
            {
                seeds.Add(new(seed.Id, seed.Title, seed.Type.Value));
                if (seed.StagedIdentity is null) unknown.Add(new("seed.identity", $"Unpublished alias {seed.Id} has no durable staged identity."));
            }
        }).ConfigureAwait(false);
        var intents = new List<OpenPublishIntentBlocker>();
        await ReadAsync("publish-intents", async () =>
        {
            foreach (var intent in await publishIntents.GetOpenIntentsAsync(ct).ConfigureAwait(false))
                intents.Add(new(intent.Identity.ToString(), intent.Title, intent.TypeName, intent.RecordedAt));
        }).ConfigureAwait(false);
        var journals = new List<UnresolvedJournalBlocker>();
        await ReadAsync("proposal-journals", async () =>
        {
            foreach (var journal in await planJournals.GetUnresolvedAsync(ct).ConfigureAwait(false))
                foreach (var operation in journal.Operations)
                    if (operation.State != PlanOperationState.Verified && operation.OutcomeReceipt is null)
                        journals.Add(new(journal.Digest, operation.OpId, journal.SourcePath, operation.State,
                            operation.Error ?? "Native outcome has not been established; preserve the original artifact."));
        }).ConfigureAwait(false);

        var claims = new List<ReservedClaimBlocker>();
        await ReadAsync("registry.claims", async () =>
        {
            var rows = await registry.FindClaimsForWorktreeAsync(fingerprint, ct).ConfigureAwait(false);
            if (!rows.IsSuccess) throw new InvalidOperationException(rows.Error);
            foreach (var row in rows.Value)
            {
                if (row.State is ClaimStates.Released or ClaimStates.Superseded) continue;
                if (row.State is not (ClaimStates.Pending or ClaimStates.Active))
                {
                    unknown.Add(new("claim.state", $"Claim {row.ClaimId} has unrecognized state '{row.State}'."));
                    continue;
                }
                claims.Add(new(row.ClaimId, row.MintedAt, row.State, row.WorkItemId, row.PrimaryScopeKind, row.CasToken));
                if (row.ConnectionRef != connection || row.WorktreeFingerprint != fingerprint)
                    unknown.Add(new("claim.tuple", $"Claim {row.ClaimId} does not match this connection/worktree tuple."));
                ValidateClaim(row);
            }
            if (document.ActiveClaim is { } active)
            {
                var row = rows.Value.FirstOrDefault(candidate => candidate.ClaimId == active.ClaimId);
                if (row is null)
                {
                    var referenced = await registry.FindClaimAsync(active.ClaimId, ct).ConfigureAwait(false);
                    unknown.Add(new("attachment.claim", referenced.IsSuccess && referenced.Value is not null
                        ? $"Attachment claim {active.ClaimId} belongs to another worktree tuple."
                        : $"Attachment claim {active.ClaimId} cannot be established from the native registry."));
                }
                else if (row.State != ClaimStates.Active || document.PrimaryScope is not { } scope
                    || row.WorkItemId != scope.WorkItemId || row.PrimaryScopeKind != PrimaryScopeKinds.AdoWorkItem)
                    unknown.Add(new("attachment.claim-tuple", $"Attachment claim {active.ClaimId} is not the active primary-scope tuple it references."));
            }
        }).ConfigureAwait(false);

        await ReadAsync("inspection-revision", async () =>
        {
            var final = await attachment.ReadWithRevisionAsync(ct).ConfigureAwait(false);
            if (!final.IsSuccess || final.Value.Revision != initial.Value.Revision)
                unknown.Add(new("attachment.revision", "Attachment changed or became unreadable during inspection; rerun before transition admission."));
            if (selection is not null)
            {
                var live = await TwigConfiguration.LoadSplitAsync(paths, ct).ConfigureAwait(false);
                var latest = await bindings.ResolveAsync(live, paths, ct).ConfigureAwait(false);
                if (latest.Binding != selection.Binding || latest.SelectionRevision != selection.SelectionRevision
                    || latest.SelectionSource != selection.SelectionSource || latest.Operation.WorktreeFingerprint != fingerprint)
                    unknown.Add(new("binding.revision", "Effective binding selection changed during inspection; rerun before transition admission."));
            }
        }).ConfigureAwait(false);

        var actions = new List<string>();
        if (pending.Count > 0) actions.Add("publish/discard separately: inspect twig pending -o json; explicitly review native publication or authorized discard. Inspection never flushes pending edits or notes.");
        if (seeds.Count > 0) actions.Add("publish/discard separately: use twig proposal seed to describe staged identities and an authorized publish-seed proposal, or explicitly authorize twig seed discard.");
        if (intents.Count > 0 || journals.Count > 0) actions.Add("reconcile separately: inspect original digest/operation/intent evidence with twig proposal status; use explicit native reconciliation or lease-aware resume under original authority. Unproved outcomes remain blocked.");
        if (claims.Count > 0) actions.Add("claim lifecycle separately: resolve reserved or active claim ownership/compatibility through its ordinary authorized lifecycle; inspection never releases, supersedes or remints claims.");
        if (guards.Count > 0 || unknown.Count > 0 || selection is null) actions.Add("repair/inspect separately: restore valid attachment and explicit binding selection, establish unknown native evidence, then rerun. There is no force bypass.");
        if (actions.Count == 0) actions.Add("Common prerequisites are clear at the observed revisions. A transition still requires its own authorization, fresh eligibility check and native operation/CAS fence.");
        return new IdentityChangeEligibility
        {
            ConnectionRef = connection, Organization = configuration.Organization, Project = configuration.Project,
            WorktreeFingerprint = fingerprint, WorktreeRoot = anchor.WorktreeRoot, AttachmentRevision = initial.Value.Revision,
            CurrentIdentityId = selection?.Identity.IdentityId, CurrentIdentityName = selection?.Identity.Name,
            CurrentBindingId = selection?.Binding.BindingId, SelectionRevision = selection?.SelectionRevision,
            SelectionSource = selection?.SelectionSource, PendingEdits = pending, LocalSeeds = seeds,
            OpenPublishIntents = intents, UnresolvedJournals = journals, ReservedClaims = claims,
            WorktreeGuards = guards, UnknownRows = unknown, ActionableNextSteps = actions
        };

        async Task ReadAsync(string source, Func<Task> read)
        {
            try { await read().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unknown.Add(new(source, ex.Message));
            }
        }

        void ValidateClaim(SystemClaimRow row)
        {
            try
            {
                var claim = JsonSerializer.Deserialize(row.RecordJson, TwigJsonContext.Default.ClaimRecordDocument);
                if (claim is null || claim.SchemaVersion != ClaimRecordDocument.CurrentSchemaVersion
                    || claim.ClaimId != row.ClaimId || claim.ConnectionRef != row.ConnectionRef
                    || claim.WorktreeFingerprint != fingerprint || claim.State != row.State || claim.CasToken != row.CasToken
                    || claim.PrimaryScopeKind != row.PrimaryScopeKind
                    || claim.PrimaryScopeId != row.WorkItemId.ToString(CultureInfo.InvariantCulture)
                    || string.IsNullOrWhiteSpace(claim.HolderIdentity) || string.IsNullOrWhiteSpace(claim.CreatedAt))
                    unknown.Add(new("claim.record", $"Claim {row.ClaimId} has missing, unsupported or mismatching native record/CAS/holder evidence."));
            }
            catch (JsonException)
            {
                unknown.Add(new("claim.record", $"Claim {row.ClaimId} has unreadable native record evidence."));
            }
        }
    }
}
