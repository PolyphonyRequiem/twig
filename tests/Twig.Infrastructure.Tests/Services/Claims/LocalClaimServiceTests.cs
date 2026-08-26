using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Domain.Services.Claims;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Twig.Infrastructure.Services.Claims;
using Xunit;

namespace Twig.Infrastructure.Tests.Services.Claims;

/// <summary>
/// Behavior tests for AB#739's <see cref="LocalClaimService"/>. Every
/// lifecycle branch AB#737 §Named failure vocabulary enumerates has a
/// non-vacuous, observable test. The registry is the real
/// <see cref="SqliteSystemWorktreeRegistry"/> over a per-test temp file so
/// concurrency, uniqueness, and CAS behaviors are exercised end-to-end.
/// The attachment store, ADO projection, holder resolver, and id/CAS
/// generators are testable seams so each branch can be driven precisely.
/// </summary>
public sealed class LocalClaimServiceTests : IDisposable
{
    private readonly string _dbDir;
    private readonly string _dbPath;
    private readonly TimeProvider _clock;
    private readonly SqliteSystemWorktreeRegistry _registry;
    private readonly FakeAttachmentStore _attachment;
    private readonly SequentialClaimIdGenerator _idGen;
    private readonly SequentialCasTokenGenerator _casGen;
    private readonly FakeHolderResolver _holder;
    private readonly FakeAdoClaimProjection _ado;
    private readonly LocalClaimService _svc;

    private const string ConnRef = "conn-fixture";
    private const string Fingerprint = "fingerprint-fixture";
    private const string PrimaryScopeId = "42";
    private const string PrimaryScopeKind = PrimaryScopeKinds.AdoWorkItem;
    private const string Holder = "svc-user@example.com";

    public LocalClaimServiceTests()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), "twig-claim-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dbDir);
        _dbPath = Path.Combine(_dbDir, "system.db");
        _clock = TimeProvider.System;
        _registry = new SqliteSystemWorktreeRegistry(_dbPath, _clock);
        // Register a worktree so InsertClaim's FK precheck passes.
        _registry.UpsertConnectionAsync(ConnRef, "org", "proj", team: null).GetAwaiter().GetResult();
        _registry.UpsertWorktreeAsync(Fingerprint, ConnRef, "/tmp/worktree").GetAwaiter().GetResult();

        _attachment = new FakeAttachmentStore();
        _idGen = new SequentialClaimIdGenerator();
        _casGen = new SequentialCasTokenGenerator();
        _holder = new FakeHolderResolver(new ClaimHolderDescriptor(Holder, "Service User"));
        _ado = new FakeAdoClaimProjection();
        _svc = new LocalClaimService(_registry, _attachment, _idGen, _casGen, _holder, _clock);
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { Directory.Delete(_dbDir, recursive: true); } catch { /* best-effort */ }
    }

    private MintClaimInput MintInput(string? scopeId = null, string holderIdentity = Holder, string? label = "spec-728") =>
        new(ConnRef, PrimaryScopeKind, scopeId ?? PrimaryScopeId, Fingerprint, holderIdentity, "Service User", label, Notes: null, _ado);

    private ReclaimClaimInput ReclaimInput(bool allowSupersede, string? scopeId = null) =>
        new(ConnRef, PrimaryScopeKind, scopeId ?? PrimaryScopeId, Fingerprint, Holder, "Service User",
            Label: null, Notes: null, AllowSupersede: allowSupersede, AdoProjection: _ado);

    // ── T2 mint happy path ─────────────────────────────────────────────

    [Fact]
    public async Task Mint_succeeds_end_to_end_and_produces_active_claim_ADO_write_attachment_link()
    {
        var outcome = await _svc.MintAsync(MintInput());

        outcome.ShouldBeOfType<ClaimMintOutcome.Succeeded>();
        var claim = ((ClaimMintOutcome.Succeeded)outcome).Claim;
        claim.State.ShouldBe(ClaimStates.Active);
        claim.ClaimId.ShouldNotBeNullOrEmpty();
        claim.ConnectionRef.ShouldBe(ConnRef);
        claim.PrimaryScopeId.ShouldBe(PrimaryScopeId);
        claim.HolderIdentity.ShouldBe(Holder);
        claim.Origin.ShouldBe(ClaimOrigins.Local);
        claim.LeaseGeneration.ShouldBe(0);
        claim.ExpiresAt.ShouldBeNull();
        claim.ActivatedAt.ShouldNotBeNull();
        claim.ReleaseReason.ShouldBeNull();

        // ADO projection landed exactly once.
        _ado.HolderCalls.Count.ShouldBe(1);
        _ado.HolderCalls[0].ScopeId.ShouldBe(PrimaryScopeId);
        _ado.HolderCalls[0].Holder.Identity.ShouldBe(Holder);
        _ado.ClearCalls.Count.ShouldBe(0);

        // Attachment linked exactly once with the minted id.
        _attachment.LinkedClaimIds.ShouldBe(new[] { claim.ClaimId });
        _attachment.UnlinkedClaimIds.ShouldBeEmpty();

        // Registry row is active.
        var row = (await _registry.FindClaimAsync(claim.ClaimId)).Value!;
        row.State.ShouldBe(ClaimStates.Active);
    }

    // ── Duplicate reservation on the same tuple ────────────────────────

    [Fact]
    public async Task Mint_refuses_when_a_pending_or_active_row_already_holds_the_tuple()
    {
        var first = await _svc.MintAsync(MintInput());
        first.ShouldBeOfType<ClaimMintOutcome.Succeeded>();

        var second = await _svc.MintAsync(MintInput());
        second.ShouldBeOfType<ClaimMintOutcome.PrimaryScopeAlreadyClaimed>();
        var alreadyClaimed = (ClaimMintOutcome.PrimaryScopeAlreadyClaimed)second;
        alreadyClaimed.ExistingClaimId.ShouldNotBeNullOrEmpty();
        alreadyClaimed.ExistingState.ShouldBe(ClaimStates.Active);

        // ADO write happened exactly once (for the first mint).
        _ado.HolderCalls.Count.ShouldBe(1);
    }

    // ── Mint abort: ADO projection fails ──────────────────────────────

    [Fact]
    public async Task Mint_terminalizes_pending_as_mint_abort_when_ADO_projection_fails()
    {
        _ado.NextHolderResult = Result.Fail("network-down");

        var outcome = await _svc.MintAsync(MintInput());

        outcome.ShouldBeOfType<ClaimMintOutcome.AdoProjectionFailed>();
        ((ClaimMintOutcome.AdoProjectionFailed)outcome).Underlying.ShouldBe("network-down");

        // Attachment was NEVER linked.
        _attachment.LinkedClaimIds.ShouldBeEmpty();

        // Registry: the pending row is now released with mint-abort reason.
        var row = (await _registry.FindReservedClaimAsync(ConnRef, 42, new[] { ClaimStates.Pending, ClaimStates.Active })).Value;
        row.ShouldBeNull();
        // Enumerate history to confirm the pending row was terminalized.
        var all = (await _registry.FindClaimsForTupleAsync(ConnRef, 42)).Value;
        all.Count.ShouldBe(1);
        all[0].State.ShouldBe(ClaimStates.Released);
        var doc = JsonSerializer.Deserialize(all[0].RecordJson, TwigJsonContext.Default.ClaimRecordDocument)!;
        doc.ReleaseReason.ShouldBe(ClaimReleaseReasons.MintAbort);
    }

    // ── Mint abort MUST preserve a pre-existing conformant claim on
    //    another scope. ────────────────────────────────────────────────

    [Fact]
    public async Task Mint_abort_never_disturbs_a_pre_existing_claim_on_a_different_scope()
    {
        // First: mint an active claim on scope 42.
        var mint42 = await _svc.MintAsync(MintInput(scopeId: "42"));
        mint42.ShouldBeOfType<ClaimMintOutcome.Succeeded>();
        var active42 = ((ClaimMintOutcome.Succeeded)mint42).Claim;

        // Second: attempt to mint scope 99 but ADO fails.
        _ado.NextHolderResult = Result.Fail("simulated");
        var mint99 = await _svc.MintAsync(MintInput(scopeId: "99"));
        mint99.ShouldBeOfType<ClaimMintOutcome.AdoProjectionFailed>();

        // Scope 42's claim survived byte-exact.
        var row42 = (await _registry.FindClaimAsync(active42.ClaimId)).Value!;
        row42.State.ShouldBe(ClaimStates.Active);
        row42.CasToken.ShouldBe(active42.CasToken);
        // Attachment still references only the first-minted id.
        _attachment.LinkedClaimIds.ShouldBe(new[] { active42.ClaimId });
    }

    // ── Attachment link failure surfaces AttachmentLinkFailed ─────────

    [Fact]
    public async Task Mint_returns_attachment_link_failed_when_the_attachment_store_refuses()
    {
        _attachment.LinkFailure = "attachment-io-error";
        var outcome = await _svc.MintAsync(MintInput());

        outcome.ShouldBeOfType<ClaimMintOutcome.AttachmentLinkFailed>();
        var alf = (ClaimMintOutcome.AttachmentLinkFailed)outcome;
        alf.Underlying.ShouldBe("attachment-io-error");
        alf.Claim.State.ShouldBe(ClaimStates.Active);
        // ADO write already happened; that's the "no rollback of the ADO
        // side" spec commitment — the operator sees a live active row and
        // no attachment.
        _ado.HolderCalls.Count.ShouldBe(1);
    }

    // ── Holder resolver failure → HolderUnavailable ───────────────────

    [Fact]
    public async Task Mint_fails_loudly_when_holder_identity_is_absent_and_resolver_reports_unavailable()
    {
        _holder.NextResult = Result.Fail<ClaimHolderDescriptor>("no-authenticated-holder");
        var input = new MintClaimInput(
            ConnRef, PrimaryScopeKind, PrimaryScopeId, Fingerprint,
            HolderIdentity: string.Empty, HolderDisplay: null, Label: null, Notes: null, _ado);

        var outcome = await _svc.MintAsync(input);
        outcome.ShouldBeOfType<ClaimMintOutcome.HolderUnavailable>();
        ((ClaimMintOutcome.HolderUnavailable)outcome).Underlying.ShouldBe("no-authenticated-holder");
        _ado.HolderCalls.Count.ShouldBe(0);
    }

    // ── Reclaim over active supersedes atomically ─────────────────────

    [Fact]
    public async Task Reclaim_with_allow_supersede_creates_new_id_and_marks_predecessor_superseded_atomically()
    {
        var first = await _svc.MintAsync(MintInput());
        first.ShouldBeOfType<ClaimMintOutcome.Succeeded>();
        var predecessor = ((ClaimMintOutcome.Succeeded)first).Claim;

        var reclaim = await _svc.ReclaimAsync(ReclaimInput(allowSupersede: true));
        reclaim.ShouldBeOfType<ClaimReclaimOutcome.Succeeded>();
        var succeeded = (ClaimReclaimOutcome.Succeeded)reclaim;
        succeeded.NewClaim.ClaimId.ShouldNotBe(predecessor.ClaimId);
        succeeded.NewClaim.State.ShouldBe(ClaimStates.Active);
        succeeded.SupersededClaim.ShouldNotBeNull();
        succeeded.SupersededClaim!.ClaimId.ShouldBe(predecessor.ClaimId);
        succeeded.SupersededClaim.State.ShouldBe(ClaimStates.Superseded);
        succeeded.SupersededClaim.SupersededByClaimId.ShouldBe(succeeded.NewClaim.ClaimId);
        succeeded.SupersededClaim.ReleaseReason.ShouldBe(ClaimReleaseReasons.ExplicitReclaim);

        // Attachment now points at the new id.
        _attachment.LinkedClaimIds.Last().ShouldBe(succeeded.NewClaim.ClaimId);

        // Registry: predecessor row is superseded, new row is active.
        var newRow = (await _registry.FindClaimAsync(succeeded.NewClaim.ClaimId)).Value!;
        newRow.State.ShouldBe(ClaimStates.Active);
        var oldRow = (await _registry.FindClaimAsync(predecessor.ClaimId)).Value!;
        oldRow.State.ShouldBe(ClaimStates.Superseded);
    }

    // ── Reclaim allowSupersede=true refuses when nothing to supersede ─

    [Fact]
    public async Task Reclaim_with_allow_supersede_refuses_when_no_active_row_exists()
    {
        var reclaim = await _svc.ReclaimAsync(ReclaimInput(allowSupersede: true));
        reclaim.ShouldBeOfType<ClaimReclaimOutcome.ClaimNotActive>();
        ((ClaimReclaimOutcome.ClaimNotActive)reclaim).CurrentState.ShouldBe("none");
        _ado.HolderCalls.Count.ShouldBe(0);
    }

    // ── Reclaim allowSupersede=false behaves like a fresh mint ────────

    [Fact]
    public async Task Reclaim_without_supersede_over_released_row_mints_new_id()
    {
        var first = await _svc.MintAsync(MintInput());
        var active = ((ClaimMintOutcome.Succeeded)first).Claim;
        var release = await _svc.ReleaseAsync(new ReleaseClaimInput(active.ClaimId, _ado));
        release.ShouldBeOfType<ClaimReleaseOutcome.Succeeded>();

        var reclaim = await _svc.ReclaimAsync(ReclaimInput(allowSupersede: false));
        reclaim.ShouldBeOfType<ClaimReclaimOutcome.Succeeded>();
        var succeeded = (ClaimReclaimOutcome.Succeeded)reclaim;
        succeeded.NewClaim.ClaimId.ShouldNotBe(active.ClaimId);
        succeeded.SupersededClaim.ShouldBeNull();
    }

    // ── Release: happy path clears ADO then terminalizes local ────────

    [Fact]
    public async Task Release_clears_ado_first_then_terminalizes_local_and_unlinks_attachment()
    {
        var mint = await _svc.MintAsync(MintInput());
        var claim = ((ClaimMintOutcome.Succeeded)mint).Claim;

        var release = await _svc.ReleaseAsync(new ReleaseClaimInput(claim.ClaimId, _ado));
        release.ShouldBeOfType<ClaimReleaseOutcome.Succeeded>();
        var released = ((ClaimReleaseOutcome.Succeeded)release).Released;
        released.State.ShouldBe(ClaimStates.Released);
        released.ReleaseReason.ShouldBe(ClaimReleaseReasons.ExplicitRelease);
        released.ReleasedAt.ShouldNotBeNull();

        _ado.ClearCalls.Count.ShouldBe(1);
        _ado.ClearCalls[0].ScopeId.ShouldBe(PrimaryScopeId);
        _attachment.UnlinkedClaimIds.ShouldBe(new[] { claim.ClaimId });
    }

    // ── Release: ADO clear failure leaves row active ──────────────────

    [Fact]
    public async Task Release_leaves_row_active_when_ado_clear_fails()
    {
        var mint = await _svc.MintAsync(MintInput());
        var claim = ((ClaimMintOutcome.Succeeded)mint).Claim;

        _ado.NextClearResult = Result.Fail("clear-blocked");
        var release = await _svc.ReleaseAsync(new ReleaseClaimInput(claim.ClaimId, _ado));

        release.ShouldBeOfType<ClaimReleaseOutcome.ReleaseAdoProjectionFailed>();
        var row = (await _registry.FindClaimAsync(claim.ClaimId)).Value!;
        row.State.ShouldBe(ClaimStates.Active);
        _attachment.UnlinkedClaimIds.ShouldBeEmpty();
    }

    // ── Release: attachment unlink failure surfaces AttachmentUnlinkFailed
    //    but local row is already terminal. ─────────────────────────────

    [Fact]
    public async Task Release_returns_attachment_unlink_failed_but_row_is_already_released()
    {
        var mint = await _svc.MintAsync(MintInput());
        var claim = ((ClaimMintOutcome.Succeeded)mint).Claim;

        _attachment.UnlinkFailure = "attachment-unlink-io";
        var release = await _svc.ReleaseAsync(new ReleaseClaimInput(claim.ClaimId, _ado));

        release.ShouldBeOfType<ClaimReleaseOutcome.AttachmentUnlinkFailed>();
        var auf = (ClaimReleaseOutcome.AttachmentUnlinkFailed)release;
        auf.Underlying.ShouldBe("attachment-unlink-io");
        auf.Released.State.ShouldBe(ClaimStates.Released);

        var row = (await _registry.FindClaimAsync(claim.ClaimId)).Value!;
        row.State.ShouldBe(ClaimStates.Released);
    }

    // ── Release: claim not found ─────────────────────────────────────

    [Fact]
    public async Task Release_reports_claim_not_found_when_no_row_matches()
    {
        var outcome = await _svc.ReleaseAsync(new ReleaseClaimInput("does-not-exist", _ado));
        outcome.ShouldBeOfType<ClaimReleaseOutcome.ClaimNotFound>();
    }

    // ── Release: claim not active ────────────────────────────────────

    [Fact]
    public async Task Release_reports_claim_not_active_when_row_is_terminal()
    {
        var mint = await _svc.MintAsync(MintInput());
        var claim = ((ClaimMintOutcome.Succeeded)mint).Claim;
        await _svc.ReleaseAsync(new ReleaseClaimInput(claim.ClaimId, _ado));

        var second = await _svc.ReleaseAsync(new ReleaseClaimInput(claim.ClaimId, _ado));
        second.ShouldBeOfType<ClaimReleaseOutcome.ClaimNotActive>();
        ((ClaimReleaseOutcome.ClaimNotActive)second).CurrentState.ShouldBe(ClaimStates.Released);
    }

    // ── Validate: offline success on active claim ────────────────────

    [Fact]
    public async Task Validate_offline_returns_success_on_active_row_with_matching_tuple()
    {
        var mint = await _svc.MintAsync(MintInput());
        var claim = ((ClaimMintOutcome.Succeeded)mint).Claim;

        _ado.Reset();
        var validate = await _svc.ValidateAsync(new ClaimValidationInput(
            claim.ClaimId, ConnRef, PrimaryScopeKind, PrimaryScopeId));
        validate.ShouldBeOfType<ClaimValidationOutcome.Succeeded>();
        // Validate MUST NOT touch ADO — 100% offline.
        _ado.HolderCalls.Count.ShouldBe(0);
        _ado.ClearCalls.Count.ShouldBe(0);
    }

    // ── Validate: tuple mismatch on connectionRef ────────────────────

    [Fact]
    public async Task Validate_reports_tuple_mismatch_when_stored_tuple_disagrees()
    {
        var mint = await _svc.MintAsync(MintInput());
        var claim = ((ClaimMintOutcome.Succeeded)mint).Claim;

        var validate = await _svc.ValidateAsync(new ClaimValidationInput(
            claim.ClaimId, "other-conn", PrimaryScopeKind, PrimaryScopeId));
        validate.ShouldBeOfType<ClaimValidationOutcome.TupleMismatch>();

        validate = await _svc.ValidateAsync(new ClaimValidationInput(
            claim.ClaimId, ConnRef, PrimaryScopeKind, "999"));
        validate.ShouldBeOfType<ClaimValidationOutcome.TupleMismatch>();
    }

    // ── Validate: claim not found / not active ───────────────────────

    [Fact]
    public async Task Validate_reports_claim_not_found_and_not_active_distinctly()
    {
        var validate = await _svc.ValidateAsync(new ClaimValidationInput(
            "missing", ConnRef, PrimaryScopeKind, PrimaryScopeId));
        validate.ShouldBeOfType<ClaimValidationOutcome.ClaimNotFound>();

        var mint = await _svc.MintAsync(MintInput());
        var claim = ((ClaimMintOutcome.Succeeded)mint).Claim;
        await _svc.ReleaseAsync(new ReleaseClaimInput(claim.ClaimId, _ado));

        validate = await _svc.ValidateAsync(new ClaimValidationInput(
            claim.ClaimId, ConnRef, PrimaryScopeKind, PrimaryScopeId));
        validate.ShouldBeOfType<ClaimValidationOutcome.ClaimNotActive>();
        ((ClaimValidationOutcome.ClaimNotActive)validate).CurrentState.ShouldBe(ClaimStates.Released);
    }

    // ── SchemaDrift: unknown extra field in record_json ──────────────

    [Fact]
    public async Task Validate_reports_schema_drift_on_unknown_field_in_record_json()
    {
        var mint = await _svc.MintAsync(MintInput());
        var claim = ((ClaimMintOutcome.Succeeded)mint).Claim;

        // Inject an unknown field into the stored record_json — the reader
        // MUST refuse it as schema drift.
        RewriteRecordJson(claim.ClaimId, doc =>
            doc + "-extra"); // Actually rewrite via raw JSON below

        var validate = await _svc.ValidateAsync(new ClaimValidationInput(
            claim.ClaimId, ConnRef, PrimaryScopeKind, PrimaryScopeId));
        validate.ShouldBeOfType<ClaimValidationOutcome.SchemaDrift>();
    }

    // ── SchemaDrift: newer schema version ────────────────────────────

    [Fact]
    public async Task Validate_reports_schema_drift_on_higher_schema_version()
    {
        var mint = await _svc.MintAsync(MintInput());
        var claim = ((ClaimMintOutcome.Succeeded)mint).Claim;

        // Rewrite schemaVersion to 2 — the reader must refuse.
        RewriteRecordJsonRaw(claim.ClaimId, json => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"));

        var validate = await _svc.ValidateAsync(new ClaimValidationInput(
            claim.ClaimId, ConnRef, PrimaryScopeKind, PrimaryScopeId));
        validate.ShouldBeOfType<ClaimValidationOutcome.SchemaDrift>();
    }

    // ── LookupByTuple: found + not-found ────────────────────────────

    [Fact]
    public async Task LookupByTuple_returns_the_reserved_row_or_not_found()
    {
        var missing = await _svc.LookupByTupleAsync(new ClaimTupleQuery(ConnRef, PrimaryScopeKind, PrimaryScopeId));
        missing.ShouldBeOfType<ClaimLookupOutcome.NotFound>();

        var mint = await _svc.MintAsync(MintInput());
        var claim = ((ClaimMintOutcome.Succeeded)mint).Claim;
        var found = await _svc.LookupByTupleAsync(new ClaimTupleQuery(ConnRef, PrimaryScopeKind, PrimaryScopeId));
        found.ShouldBeOfType<ClaimLookupOutcome.Found>();
        ((ClaimLookupOutcome.Found)found).Claim.ClaimId.ShouldBe(claim.ClaimId);
    }

    // ── UpdateLabel: CAS-guarded ─────────────────────────────────────

    [Fact]
    public async Task UpdateLabel_rewrites_label_under_CAS_and_bumps_token()
    {
        var mint = await _svc.MintAsync(MintInput());
        var claim = ((ClaimMintOutcome.Succeeded)mint).Claim;

        var updated = await _svc.UpdateLabelAsync(new UpdateClaimLabelInput(claim.ClaimId, "new-label", claim.CasToken));
        updated.ShouldBeOfType<ClaimLabelUpdateOutcome.Succeeded>();
        var succeeded = ((ClaimLabelUpdateOutcome.Succeeded)updated).Claim;
        succeeded.Label.ShouldBe("new-label");
        succeeded.CasToken.ShouldNotBe(claim.CasToken);
        succeeded.State.ShouldBe(ClaimStates.Active);
    }

    [Fact]
    public async Task UpdateLabel_reports_concurrent_write_on_stale_cas_token()
    {
        var mint = await _svc.MintAsync(MintInput());
        var claim = ((ClaimMintOutcome.Succeeded)mint).Claim;

        var stale = await _svc.UpdateLabelAsync(new UpdateClaimLabelInput(claim.ClaimId, "x", "stale-token"));
        stale.ShouldBeOfType<ClaimLabelUpdateOutcome.ConcurrentClaimWrite>();
    }

    // ── Concurrency: parallel mints on the same tuple race safely ────

    [Fact]
    public async Task Two_concurrent_mints_on_the_same_tuple_produce_one_success_and_one_already_claimed()
    {
        // Serialize the underlying db writes but issue the two mints back
        // to back so both attempt the InsertClaim. The registry's partial
        // unique index turns one into a duplicate.
        var input = MintInput();
        var mintA = _svc.MintAsync(input);
        var mintB = _svc.MintAsync(input);
        var results = await Task.WhenAll(mintA, mintB);

        var successes = results.Count(r => r is ClaimMintOutcome.Succeeded);
        var duplicates = results.Count(r => r is ClaimMintOutcome.PrimaryScopeAlreadyClaimed);
        successes.ShouldBe(1);
        duplicates.ShouldBe(1);
    }

    // ── Concurrency: activation CAS mismatch ─────────────────────────

    [Fact]
    public async Task Mint_returns_concurrent_write_when_activation_cas_mismatches()
    {
        // Two mints will each generate the same pending id in this sequential
        // generator, so injecting a colliding CAS token between reserve
        // and activate breaks the update. Simulate via direct registry manip:
        // insert a pending row, corrupt its CAS token, then run mint via a
        // pre-fabricated pending record. Simpler: install a fake registry.

        var mockRegistry = new StubRegistry();
        mockRegistry.InsertResult = Result.Ok();
        mockRegistry.UpdateStateResult = Result.Fail(AttachmentStorageFailure.ClaimCasMismatch);
        var svc = new LocalClaimService(
            mockRegistry, _attachment, _idGen, _casGen, _holder, _clock);

        var outcome = await svc.MintAsync(MintInput());
        outcome.ShouldBeOfType<ClaimMintOutcome.ConcurrentClaimWrite>();
    }

    // ── Invalid input handling ───────────────────────────────────────

    [Fact]
    public async Task Mint_reports_invalid_request_when_required_fields_are_empty()
    {
        var bad = new MintClaimInput(
            ConnectionRef: "", PrimaryScopeKind, PrimaryScopeId, Fingerprint, Holder, null, null, null, _ado);
        var outcome = await _svc.MintAsync(bad);
        outcome.ShouldBeOfType<ClaimMintOutcome.InvalidRequest>();
    }

    private void RewriteRecordJson(string claimId, Func<string, string> transform)
    {
        // Not used — the parameterless overload rewrites through the raw path.
        _ = transform;
        RewriteRecordJsonRaw(claimId, raw =>
            raw.TrimEnd('}') + ",\"unknownExtraField\":\"drift\"}");
    }

    private void RewriteRecordJsonRaw(string claimId, Func<string, string> transform)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        string original;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT record_json FROM claims WHERE claim_id = $id;";
            read.Parameters.AddWithValue("$id", claimId);
            original = (string)read.ExecuteScalar()!;
        }
        var rewritten = transform(original);
        using var write = conn.CreateCommand();
        write.CommandText = "UPDATE claims SET record_json = $j WHERE claim_id = $id;";
        write.Parameters.AddWithValue("$id", claimId);
        write.Parameters.AddWithValue("$j", rewritten);
        write.ExecuteNonQuery();
    }

    // ── Fakes ────────────────────────────────────────────────────────

    private sealed class FakeAttachmentStore : IPrimaryScopeAttachmentStore
    {
        public List<string> LinkedClaimIds { get; } = new();
        public List<string> UnlinkedClaimIds { get; } = new();
        public string? LinkFailure { get; set; }
        public string? UnlinkFailure { get; set; }

        public bool IsManagedWorktree() => true;
        public Task<Result<PrimaryScopeAttachment>> ReadAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Ok(PrimaryScopeAttachment.Empty("ignored")));
        public Task<Result> WriteAsync(PrimaryScopeAttachment attachment, CancellationToken ct = default) =>
            Task.FromResult(Result.Ok());
        public Task<Result> InitializeAsync(CancellationToken ct = default) => Task.FromResult(Result.Ok());

        public Task<Result> LinkClaimAsync(string claimId, DateTimeOffset mintedAt, CancellationToken ct = default)
        {
            if (LinkFailure is not null)
                return Task.FromResult(Result.Fail(LinkFailure));
            LinkedClaimIds.Add(claimId);
            return Task.FromResult(Result.Ok());
        }

        public Task<Result> UnlinkClaimAsync(string expectedClaimId, CancellationToken ct = default)
        {
            if (UnlinkFailure is not null)
                return Task.FromResult(Result.Fail(UnlinkFailure));
            UnlinkedClaimIds.Add(expectedClaimId);
            return Task.FromResult(Result.Ok());
        }
    }

    private sealed class SequentialClaimIdGenerator : IClaimIdGenerator
    {
        private int _seq;
        public string NewClaimId() => $"CLM{Interlocked.Increment(ref _seq):D6}";
    }

    private sealed class SequentialCasTokenGenerator : IClaimCasTokenGenerator
    {
        private int _seq;
        public string NewCasToken() => $"CAS{Interlocked.Increment(ref _seq):D6}";
    }

    private sealed class FakeHolderResolver : IClaimHolderResolver
    {
        private readonly ClaimHolderDescriptor _defaultHolder;
        public Result<ClaimHolderDescriptor>? NextResult { get; set; }
        public FakeHolderResolver(ClaimHolderDescriptor holder) => _defaultHolder = holder;
        public Task<Result<ClaimHolderDescriptor>> ResolveAsync(CancellationToken ct = default)
            => Task.FromResult(NextResult ?? Result.Ok(_defaultHolder));
    }

    private sealed class FakeAdoClaimProjection : IAdoClaimProjection
    {
        public List<(string ScopeId, ClaimHolderDescriptor Holder)> HolderCalls { get; } = new();
        public List<(string ScopeId, DateTimeOffset At)> ClearCalls { get; } = new();
        public Result? NextHolderResult { get; set; }
        public Result? NextClearResult { get; set; }

        public Task<Result> ProjectHolderAsync(string primaryScopeId, ClaimHolderDescriptor holder, CancellationToken ct = default)
        {
            HolderCalls.Add((primaryScopeId, holder));
            var r = NextHolderResult ?? Result.Ok();
            NextHolderResult = null;
            return Task.FromResult(r);
        }

        public Task<Result> ClearHolderAsync(string primaryScopeId, CancellationToken ct = default)
        {
            ClearCalls.Add((primaryScopeId, DateTimeOffset.UtcNow));
            var r = NextClearResult ?? Result.Ok();

            NextClearResult = null;
            return Task.FromResult(r);
        }

        public void Reset()
        {
            HolderCalls.Clear();
            ClearCalls.Clear();
            NextHolderResult = null;
            NextClearResult = null;
        }
    }

    /// <summary>Stub registry used by the CAS-mismatch mint test — every
    /// other test uses the real SQLite registry so uniqueness + CAS behave
    /// exactly as production.</summary>
    private sealed class StubRegistry : ISystemWorktreeRegistry
    {
        public Result InsertResult { get; set; } = Result.Ok();
        public Result UpdateStateResult { get; set; } = Result.Ok();

        public Task<Result<SystemWorktreeRow?>> FindWorktreeAsync(string worktreeFingerprint, CancellationToken ct = default) => Task.FromResult(Result.Ok<SystemWorktreeRow?>(new SystemWorktreeRow("conn", null)));
        public Task<Result> UpsertConnectionAsync(string connectionRef, string organization, string project, string? team, CancellationToken ct = default) => Task.FromResult(Result.Ok());
        public Task<Result> UpsertWorktreeAsync(string worktreeFingerprint, string connectionRef, string worktreeRoot, CancellationToken ct = default) => Task.FromResult(Result.Ok());
        public Task<Result> InsertClaimAsync(string claimId, string connectionRef, string worktreeFingerprint, int workItemId, string state, string casToken, string recordJson, CancellationToken ct = default) => Task.FromResult(InsertResult);
        public Task<Result> UpdateClaimStateAsync(string claimId, string expectedCasToken, string newCasToken, string state, DateTimeOffset? endedAt, string recordJson, CancellationToken ct = default) => Task.FromResult(UpdateStateResult);
        public Task<Result<SystemClaimRow?>> FindClaimAsync(string claimId, CancellationToken ct = default) => Task.FromResult(Result.Ok<SystemClaimRow?>(null));
        public Task<Result<SystemClaimRow?>> FindReservedClaimAsync(string connectionRef, int workItemId, IReadOnlyList<string> reservedStates, CancellationToken ct = default) => Task.FromResult(Result.Ok<SystemClaimRow?>(null));
        public Task<Result<IReadOnlyList<SystemClaimRow>>> FindClaimsForTupleAsync(string connectionRef, int workItemId, CancellationToken ct = default) => Task.FromResult(Result.Ok<IReadOnlyList<SystemClaimRow>>(Array.Empty<SystemClaimRow>()));
        public Task<Result> SupersedeAndActivateClaimAsync(string newClaimId, string newCasToken, string connectionRef, string worktreeFingerprint, int workItemId, string newRecordJson, string predecessorClaimId, string predecessorExpectedCasToken, string predecessorNewCasToken, string predecessorRecordJson, DateTimeOffset transitionAt, CancellationToken ct = default) => Task.FromResult(Result.Ok());
        public Task<Result<SystemProfileCacheRow?>> ReadProfileCacheAsync(string connectionRef, CancellationToken ct = default) => Task.FromResult(Result.Ok<SystemProfileCacheRow?>(null));
        public Task<Result> WriteProfileCacheAsync(string connectionRef, string profileIdentity, string profileVersion, string payload, CancellationToken ct = default) => Task.FromResult(Result.Ok());
    }
}
