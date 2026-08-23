using System.Globalization;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Infrastructure.Plan;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="IPlanJournalRepository"/> over the durable store's
/// <c>plan_journals</c> / <c>plan_operations</c> tables (twig plan native, wayfinder 0016).
/// <para>
/// The journal is the DURABLE half of "record intent before the call, record the outcome after
/// it" (0001 §4). The source plan file is bound to the row here by canonical SHA-256 digest, and
/// state advances only through the atomic compare-and-transition in
/// <see cref="TryTransitionOperationAsync"/> — that is the single lifecycle guard, and it is why
/// two concurrent workers writing the same op cannot both win.
/// </para>
/// <para>
/// Tables live in the attached <c>pending</c> schema. SQLite resolves unqualified table names
/// across attached schemas, so the SQL below carries no prefix (0013).
/// </para>
/// </summary>
public sealed class SqlitePlanJournalRepository : IPlanJournalRepository
{
    // The plan document format is versioned externally; this is the version the header row
    // records so a future migration can tell what shape the canonical_json was written under.
    // Not to be confused with the durable schema version (which shapes THIS row).
    private const int PlanFileSchemaVersion = 1;

    private readonly SqliteCacheStore _store;

    public SqlitePlanJournalRepository(SqliteCacheStore store) => _store = store;

    /// <summary>
    /// Writes the header row and every operation row in state <see cref="PlanOperationState.Planned"/>
    /// under a single transaction, cryptographically binding the three artifact arguments before
    /// any row is touched.
    /// <para>
    /// The boundary check recomputes canonical form via <see cref="PlanCanonicalizer"/> and
    /// rejects the call if <paramref name="canonicalJson"/> is not already in canonical form, if
    /// its SHA-256 does not equal <paramref name="digest"/>, or if <paramref name="plan"/>'s
    /// workspace and operation identities do not agree with the parsed canonical document. That
    /// is what guarantees a persisted digest names exactly the bytes it was computed from.
    /// </para>
    /// <para>
    /// The header write itself is <c>INSERT OR IGNORE</c>, so a same-digest race lets exactly one
    /// caller write the header + ops; every subsequent (or concurrently-losing) caller reloads
    /// the persisted canonical JSON and compares. Equal returns the existing journal; unequal is
    /// a defense-in-depth refusal — a caller cannot re-bind an existing digest to a different
    /// document even if it survived the boundary check.
    /// </para>
    /// </summary>
    public async Task<PlanJournal> ImportAsync(
        PlanDefinition plan,
        string canonicalJson,
        string digest,
        string sourcePath,
        DateTimeOffset previewedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrEmpty(canonicalJson);
        ArgumentException.ThrowIfNullOrEmpty(digest);
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);

        // Boundary binding. Every failure mode here is a caller bug: mismatched arguments would
        // let the journal describe a plan the file no longer represents. Refuse before touching
        // the DB so a rejected import leaves no partial state.
        BindArtifacts(plan, canonicalJson, digest);

        var conn = _store.GetConnection();
        var ownedTx = _store.ActiveTransaction is null;
        var tx = _store.ActiveTransaction ?? conn.BeginTransaction();
        bool inserted;
        try
        {
            using (var header = conn.CreateCommand())
            {
                header.Transaction = tx;
                // INSERT OR IGNORE turns the same-digest race into a single-winner outcome
                // without a primary-key exception. Two concurrent importers of an identical plan
                // do not need to coordinate — the loser sees changes()=0 and reloads.
                header.CommandText = """
                    INSERT OR IGNORE INTO plan_journals
                        (digest, schema_version, organization, project, source_path,
                         canonical_json, state, previewed_at, confirmed_at, completed_at, error)
                    VALUES
                        (@digest, @schemaVersion, @org, @project, @source,
                         @canonical, @state, @previewedAt, NULL, NULL, NULL);
                    """;
                header.Parameters.AddWithValue("@digest", digest);
                header.Parameters.AddWithValue("@schemaVersion", PlanFileSchemaVersion);
                header.Parameters.AddWithValue("@org", plan.Workspace.Organization);
                header.Parameters.AddWithValue("@project", plan.Workspace.Project);
                header.Parameters.AddWithValue("@source", sourcePath);
                header.Parameters.AddWithValue("@canonical", canonicalJson);
                header.Parameters.AddWithValue("@state", PlanOperationState.Planned.ToString());
                header.Parameters.AddWithValue("@previewedAt", FormatTimestamp(previewedAt));
                inserted = header.ExecuteNonQuery() == 1;
            }

            if (inserted)
            {
                // Only the winner writes ops. The per-op canonical JSON is a slice of the whole
                // canonical document (already in canonical form per the boundary check above),
                // so no re-canonicalization is needed here.
                using var canonical = JsonDocument.Parse(canonicalJson);
                var operationsElement = canonical.RootElement.GetProperty("operations");

                for (var ordinal = 0; ordinal < plan.Operations.Count; ordinal++)
                {
                    var op = plan.Operations[ordinal];
                    var requestJson = operationsElement[ordinal].GetRawText();

                    using var opCmd = conn.CreateCommand();
                    opCmd.Transaction = tx;
                    opCmd.CommandText = """
                        INSERT INTO plan_operations
                            (digest, ordinal, op_id, kind, state, request_json,
                             started_at, applied_at, verified_at, result_json, error)
                        VALUES
                            (@digest, @ordinal, @opId, @kind, @state, @requestJson,
                             NULL, NULL, NULL, NULL, NULL);
                        """;
                    opCmd.Parameters.AddWithValue("@digest", digest);
                    opCmd.Parameters.AddWithValue("@ordinal", ordinal);
                    opCmd.Parameters.AddWithValue("@opId", op.Id);
                    opCmd.Parameters.AddWithValue("@kind", op.Kind.ToString());
                    opCmd.Parameters.AddWithValue("@state", PlanOperationState.Planned.ToString());
                    opCmd.Parameters.AddWithValue("@requestJson", requestJson);
                    opCmd.ExecuteNonQuery();
                }
            }

            if (ownedTx)
                tx.Commit();
        }
        catch
        {
            if (ownedTx)
                tx.Rollback();
            throw;
        }
        finally
        {
            if (ownedTx)
                tx.Dispose();
        }

        var loaded = await GetAsync(digest, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Plan journal was inserted but could not be read back — durable store may be corrupt.");

        if (!inserted && !string.Equals(loaded.CanonicalJson, canonicalJson, StringComparison.Ordinal))
        {
            // Defense-in-depth: the boundary already guarantees SHA(canonical) == digest, so
            // reaching here means the persisted row was written by a different importer against a
            // canonical document that also happened to hash to the same digest. That is either a
            // SHA-256 collision or someone bypassed this API to write a doctored row. Either way
            // silently accepting one over the other would let the journal describe a plan that no
            // caller currently observes. Refuse.
            throw new InvalidOperationException(
                $"Plan digest '{digest}' is already recorded against a different canonical " +
                "document. Refusing to overwrite an existing plan journal.");
        }

        return loaded;
    }

    /// <summary>
    /// Cryptographic binding of the three artifact arguments. The canonical bytes are reparsed
    /// through <see cref="PlanDocumentParser"/> — the same validator that produced them from
    /// the source file — and the parsed result becomes the reference: its canonical bytes must
    /// equal the caller's, its digest must equal the caller's, and its <see cref="PlanDefinition"/>
    /// must be structurally identical (workspace + every per-subtype operation field) to the
    /// caller's <paramref name="plan"/>. Any divergence is rejected before the transaction
    /// opens so a mismatched import leaves no partial state.
    /// </summary>
    private static void BindArtifacts(PlanDefinition plan, string canonicalJson, string digest)
    {
        var parseResult = new PlanDocumentParser().Parse(canonicalJson);
        if (!parseResult.IsValid || parseResult.Plan is null
            || parseResult.CanonicalJson is null || parseResult.Digest is null)
        {
            var reason = parseResult.Issues.Count > 0
                ? $"{parseResult.Issues[0].Code}: {parseResult.Issues[0].Message}"
                : "no plan produced";
            throw new InvalidOperationException(
                $"Plan import: canonicalJson did not parse as a valid plan v1 document ({reason}). " +
                "Refusing to persist an unvalidated artifact.");
        }

        // Canonical form: any whitespace, re-ordering, or numeric-formatting difference between
        // the caller's bytes and the parser's canonicalization means the digest binds bytes we
        // would never persist. Refuse before the DB write so a subsequent reload cannot see a
        // document at odds with the hash that named it.
        if (!string.Equals(parseResult.CanonicalJson, canonicalJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Plan import: canonicalJson is not in canonical form. Refusing to persist a " +
                "journal keyed on a document that was not produced by the canonicalizer.");
        }
        if (!string.Equals(parseResult.Digest, digest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Plan import: supplied digest '{digest}' does not match the SHA-256 of the " +
                $"canonical document ('{parseResult.Digest}'). Refusing to bind the journal to a " +
                "mismatched identity.");
        }

        // Semantic-equality cross-check. The parser IS the reference implementation of "what
        // this canonical document means" — comparing subtype by subtype catches every same-id,
        // same-kind payload tamper (workItemId, expectedRevision, fields, relation/otherId,
        // stagedIdentity/expectedFingerprint) that a coarser id+kind check would miss.
        if (!IsSemanticallyEqual(parseResult.Plan, plan, out var mismatch))
        {
            throw new InvalidOperationException(
                $"Plan import: PlanDefinition does not match the canonical document ({mismatch}). " +
                "Refusing to persist a journal whose PlanDefinition disagrees with the artifact " +
                "its digest names.");
        }
    }

    private static bool IsSemanticallyEqual(PlanDefinition canonical, PlanDefinition supplied, out string mismatch)
    {
        if (canonical.Version != supplied.Version)
        {
            mismatch = $"version {canonical.Version} vs {supplied.Version}";
            return false;
        }
        if (!string.Equals(canonical.Workspace.Organization, supplied.Workspace.Organization, StringComparison.Ordinal)
            || !string.Equals(canonical.Workspace.Project, supplied.Workspace.Project, StringComparison.Ordinal))
        {
            mismatch = "workspace";
            return false;
        }
        if (canonical.Operations.Count != supplied.Operations.Count)
        {
            mismatch = $"operation count {canonical.Operations.Count} vs {supplied.Operations.Count}";
            return false;
        }
        for (var i = 0; i < canonical.Operations.Count; i++)
        {
            if (!IsOperationSemanticallyEqual(canonical.Operations[i], supplied.Operations[i], out var opMismatch))
            {
                mismatch = $"operation at ordinal {i}: {opMismatch}";
                return false;
            }
        }
        mismatch = string.Empty;
        return true;
    }

    private static bool IsOperationSemanticallyEqual(
        PlanOperationDefinition canonical,
        PlanOperationDefinition supplied,
        out string mismatch)
    {
        if (canonical.GetType() != supplied.GetType())
        {
            mismatch = $"kind {canonical.Kind} vs {supplied.Kind}";
            return false;
        }
        if (!string.Equals(canonical.Id, supplied.Id, StringComparison.Ordinal))
        {
            mismatch = $"id '{canonical.Id}' vs '{supplied.Id}'";
            return false;
        }
        switch (canonical)
        {
            case BatchOperation cb when supplied is BatchOperation sb:
                if (cb.WorkItemId != sb.WorkItemId) { mismatch = "workItemId"; return false; }
                if (cb.ExpectedRevision != sb.ExpectedRevision) { mismatch = "expectedRevision"; return false; }
                if (!AreFieldsEqual(cb.Fields, sb.Fields, out var fieldsReason))
                { mismatch = $"fields ({fieldsReason})"; return false; }
                break;
            case AddLinkOperation cal when supplied is AddLinkOperation sal:
                if (cal.WorkItemId != sal.WorkItemId) { mismatch = "workItemId"; return false; }
                if (cal.ExpectedRevision != sal.ExpectedRevision) { mismatch = "expectedRevision"; return false; }
                if (!string.Equals(cal.Relation, sal.Relation, StringComparison.Ordinal))
                { mismatch = "relation"; return false; }
                if (cal.OtherId != sal.OtherId) { mismatch = "otherId"; return false; }
                break;
            case RemoveLinkOperation crl when supplied is RemoveLinkOperation srl:
                if (crl.WorkItemId != srl.WorkItemId) { mismatch = "workItemId"; return false; }
                if (crl.ExpectedRevision != srl.ExpectedRevision) { mismatch = "expectedRevision"; return false; }
                if (!string.Equals(crl.Relation, srl.Relation, StringComparison.Ordinal))
                { mismatch = "relation"; return false; }
                if (crl.OtherId != srl.OtherId) { mismatch = "otherId"; return false; }
                break;
            case PublishSeedOperation cps when supplied is PublishSeedOperation sps:
                if (!cps.StagedIdentity.Equals(sps.StagedIdentity))
                { mismatch = "stagedIdentity"; return false; }
                if (!string.Equals(cps.ExpectedFingerprint, sps.ExpectedFingerprint, StringComparison.Ordinal))
                { mismatch = "expectedFingerprint"; return false; }
                break;
            case DeleteOperation cd when supplied is DeleteOperation sd:
                if (cd.WorkItemId != sd.WorkItemId) { mismatch = "workItemId"; return false; }
                if (cd.ExpectedRevision != sd.ExpectedRevision) { mismatch = "expectedRevision"; return false; }
                break;
            default:
                mismatch = $"unknown subtype {canonical.GetType().Name}";
                return false;
        }
        mismatch = string.Empty;
        return true;
    }

    private static bool AreFieldsEqual(
        IReadOnlyDictionary<string, string?> a,
        IReadOnlyDictionary<string, string?> b,
        out string mismatch)
    {
        if (a.Count != b.Count)
        {
            mismatch = $"count {a.Count} vs {b.Count}";
            return false;
        }
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var otherValue))
            {
                mismatch = $"missing key '{kv.Key}'";
                return false;
            }
            if (!string.Equals(kv.Value, otherValue, StringComparison.Ordinal))
            {
                mismatch = $"value at '{kv.Key}'";
                return false;
            }
        }
        mismatch = string.Empty;
        return true;
    }

    public Task<PlanJournal?> GetAsync(string digest, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(digest);

        var conn = _store.GetConnection();
        using var header = conn.CreateCommand();
        header.Transaction = _store.ActiveTransaction;
        header.CommandText = """
            SELECT organization, project, source_path, canonical_json,
                   state, previewed_at, confirmed_at, completed_at, error
            FROM plan_journals
            WHERE digest = @digest;
            """;
        header.Parameters.AddWithValue("@digest", digest);

        using var reader = header.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<PlanJournal?>(null);

        var workspace = new PlanWorkspace
        {
            Organization = reader.GetString(0),
            Project = reader.GetString(1),
        };
        var sourcePath = reader.GetString(2);
        var canonicalJson = reader.GetString(3);
        var state = ParseState(reader.GetString(4));
        var previewedAt = ParseTimestamp(reader.GetString(5));
        var confirmedAt = reader.IsDBNull(6) ? (DateTimeOffset?)null : ParseTimestamp(reader.GetString(6));
        var completedAt = reader.IsDBNull(7) ? (DateTimeOffset?)null : ParseTimestamp(reader.GetString(7));
        var error = reader.IsDBNull(8) ? null : reader.GetString(8);
        reader.Close();

        var operations = ReadOperations(digest);

        return Task.FromResult<PlanJournal?>(new PlanJournal
        {
            Digest = digest,
            SourcePath = sourcePath,
            CanonicalJson = canonicalJson,
            Workspace = workspace,
            State = state,
            PreviewedAt = previewedAt,
            ConfirmedAt = confirmedAt,
            CompletedAt = completedAt,
            Error = error,
            Operations = operations,
        });
    }

    private IReadOnlyList<PlanJournalOperation> ReadOperations(string digest)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = """
            SELECT ordinal, op_id, kind, state, request_json,
                   started_at, applied_at, verified_at, result_json, error
            FROM plan_operations
            WHERE digest = @digest
            ORDER BY ordinal ASC;
            """;
        cmd.Parameters.AddWithValue("@digest", digest);

        var result = new List<PlanJournalOperation>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PlanJournalOperation
            {
                Ordinal = reader.GetInt32(0),
                OpId = reader.GetString(1),
                Kind = ParseKind(reader.GetString(2)),
                State = ParseState(reader.GetString(3)),
                RequestJson = reader.GetString(4),
                StartedAt = reader.IsDBNull(5) ? null : ParseTimestamp(reader.GetString(5)),
                AppliedAt = reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6)),
                VerifiedAt = reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7)),
                ResultJson = reader.IsDBNull(8) ? null : reader.GetString(8),
                Error = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }
        return result;
    }

    public Task ConfirmAsync(string digest, DateTimeOffset confirmedAt, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(digest);

        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        // Confirmation only advances the header from Planned. A journal already Applying / Applied
        // / Verified / Failed cannot be re-confirmed — the plan is already past that gate. A
        // Planned → Confirmed conditional UPDATE captures that with the same atomic-guard shape as
        // the per-op transition.
        cmd.CommandText = """
            UPDATE plan_journals
            SET state = @confirmed, confirmed_at = @timestamp
            WHERE digest = @digest AND state = @planned;
            """;
        cmd.Parameters.AddWithValue("@digest", digest);
        cmd.Parameters.AddWithValue("@confirmed", PlanOperationState.Confirmed.ToString());
        cmd.Parameters.AddWithValue("@planned", PlanOperationState.Planned.ToString());
        cmd.Parameters.AddWithValue("@timestamp", FormatTimestamp(confirmedAt));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The single lifecycle guard: a conditional UPDATE that succeeds only when the row is still
    /// in <paramref name="fromState"/> AND is not already terminal
    /// (<see cref="PlanOperationState.Verified"/>, <see cref="PlanOperationState.Failed"/>,
    /// <see cref="PlanOperationState.Indeterminate"/>). Two workers racing to move the same op
    /// to <see cref="PlanOperationState.Applying"/> — exactly one wins, the other sees
    /// <c>false</c>.
    /// </summary>
    /// <remarks>
    /// The <paramref name="timestamp"/> is written into the column that names the transition
    /// being made (<c>started_at</c> for Applying, <c>applied_at</c> for Applied,
    /// <c>verified_at</c> for Verified). Other columns are preserved.
    /// <para>
    /// Terminal-state immutability is enforced in the SQL WHERE, not left to the caller: even a
    /// caller that names <c>fromState = Verified</c> (or Failed / Indeterminate) cannot walk a
    /// row back out of a terminal state through this method. That guarantee is what
    /// <see cref="SaveOperationResultAsync"/> and <see cref="SaveOperationErrorAsync"/>'s
    /// terminal-immutability contract rests on — otherwise a rerun that observed Verified could
    /// pull the row back into Applying and overwrite the outcome the previous run committed.
    /// </para>
    /// </remarks>
    public Task<bool> TryTransitionOperationAsync(
        string digest,
        string opId,
        PlanOperationState fromState,
        PlanOperationState toState,
        DateTimeOffset timestamp,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(digest);
        ArgumentException.ThrowIfNullOrEmpty(opId);

        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = """
            UPDATE plan_operations
            SET state = @toState,
                started_at  = CASE WHEN @toState = @applyingState THEN @timestamp ELSE started_at  END,
                applied_at  = CASE WHEN @toState = @appliedState  THEN @timestamp ELSE applied_at  END,
                verified_at = CASE WHEN @toState = @verifiedState THEN @timestamp ELSE verified_at END
            WHERE digest = @digest
              AND op_id = @opId
              AND state = @fromState
              AND state NOT IN (@verifiedState, @failedState, @indeterminateState);
            """;
        cmd.Parameters.AddWithValue("@digest", digest);
        cmd.Parameters.AddWithValue("@opId", opId);
        cmd.Parameters.AddWithValue("@fromState", fromState.ToString());
        cmd.Parameters.AddWithValue("@toState", toState.ToString());
        cmd.Parameters.AddWithValue("@timestamp", FormatTimestamp(timestamp));
        cmd.Parameters.AddWithValue("@applyingState", PlanOperationState.Applying.ToString());
        cmd.Parameters.AddWithValue("@appliedState", PlanOperationState.Applied.ToString());
        cmd.Parameters.AddWithValue("@verifiedState", PlanOperationState.Verified.ToString());
        cmd.Parameters.AddWithValue("@failedState", PlanOperationState.Failed.ToString());
        cmd.Parameters.AddWithValue("@indeterminateState", PlanOperationState.Indeterminate.ToString());

        var changed = cmd.ExecuteNonQuery();
        return Task.FromResult(changed > 0);
    }

    /// <summary>
    /// Records the outcome of an apply attempt (<paramref name="resultJson"/>) on an
    /// <see cref="PlanOperationState.Applied"/> row. Does NOT change the operation's state
    /// and does NOT stamp any timestamp column — the Applied → Verified transition is a
    /// separate explicit CAS via <see cref="TryTransitionOperationAsync"/>, and it is the
    /// sole writer of <c>verified_at</c>. Rows that are not yet in Applied, or already
    /// terminal (Verified / Failed / Indeterminate), are left untouched.
    /// </summary>
    /// <remarks>
    /// The Applied-only guard is intentional: a result is a fact about an apply that already
    /// committed, and there is no meaningful outcome to record for a row still in Planned /
    /// Confirmed / Applying, nor for one whose terminal outcome has already been settled. The
    /// call is silent (no throw) on those states — repeated crash-recovered retries must be
    /// safe even when they observe a state they did not expect.
    /// </remarks>
    public Task SaveOperationResultAsync(
        string digest,
        string opId,
        string? resultJson,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(digest);
        ArgumentException.ThrowIfNullOrEmpty(opId);

        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        // Applied-only precondition. Neither state nor any timestamp column is written —
        // the row stays in Applied until an explicit TryTransitionOperationAsync(Applied →
        // Verified) moves it and stamps verified_at. Two effects fall out of this shape:
        // (1) the "does not change state, does not stamp verified_at" contract is enforced
        // at the SQL, and (2) a rerun that sees Verified / Failed / Indeterminate walks
        // past this call as a no-op, preserving the terminal outcome the previous run
        // committed.
        cmd.CommandText = """
            UPDATE plan_operations
            SET result_json = @resultJson
            WHERE digest = @digest
              AND op_id = @opId
              AND state = @applied;
            """;
        cmd.Parameters.AddWithValue("@digest", digest);
        cmd.Parameters.AddWithValue("@opId", opId);
        cmd.Parameters.AddWithValue("@applied", PlanOperationState.Applied.ToString());
        cmd.Parameters.AddWithValue("@resultJson", (object?)resultJson ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Atomic Applying → Applied that stamps <c>applied_at</c> and writes
    /// <c>result_json</c> in one row update. This is the crash-window-free replacement for
    /// a <see cref="TryTransitionOperationAsync"/> + <see cref="SaveOperationResultAsync"/>
    /// pair: no gap can leave a row Applied with a null result.
    /// </summary>
    /// <remarks>
    /// The Applying-only precondition is enforced in the SQL WHERE. A row already Applied
    /// (an earlier successful atomic record), or any terminal row (Verified / Failed /
    /// Indeterminate), is left untouched and the method returns <c>false</c>. Two workers
    /// racing to atomically record the same op — exactly one wins.
    /// </remarks>
    public Task<bool> TryRecordAppliedAsync(
        string digest,
        string opId,
        string? resultJson,
        DateTimeOffset appliedAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(digest);
        ArgumentException.ThrowIfNullOrEmpty(opId);

        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = """
            UPDATE plan_operations
            SET state = @applied,
                applied_at = @timestamp,
                result_json = @resultJson
            WHERE digest = @digest
              AND op_id = @opId
              AND state = @applying;
            """;
        cmd.Parameters.AddWithValue("@digest", digest);
        cmd.Parameters.AddWithValue("@opId", opId);
        cmd.Parameters.AddWithValue("@applied", PlanOperationState.Applied.ToString());
        cmd.Parameters.AddWithValue("@applying", PlanOperationState.Applying.ToString());
        cmd.Parameters.AddWithValue("@timestamp", FormatTimestamp(appliedAt));
        cmd.Parameters.AddWithValue("@resultJson", (object?)resultJson ?? DBNull.Value);

        var changed = cmd.ExecuteNonQuery();
        return Task.FromResult(changed > 0);
    }

    public Task SaveOperationErrorAsync(
        string digest,
        string opId,
        string error,
        PlanOperationState finalState,
        DateTimeOffset timestamp,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(digest);
        ArgumentException.ThrowIfNullOrEmpty(opId);
        ArgumentException.ThrowIfNullOrEmpty(error);

        if (finalState != PlanOperationState.Failed && finalState != PlanOperationState.Indeterminate)
        {
            // A non-terminal "error final state" would leave the op looking recoverable while the
            // error column names something that already happened. Refuse the mixed message.
            throw new ArgumentOutOfRangeException(nameof(finalState),
                $"Error outcome must terminate as Failed or Indeterminate, not {finalState}.");
        }

        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = """
            UPDATE plan_operations
            SET state = @finalState,
                error = @error,
                applied_at  = CASE WHEN @finalState = @appliedState  THEN @timestamp ELSE applied_at  END,
                verified_at = CASE WHEN @finalState = @verifiedState THEN @timestamp ELSE verified_at END
            WHERE digest = @digest
              AND op_id = @opId
              AND state NOT IN (@verified, @failed, @indeterminate);
            """;
        cmd.Parameters.AddWithValue("@digest", digest);
        cmd.Parameters.AddWithValue("@opId", opId);
        cmd.Parameters.AddWithValue("@finalState", finalState.ToString());
        cmd.Parameters.AddWithValue("@error", error);
        cmd.Parameters.AddWithValue("@timestamp", FormatTimestamp(timestamp));
        cmd.Parameters.AddWithValue("@verified", PlanOperationState.Verified.ToString());
        cmd.Parameters.AddWithValue("@failed", PlanOperationState.Failed.ToString());
        cmd.Parameters.AddWithValue("@indeterminate", PlanOperationState.Indeterminate.ToString());
        cmd.Parameters.AddWithValue("@appliedState", PlanOperationState.Applied.ToString());
        cmd.Parameters.AddWithValue("@verifiedState", PlanOperationState.Verified.ToString());
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task CompleteAsync(
        string digest,
        PlanOperationState finalState,
        DateTimeOffset completedAt,
        string? error,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(digest);

        if (finalState != PlanOperationState.Verified
            && finalState != PlanOperationState.Failed
            && finalState != PlanOperationState.Indeterminate)
        {
            throw new ArgumentOutOfRangeException(nameof(finalState),
                $"Journal completion must end in a terminal state (Verified, Failed, or " +
                $"Indeterminate), not {finalState}.");
        }

        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        // Same terminal-immutability guard as the per-op writes: a completed journal cannot be
        // reopened by this API. Recovery starts from what is already written, not from a
        // rewriting caller.
        cmd.CommandText = """
            UPDATE plan_journals
            SET state = @finalState,
                completed_at = @timestamp,
                error = @error
            WHERE digest = @digest
              AND state NOT IN (@verified, @failed, @indeterminate);
            """;
        cmd.Parameters.AddWithValue("@digest", digest);
        cmd.Parameters.AddWithValue("@finalState", finalState.ToString());
        cmd.Parameters.AddWithValue("@timestamp", FormatTimestamp(completedAt));
        cmd.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@verified", PlanOperationState.Verified.ToString());
        cmd.Parameters.AddWithValue("@failed", PlanOperationState.Failed.ToString());
        cmd.Parameters.AddWithValue("@indeterminate", PlanOperationState.Indeterminate.ToString());
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    // ISO 8601 with offset — round-trip format. Matches the convention every other durable table
    // uses (see SqlitePublishIntentRepository, SqliteSeedLinkRepository), so a future join or
    // report reads the same shape everywhere.
    private static string FormatTimestamp(DateTimeOffset value) => value.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static PlanOperationState ParseState(string value) =>
        Enum.Parse<PlanOperationState>(value, ignoreCase: false);

    private static PlanOperationKind ParseKind(string value) =>
        Enum.Parse<PlanOperationKind>(value, ignoreCase: false);
}
