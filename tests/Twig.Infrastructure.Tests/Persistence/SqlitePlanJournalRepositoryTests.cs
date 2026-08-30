using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Integration tests for <see cref="SqlitePlanJournalRepository"/> — the durable ledger of a
/// declarative plan document (twig plan native, wayfinder 0016).
/// <para>
/// File-backed for the crash-reload / concurrent-race tests; the rest use <c>:memory:</c> for
/// isolation. Every plan artifact used here is produced by <see cref="PlanDocumentParser"/>, so
/// the tests exercise the same canonical vocabulary (<c>batch</c> / <c>add-link</c> / ...) the
/// public contract accepts, not a fixture-local approximation.
/// </para>
/// </summary>
public class SqlitePlanJournalRepositoryTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqlitePlanJournalRepository _repo;

    public SqlitePlanJournalRepositoryTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _repo = new SqlitePlanJournalRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void Migration_CreatesProposalJournalsAndProposalOperationsInTheDurableSchema()
    {
        // The durable store must actually contain the new tables under the ATTACHed pending
        // schema. A silent no-op migration would make every other test pass by accident.
        AssertDurableTableExists("proposal_journals");
        AssertDurableTableExists("proposal_operations");
        AssertDurableIndexExists("idx_proposal_journals_state");
        AssertDurableIndexExists("idx_proposal_operations_ordinal");
        AssertDurableIndexExists("idx_proposal_operations_state");
    }

    [Fact]
    public async Task Import_PersistsHeaderAndOperationsInPlannedState()
    {
        var plan = BuildTwoOpPlan();
        var previewedAt = DateTimeOffset.Parse("2026-08-22T10:00:00Z");

        var imported = await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/plans/x.json", previewedAt);

        imported.Digest.ShouldBe(plan.Digest);
        imported.State.ShouldBe(PlanOperationState.Planned);
        imported.CanonicalJson.ShouldBe(plan.CanonicalJson);
        imported.Workspace.Organization.ShouldBe("acme");
        imported.Workspace.Project.ShouldBe("cache");
        imported.PreviewedAt.ShouldBe(previewedAt);
        imported.ConfirmedAt.ShouldBeNull();
        imported.CompletedAt.ShouldBeNull();
        imported.Error.ShouldBeNull();

        imported.Operations.Count.ShouldBe(2);
        foreach (var op in imported.Operations)
        {
            op.State.ShouldBe(PlanOperationState.Planned);
            op.StartedAt.ShouldBeNull();
            op.AppliedAt.ShouldBeNull();
            op.VerifiedAt.ShouldBeNull();
            op.ResultJson.ShouldBeNull();
            op.Error.ShouldBeNull();
            op.RequestJson.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task Import_IsIdempotentBySameDigest()
    {
        var plan = BuildTwoOpPlan();

        var first = await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var again = await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now().AddMinutes(5));

        // Same digest -> same row returned, PreviewedAt unchanged. A second import that MOVED
        // PreviewedAt would erase the moment the caller first saw the plan.
        again.Digest.ShouldBe(first.Digest);
        again.PreviewedAt.ShouldBe(first.PreviewedAt);

        // Exactly one journal row and N op rows — the second call did not insert duplicates.
        CountDurableRows(_store, "proposal_journals", "digest", plan.Digest).ShouldBe(1);
        CountDurableRows(_store, "proposal_operations", "digest", plan.Digest).ShouldBe(2);
    }

    // ─── boundary binding: digest / canonical form / plan identity ──────────────

    [Fact]
    public async Task Import_RejectsDigestNotMatchingCanonicalBytes()
    {
        // The boundary binds the digest to the canonical bytes — a caller feeding the wrong
        // digest cannot get past ImportAsync, so a persisted journal is always keyed on the
        // hash of exactly what its row contains.
        var plan = BuildTwoOpPlan();
        var wrongDigest = new string('0', 64);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _repo.ImportAsync(plan, plan.CanonicalJson, wrongDigest, "/p.json", Now()));

        ex.Message.ShouldContain(wrongDigest);
        // Refusal happens before any DB write — no partial state left behind.
        CountDurableRows(_store, "proposal_journals", "digest", wrongDigest).ShouldBe(0);
        CountDurableRows(_store, "proposal_journals", "digest", plan.Digest).ShouldBe(0);
    }

    [Fact]
    public async Task Import_RejectsCanonicalJsonNotInCanonicalForm()
    {
        // The canonical-form check protects against callers who compute the digest of one byte
        // sequence but pass a semantically-equivalent but not-yet-canonical one. Since the
        // digest names the exact bytes, storing a divergent copy would let a later reload see
        // a document at odds with what the digest binds.
        var plan = BuildTwoOpPlan();
        var whitespaced = "  " + plan.CanonicalJson + "  ";
        var digestOfWhitespaced = PlanCanonicalizer.ComputeDigest(
            System.Text.Encoding.UTF8.GetBytes(whitespaced));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _repo.ImportAsync(plan, whitespaced, digestOfWhitespaced, "/p.json", Now()));

        ex.Message.ShouldContain("canonical form");
        CountDurableRows(_store, "proposal_journals", "digest", digestOfWhitespaced).ShouldBe(0);
    }

    [Fact]
    public async Task Import_RejectsCanonicalJsonThatDoesNotParse()
    {
        var plan = BuildTwoOpPlan();
        var garbage = "{ this is not JSON";
        var digestOfGarbage = PlanCanonicalizer.ComputeDigest(
            System.Text.Encoding.UTF8.GetBytes(garbage));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            _repo.ImportAsync(plan, garbage, digestOfGarbage, "/p.json", Now()));

        CountDurableRows(_store, "proposal_journals", "digest", digestOfGarbage).ShouldBe(0);
    }

    [Fact]
    public async Task Import_RejectsCanonicalJsonThatFailsPlanValidation()
    {
        // A canonical byte sequence that parses as JSON but isn't a valid plan (wrong version,
        // missing workspace, unknown operation kind, ...) must be rejected by the boundary —
        // otherwise the journal could name a "plan" the rest of the pipeline can never execute.
        var plan = BuildTwoOpPlan();
        // Canonical form of a well-formed JSON that isn't a plan.
        var invalidPlanJson = "{\"version\":1}";
        var canonical = new PlanDocumentParser().Parse(invalidPlanJson);
        canonical.IsValid.ShouldBeFalse(); // sanity: parser rejects this
        // Compute a "real" canonical/digest pair for the JSON as-is (skipping validation).
        var utf8 = System.Text.Encoding.UTF8.GetBytes(invalidPlanJson);
        var digest = PlanCanonicalizer.ComputeDigest(utf8);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            _repo.ImportAsync(plan, invalidPlanJson, digest, "/p.json", Now()));

        CountDurableRows(_store, "proposal_journals", "digest", digest).ShouldBe(0);
    }

    [Fact]
    public async Task Import_RejectsPlanWorkspaceDisagreementWithCanonical()
    {
        // Even when canonical + digest hash-match, PlanDefinition.Workspace is cross-checked
        // against the parsed canonical document. A caller assembling PlanDefinition from one
        // plan and canonical bytes from another cannot get past the boundary.
        var truePlan = BuildTwoOpPlan();
        var forgedDefinition = new PlanDefinition
        {
            Version = truePlan.Plan.Version,
            Workspace = new PlanWorkspace { Organization = "some-other-org", Project = "cache" },
            Operations = truePlan.Plan.Operations,
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _repo.ImportAsync(forgedDefinition, truePlan.CanonicalJson, truePlan.Digest, "/p.json", Now()));

        ex.Message.ShouldContain("workspace");
        CountDurableRows(_store, "proposal_journals", "digest", truePlan.Digest).ShouldBe(0);
    }

    [Fact]
    public async Task Import_RejectsPlanOperationCountDisagreementWithCanonical()
    {
        var truePlan = BuildTwoOpPlan();
        var forgedDefinition = new PlanDefinition
        {
            Version = truePlan.Plan.Version,
            Workspace = truePlan.Plan.Workspace,
            Operations = new[] { truePlan.Plan.Operations[0] },
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _repo.ImportAsync(forgedDefinition, truePlan.CanonicalJson, truePlan.Digest, "/p.json", Now()));

        ex.Message.ShouldContain("operation count");
        CountDurableRows(_store, "proposal_journals", "digest", truePlan.Digest).ShouldBe(0);
    }

    [Fact]
    public async Task Import_RejectsPlanOperationKindDisagreementWithCanonical()
    {
        // Same id, different subtype (Batch vs Delete). id+kind mismatch is the coarsest
        // possible payload tamper — the boundary must reject it.
        var truePlan = BuildTwoOpPlan();
        var batchOp = (BatchOperation)truePlan.Plan.Operations[0];
        var forgedOps = new PlanOperationDefinition[]
        {
            new DeleteOperation
            {
                Id = batchOp.Id,
                WorkItemId = batchOp.WorkItemId,
                ExpectedRevision = batchOp.ExpectedRevision,
            },
            truePlan.Plan.Operations[1],
        };
        var forgedDefinition = new PlanDefinition
        {
            Version = truePlan.Plan.Version,
            Workspace = truePlan.Plan.Workspace,
            Operations = forgedOps,
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _repo.ImportAsync(forgedDefinition, truePlan.CanonicalJson, truePlan.Digest, "/p.json", Now()));

        ex.Message.ShouldContain("ordinal 0");
        ex.Message.ShouldContain("kind");
        CountDurableRows(_store, "proposal_journals", "digest", truePlan.Digest).ShouldBe(0);
    }

    [Fact]
    public async Task Import_RejectsSameKindSameIdPayloadTamper_WorkItemId()
    {
        // Same kind, same id, different target work item id. A coarse id+kind check would
        // let this through and execute an ADO PATCH against the wrong record. The boundary
        // must compare the full per-subtype payload.
        var truePlan = BuildTwoOpPlan();
        var batch = (BatchOperation)truePlan.Plan.Operations[0];
        var forged = truePlan.Plan.Operations.ToArray();
        forged[0] = new BatchOperation
        {
            Id = batch.Id,
            WorkItemId = batch.WorkItemId + 1, // tampered
            ExpectedRevision = batch.ExpectedRevision,
            Fields = batch.Fields,
        };
        var forgedDefinition = new PlanDefinition
        {
            Version = truePlan.Plan.Version,
            Workspace = truePlan.Plan.Workspace,
            Operations = forged,
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _repo.ImportAsync(forgedDefinition, truePlan.CanonicalJson, truePlan.Digest, "/p.json", Now()));

        ex.Message.ShouldContain("workItemId");
        CountDurableRows(_store, "proposal_journals", "digest", truePlan.Digest).ShouldBe(0);
    }

    [Fact]
    public async Task Import_RejectsSameKindSameIdPayloadTamper_ExpectedRevision()
    {
        var truePlan = BuildTwoOpPlan();
        var batch = (BatchOperation)truePlan.Plan.Operations[0];
        var forged = truePlan.Plan.Operations.ToArray();
        forged[0] = new BatchOperation
        {
            Id = batch.Id,
            WorkItemId = batch.WorkItemId,
            ExpectedRevision = batch.ExpectedRevision + 42, // tampered
            Fields = batch.Fields,
        };
        var forgedDefinition = new PlanDefinition
        {
            Version = truePlan.Plan.Version,
            Workspace = truePlan.Plan.Workspace,
            Operations = forged,
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _repo.ImportAsync(forgedDefinition, truePlan.CanonicalJson, truePlan.Digest, "/p.json", Now()));

        ex.Message.ShouldContain("expectedRevision");
    }

    [Fact]
    public async Task Import_RejectsSameKindSameIdPayloadTamper_Fields()
    {
        var truePlan = BuildTwoOpPlan();
        var batch = (BatchOperation)truePlan.Plan.Operations[0];
        var doctoredFields = new Dictionary<string, string?>(batch.Fields);
        // Alter one field value — same keys, different bytes on the wire.
        var firstKey = doctoredFields.Keys.First();
        doctoredFields[firstKey] = "TAMPERED-" + doctoredFields[firstKey];

        var forged = truePlan.Plan.Operations.ToArray();
        forged[0] = new BatchOperation
        {
            Id = batch.Id,
            WorkItemId = batch.WorkItemId,
            ExpectedRevision = batch.ExpectedRevision,
            Fields = doctoredFields,
        };
        var forgedDefinition = new PlanDefinition
        {
            Version = truePlan.Plan.Version,
            Workspace = truePlan.Plan.Workspace,
            Operations = forged,
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _repo.ImportAsync(forgedDefinition, truePlan.CanonicalJson, truePlan.Digest, "/p.json", Now()));

        ex.Message.ShouldContain("fields");
    }

    [Fact]
    public async Task Import_RejectsSameKindSameIdPayloadTamper_LinkRelation()
    {
        // add-link operation with the same id but a different relation kind must not be able
        // to smuggle through: a "predecessor" op passed as if it were a "successor" one would
        // execute the wrong side of a dependency edit.
        var truePlan = BuildLinkPlan();
        var link = (AddLinkOperation)truePlan.Plan.Operations[0];
        var forged = new PlanDefinition
        {
            Version = truePlan.Plan.Version,
            Workspace = truePlan.Plan.Workspace,
            Operations = new[]
            {
                (PlanOperationDefinition)new AddLinkOperation
                {
                    Id = link.Id,
                    WorkItemId = link.WorkItemId,
                    ExpectedRevision = link.ExpectedRevision,
                    Relation = link.Relation == "successor" ? "predecessor" : "successor",
                    OtherId = link.OtherId,
                },
            },
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _repo.ImportAsync(forged, truePlan.CanonicalJson, truePlan.Digest, "/p.json", Now()));

        ex.Message.ShouldContain("relation");
    }

    [Fact]
    public async Task Import_RejectsSameKindSameIdPayloadTamper_PublishSeed()
    {
        // publish-seed's staged identity is the ONLY thing that names which seed publishes;
        // an unchecked mismatch could send the publish to a different queued item entirely.
        var truePlan = BuildPublishSeedPlan();
        var seed = (PublishSeedOperation)truePlan.Plan.Operations[0];
        var forged = new PlanDefinition
        {
            Version = truePlan.Plan.Version,
            Workspace = truePlan.Plan.Workspace,
            Operations = new[]
            {
                (PlanOperationDefinition)new PublishSeedOperation
                {
                    Id = seed.Id,
                    StagedIdentity = StagedIdentity.New(), // tampered
                    ExpectedFingerprint = seed.ExpectedFingerprint,
                },
            },
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _repo.ImportAsync(forged, truePlan.CanonicalJson, truePlan.Digest, "/p.json", Now()));

        ex.Message.ShouldContain("stagedIdentity");
    }

    // Every branch of the semantic-equality comparator that the boundary uses to reject a
    // PlanDefinition that disagrees with its canonical bytes. A missing branch here means a
    // caller could smuggle a tampered payload past the digest check because the property was
    // never compared. The matrix drives one plan (mixed-subtype, all five kinds) through the
    // full grid of single-property mutations — Version, Workspace.Project, per-op Id, and
    // every payload field on Batch / AddLink / RemoveLink / PublishSeed / Delete.
    public static IEnumerable<object[]> SemanticMutations() => new[]
    {
        new object[] { "version", "version" },
        new object[] { "workspace.project", "workspace" },
        new object[] { "op.id", "id" },
        new object[] { "batch.workItemId", "workItemId" },
        new object[] { "batch.expectedRevision", "expectedRevision" },
        new object[] { "batch.fields.value", "fields" },
        new object[] { "batch.fields.count", "fields" },
        new object[] { "addlink.workItemId", "workItemId" },
        new object[] { "addlink.expectedRevision", "expectedRevision" },
        new object[] { "addlink.relation", "relation" },
        new object[] { "addlink.otherId", "otherId" },
        new object[] { "removelink.workItemId", "workItemId" },
        new object[] { "removelink.expectedRevision", "expectedRevision" },
        new object[] { "removelink.relation", "relation" },
        new object[] { "removelink.otherId", "otherId" },
        new object[] { "publishseed.stagedIdentity", "stagedIdentity" },
        new object[] { "publishseed.expectedFingerprint", "expectedFingerprint" },
        new object[] { "delete.workItemId", "workItemId" },
        new object[] { "delete.expectedRevision", "expectedRevision" },
    };

    [Theory]
    [MemberData(nameof(SemanticMutations))]
    public async Task Import_RejectsSemanticMismatch_AtEveryComparatorBranch(string mutation, string expectedMessagePart)
    {
        var truePlan = BuildMixedPlan();
        var forged = MutateForRejection(truePlan.Plan, mutation);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _repo.ImportAsync(forged, truePlan.CanonicalJson, truePlan.Digest, "/p.json", Now()));

        ex.Message.ShouldContain(expectedMessagePart);
        // Refusal happens before the transaction opens — no row lands.
        CountDurableRows(_store, "proposal_journals", "digest", truePlan.Digest).ShouldBe(0);
    }

    /// <summary>
    /// Applies exactly one mutation to a fully-populated canonical PlanDefinition and returns
    /// a new definition that differs by that single property. Each branch of the semantic
    /// comparator is targeted by exactly one case in <see cref="SemanticMutations"/>.
    /// </summary>
    private static PlanDefinition MutateForRejection(PlanDefinition source, string mutation)
    {
        var ops = source.Operations.ToArray();
        int batchIx = IndexOfKind(ops, PlanOperationKind.Batch);
        int addIx = IndexOfKind(ops, PlanOperationKind.AddLink);
        int rmIx = IndexOfKind(ops, PlanOperationKind.RemoveLink);
        int seedIx = IndexOfKind(ops, PlanOperationKind.PublishSeed);
        int delIx = IndexOfKind(ops, PlanOperationKind.Delete);
        var version = source.Version;
        var workspace = source.Workspace;

        switch (mutation)
        {
            case "version":
                // PlanDocumentParser normalises Version to 1; anything else is a bind failure.
                version = 2;
                break;
            case "workspace.project":
                workspace = new PlanWorkspace
                {
                    Organization = source.Workspace.Organization,
                    Project = source.Workspace.Project + "-tampered",
                };
                break;
            case "op.id":
                ops[batchIx] = ((BatchOperation)ops[batchIx]) with { Id = ops[batchIx].Id + "-x" };
                break;
            case "batch.workItemId":
                ops[batchIx] = ((BatchOperation)ops[batchIx]) with { WorkItemId = 999_999 };
                break;
            case "batch.expectedRevision":
                ops[batchIx] = ((BatchOperation)ops[batchIx]) with { ExpectedRevision = 999 };
                break;
            case "batch.fields.value":
                {
                    var b = (BatchOperation)ops[batchIx];
                    var copy = new Dictionary<string, string?>(b.Fields);
                    var key = copy.Keys.First();
                    copy[key] = "TAMPERED-" + copy[key];
                    ops[batchIx] = b with { Fields = copy };
                    break;
                }
            case "batch.fields.count":
                {
                    var b = (BatchOperation)ops[batchIx];
                    var copy = new Dictionary<string, string?>(b.Fields) { ["System.Extra"] = "sneaked-in" };
                    ops[batchIx] = b with { Fields = copy };
                    break;
                }
            case "addlink.workItemId":
                ops[addIx] = ((AddLinkOperation)ops[addIx]) with { WorkItemId = 987_654 };
                break;
            case "addlink.expectedRevision":
                ops[addIx] = ((AddLinkOperation)ops[addIx]) with { ExpectedRevision = 987 };
                break;
            case "addlink.relation":
                ops[addIx] = ((AddLinkOperation)ops[addIx]) with { Relation = "related" };
                break;
            case "addlink.otherId":
                ops[addIx] = ((AddLinkOperation)ops[addIx]) with { OtherId = 4242 };
                break;
            case "removelink.workItemId":
                ops[rmIx] = ((RemoveLinkOperation)ops[rmIx]) with { WorkItemId = 555_555 };
                break;
            case "removelink.expectedRevision":
                ops[rmIx] = ((RemoveLinkOperation)ops[rmIx]) with { ExpectedRevision = 555 };
                break;
            case "removelink.relation":
                ops[rmIx] = ((RemoveLinkOperation)ops[rmIx]) with { Relation = "successor" };
                break;
            case "removelink.otherId":
                ops[rmIx] = ((RemoveLinkOperation)ops[rmIx]) with { OtherId = 3333 };
                break;
            case "publishseed.stagedIdentity":
                ops[seedIx] = ((PublishSeedOperation)ops[seedIx]) with { StagedIdentity = StagedIdentity.New() };
                break;
            case "publishseed.expectedFingerprint":
                ops[seedIx] = ((PublishSeedOperation)ops[seedIx]) with { ExpectedFingerprint = "ZZ" };
                break;
            case "delete.workItemId":
                ops[delIx] = ((DeleteOperation)ops[delIx]) with { WorkItemId = 42 };
                break;
            case "delete.expectedRevision":
                ops[delIx] = ((DeleteOperation)ops[delIx]) with { ExpectedRevision = 42 };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), $"Unknown mutation '{mutation}'.");
        }

        return new PlanDefinition
        {
            Version = version,
            Workspace = workspace,
            Operations = ops,
        };
    }

    private static int IndexOfKind(IReadOnlyList<PlanOperationDefinition> ops, PlanOperationKind kind)
    {
        for (var i = 0; i < ops.Count; i++)
            if (ops[i].Kind == kind) return i;
        throw new InvalidOperationException($"Mixed fixture missing an operation of kind {kind}.");
    }

    [Fact]
    public async Task Import_DefenseInDepth_ExistingRowWithDifferentCanonicalIsRefused()
    {
        // Boundary binding forbids a doctored (canonical, digest) pair, so within the public
        // API two rows with matching digest and diverging canonical is only reachable by a
        // SHA-256 collision or an out-of-band writer. Even so the reload/compare step at the
        // end of ImportAsync must refuse rather than silently return a row whose canonical
        // disagrees with the caller's — otherwise the caller believes it recorded ONE plan
        // and the ledger holds another.
        var plan = BuildTwoOpPlan();

        // Seed a row directly with the digest but a doctored canonical_json.
        var doctored = plan.CanonicalJson.Replace("acme", "somebody-else");
        InsertRawPlanJournalRow(plan.Digest, doctored);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now()));

        ex.Message.ShouldContain(plan.Digest);
        ex.Message.ShouldContain("different canonical");
    }

    // ─── concurrency ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Import_ConcurrentSameDigestRace_BarrierGated_SingleRowSingleWinner()
    {
        // Two workers observe the same plan file and race to import it. A Barrier(2) makes
        // both threads enter ImportAsync at the same instant so the underlying INSERT OR
        // IGNORE is genuinely contended — a sequential run would fail the assertion below
        // that BOTH results carry the same SourcePath (the winner's), because a non-race
        // execution would let each caller's own path stick.
        var dir = Path.Combine(Path.GetTempPath(), $"twig-plan-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "twig.db");

        try
        {
            using (var warmup = new SqliteCacheStore($"Data Source={dbPath}"))
            { /* migrations run in ctor */ }

            using var storeA = new SqliteCacheStore($"Data Source={dbPath}");
            using var storeB = new SqliteCacheStore($"Data Source={dbPath}");
            var repoA = new SqlitePlanJournalRepository(storeA);
            var repoB = new SqlitePlanJournalRepository(storeB);

            var plan = BuildTwoOpPlan();
            var previewedAt = Now();

            using var barrier = new Barrier(2);
            var t1 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                return repoA.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/a/plan.json", previewedAt);
            });
            var t2 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                return repoB.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/b/plan.json", previewedAt);
            });

            var results = await Task.WhenAll(t1, t2);

            results[0].Digest.ShouldBe(plan.Digest);
            results[1].Digest.ShouldBe(plan.Digest);
            results[0].CanonicalJson.ShouldBe(plan.CanonicalJson);
            results[1].CanonicalJson.ShouldBe(plan.CanonicalJson);
            // Both callers observe the SAME persisted sourcePath (the winner's) — the loser
            // did not overwrite the winner. It must be one of the two supplied values.
            results[0].SourcePath.ShouldBe(results[1].SourcePath);
            new[] { "/a/plan.json", "/b/plan.json" }.ShouldContain(results[0].SourcePath);

            // Exactly one header row + N op rows in the durable store.
            using var probe = new SqliteCacheStore($"Data Source={dbPath}");
            CountDurableRows(probe, "proposal_journals", "digest", plan.Digest).ShouldBe(1);
            CountDurableRows(probe, "proposal_operations", "digest", plan.Digest).ShouldBe(2);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Import_ConcurrentSameDigestRace_DeterministicOverlap_LoserBlocksThenReloads()
    {
        // Stronger race: caller A opens an ambient transaction and drives ImportAsync inside
        // it, so the header row exists but is not yet committed. Caller B calls ImportAsync on
        // a separate connection — under SQLite's WAL writer lock B's INSERT OR IGNORE must
        // BLOCK until A commits. We prove overlap by asserting B has not completed within a
        // wait window, then commit A and let B proceed. An implementation that still used
        // check-then-INSERT would throw a PRIMARY KEY violation on B once A commits, because
        // B's precheck saw no row.
        var dir = Path.Combine(Path.GetTempPath(), $"twig-plan-race-det-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "twig.db");

        try
        {
            using (var warmup = new SqliteCacheStore($"Data Source={dbPath}"))
            { /* migrations run in ctor */ }

            using var storeA = new SqliteCacheStore($"Data Source={dbPath}");
            using var storeB = new SqliteCacheStore($"Data Source={dbPath}");
            var repoA = new SqlitePlanJournalRepository(storeA);
            var repoB = new SqlitePlanJournalRepository(storeB);

            var plan = BuildTwoOpPlan();
            var previewedAt = Now();

            var uowA = new SqliteUnitOfWork(storeA);
            var txA = await uowA.BeginAsync();
            try
            {
                // A drives ImportAsync inside its ambient tx — header + ops written but not
                // yet committed. Any other connection cannot see them.
                var loadedA = await repoA.ImportAsync(
                    plan, plan.CanonicalJson, plan.Digest, "/a/plan.json", previewedAt);
                loadedA.Digest.ShouldBe(plan.Digest);

                // B starts on its own connection. Its INSERT OR IGNORE waits for the writer
                // lock held by A's uncommitted tx.
                var importB = Task.Run(() => repoB.ImportAsync(
                    plan, plan.CanonicalJson, plan.Digest, "/b/plan.json", previewedAt));

                // Prove B is genuinely blocked, not merely slow. 300ms is generous — B's only
                // pending work is a single INSERT that would return in microseconds if not
                // waiting on a lock.
                var raced = await Task.WhenAny(importB, Task.Delay(300));
                raced.ShouldNotBe((Task)importB,
                    "B must block on the writer lock while A's tx is still uncommitted");

                // Commit A. B's INSERT OR IGNORE now sees the row, changes()=0, and B reloads
                // the persisted canonical (== its own supplied canonical) and returns.
                await uowA.CommitAsync(txA);

                var loadedB = await importB;
                loadedB.Digest.ShouldBe(plan.Digest);
                loadedB.CanonicalJson.ShouldBe(plan.CanonicalJson);
                loadedB.SourcePath.ShouldBe("/a/plan.json"); // A was the writer; B did not overwrite
            }
            finally
            {
                await txA.DisposeAsync();
            }

            using var probe = new SqliteCacheStore($"Data Source={dbPath}");
            CountDurableRows(probe, "proposal_journals", "digest", plan.Digest).ShouldBe(1);
            CountDurableRows(probe, "proposal_operations", "digest", plan.Digest).ShouldBe(2);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ─── operation lifecycle ────────────────────────────────────────────────────

    [Fact]
    public async Task TryTransitionOperation_LegalTransitionAdvancesStateAndTimestamp()
    {
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var opId = plan.Plan.Operations[0].Id;
        var startedAt = Now();

        var advanced = await _repo.TryTransitionOperationAsync(
            plan.Digest, opId, PlanOperationState.Planned, PlanOperationState.Applying, startedAt);

        advanced.ShouldBeTrue();
        var journal = await _repo.GetAsync(plan.Digest);
        var op = journal!.Operations.Single(o => o.OpId == opId);
        op.State.ShouldBe(PlanOperationState.Applying);
        op.StartedAt.ShouldBe(startedAt);
        op.AppliedAt.ShouldBeNull();
        op.VerifiedAt.ShouldBeNull();
    }

    [Fact]
    public async Task TryTransitionOperation_IllegalFromStateReturnsFalseAndLeavesRowUntouched()
    {
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var opId = plan.Plan.Operations[0].Id;

        // Row is Planned, not Applied — the guard must refuse to move it and NOT rewrite state.
        var changed = await _repo.TryTransitionOperationAsync(
            plan.Digest, opId, PlanOperationState.Applied, PlanOperationState.Verified, Now());

        changed.ShouldBeFalse();
        var journal = await _repo.GetAsync(plan.Digest);
        journal!.Operations.Single(o => o.OpId == opId).State.ShouldBe(PlanOperationState.Planned);
    }

    [Fact]
    public async Task TryTransitionOperation_RaceBetweenTwoCallers_OnlyOneWins()
    {
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var opId = plan.Plan.Operations[0].Id;

        // Two workers try to lift the same op into Applying. The atomic compare-and-transition
        // must produce exactly one winner — that is the single lifecycle guard. If both won,
        // apply could double-execute a batch mutation.
        var first = await _repo.TryTransitionOperationAsync(
            plan.Digest, opId, PlanOperationState.Planned, PlanOperationState.Applying, Now());
        var second = await _repo.TryTransitionOperationAsync(
            plan.Digest, opId, PlanOperationState.Planned, PlanOperationState.Applying, Now());

        first.ShouldBeTrue();
        second.ShouldBeFalse();
    }

    // ─── warning persistence (AB#754/755) ───────────────────────────────────────

    [Fact]
    public async Task TryTransitionOperation_WithWarning_PersistsItInTheSameRowUpdateAsTheTransition()
    {
        // The warning is written BY the CAS, not before it. That is the whole point: a
        // pre-CAS write could strand warning text on a row whose transition was then lost.
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var opId = plan.Plan.Operations[0].Id;
        (await _repo.TryTransitionOperationAsync(
            plan.Digest, opId, PlanOperationState.Planned, PlanOperationState.Applied, Now())).ShouldBeTrue();

        var verified = await _repo.TryTransitionOperationAsync(
            plan.Digest, opId, PlanOperationState.Applied, PlanOperationState.Verified, Now(),
            default, "ADO normalized server-generated field(s) after apply: ClosedDate.");

        verified.ShouldBeTrue();
        var journal = await _repo.GetAsync(plan.Digest);
        var op = journal!.Operations.Single(o => o.OpId == opId);
        op.State.ShouldBe(PlanOperationState.Verified);
        op.Warning.ShouldNotBeNull();
        op.Warning.ShouldContain("ClosedDate");
        // A warning must never masquerade as a failure.
        op.Error.ShouldBeNull();
    }

    [Fact]
    public async Task TryTransitionOperation_LostCas_DoesNotWriteTheWarning()
    {
        // The stranding scenario the in-CAS write exists to prevent: a caller that loses the
        // transition must leave no trace on the row, warning included.
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var opId = plan.Plan.Operations[0].Id;

        var changed = await _repo.TryTransitionOperationAsync(
            plan.Digest, opId, PlanOperationState.Applied, PlanOperationState.Verified, Now(),
            default, "this warning must not land");

        changed.ShouldBeFalse();
        var journal = await _repo.GetAsync(plan.Digest);
        var op = journal!.Operations.Single(o => o.OpId == opId);
        op.State.ShouldBe(PlanOperationState.Planned);
        op.Warning.ShouldBeNull();
    }

    [Fact]
    public async Task TryTransitionOperation_NullWarning_PreservesAnAlreadyRecordedWarning()
    {
        // COALESCE semantics: a later warning-free transition must not erase detail an
        // earlier one recorded, or the ledger would silently lose the normalization.
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var opId = plan.Plan.Operations[0].Id;
        (await _repo.TryTransitionOperationAsync(
            plan.Digest, opId, PlanOperationState.Planned, PlanOperationState.Applying, Now(),
            default, "recorded earlier")).ShouldBeTrue();

        (await _repo.TryTransitionOperationAsync(
            plan.Digest, opId, PlanOperationState.Applying, PlanOperationState.Applied, Now()))
            .ShouldBeTrue();

        var journal = await _repo.GetAsync(plan.Digest);
        journal!.Operations.Single(o => o.OpId == opId).Warning.ShouldBe("recorded earlier");
    }

    [Fact]
    public async Task GetAsync_OperationWithNoWarning_ReadsBackNull()
    {
        // Guards the new reader ordinal: a row that never carried a warning must read null,
        // not an empty string or a shifted column value.
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());

        var journal = await _repo.GetAsync(plan.Digest);

        foreach (var op in journal!.Operations)
        {
            op.Warning.ShouldBeNull();
            // Adjacent columns must still resolve — a wrong ordinal would surface here.
            op.Error.ShouldBeNull();
            op.ResultJson.ShouldBeNull();
            op.State.ShouldBe(PlanOperationState.Planned);
        }
    }

    // ─── SaveOperationResult contract (Applied-only, writes result_json only) ────

    [Fact]
    public async Task SaveOperationResult_OnApplied_WritesResultJsonWithoutChangingStateOrStampingVerifiedAt()
    {
        // Contract: the call records result_json on an Applied row without touching state
        // and without stamping verified_at. The Applied → Verified transition is the sole
        // writer of verified_at, and it is a separate explicit CAS the caller invokes AFTER
        // this write.
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var opId = plan.Plan.Operations[0].Id;
        await WalkOpToApplied(plan.Digest, opId);

        await _repo.SaveOperationResultAsync(plan.Digest, opId, """{"newRevision":42}""");

        var after = (await _repo.GetAsync(plan.Digest))!.Operations.Single(o => o.OpId == opId);
        after.State.ShouldBe(PlanOperationState.Applied); // state UNCHANGED
        after.ResultJson.ShouldBe("""{"newRevision":42}""");
        after.VerifiedAt.ShouldBeNull(); // verified_at is the transition's stamp, not Save's
    }

    [Theory]
    [InlineData(PlanOperationState.Planned)]
    [InlineData(PlanOperationState.Confirmed)]
    [InlineData(PlanOperationState.Applying)]
    public async Task SaveOperationResult_OnNonAppliedNonTerminalState_IsSilentNoOp(PlanOperationState startingState)
    {
        // Applied-only precondition: a result is a fact about apply that already succeeded, so
        // a row that hasn't reached Applied yet has nothing to record. The call is silent
        // rather than throwing — a crash-recovered retry must be safe when it observes a state
        // it did not expect.
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var opId = plan.Plan.Operations[0].Id;
        await WalkOpToState(plan.Digest, opId, startingState);

        await _repo.SaveOperationResultAsync(plan.Digest, opId, """{"stray":1}""");

        var after = (await _repo.GetAsync(plan.Digest))!.Operations.Single(o => o.OpId == opId);
        after.State.ShouldBe(startingState);
        after.ResultJson.ShouldBeNull();
        after.VerifiedAt.ShouldBeNull();
    }

    [Fact]
    public async Task SaveOperationResult_HappyPath_SaveThenTransitionEndsAtVerifiedWithResult()
    {
        // The one legal ordering: record the result on Applied, THEN transition to Verified.
        // The Applied → Verified transition is the SOLE writer of verified_at; SaveOperation
        // Result never stamps a timestamp, so the two writes cannot race and there is nothing
        // to overwrite.
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var opId = plan.Plan.Operations[0].Id;
        await WalkOpToApplied(plan.Digest, opId);

        var resultJson = """{"newRevision":7}""";
        await _repo.SaveOperationResultAsync(plan.Digest, opId, resultJson);

        // Still no verified_at — the row is in Applied and no transition has fired.
        var mid = (await _repo.GetAsync(plan.Digest))!.Operations.Single(o => o.OpId == opId);
        mid.State.ShouldBe(PlanOperationState.Applied);
        mid.ResultJson.ShouldBe(resultJson);
        mid.VerifiedAt.ShouldBeNull();

        var verifiedAt = Now().AddSeconds(1);
        (await _repo.TryTransitionOperationAsync(
            plan.Digest, opId, PlanOperationState.Applied, PlanOperationState.Verified, verifiedAt))
            .ShouldBeTrue();

        var after = (await _repo.GetAsync(plan.Digest))!.Operations.Single(o => o.OpId == opId);
        after.State.ShouldBe(PlanOperationState.Verified);
        after.ResultJson.ShouldBe(resultJson);
        after.VerifiedAt.ShouldBe(verifiedAt);
    }

    [Fact]
    public async Task SaveOperationResult_ContractViolation_TransitionBeforeSaveLosesResult()
    {
        // Encodes the ordering contract as an executable regression: transition-first is a
        // caller bug — the row terminalises at Verified with result_json still null, and the
        // subsequent SaveOperationResult is silently rejected by the Applied-only guard.
        // Future refactor that "helpfully" widens the guard to include Verified would silently
        // let a rerun overwrite a terminal outcome; this test guards against that regression.
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var opId = plan.Plan.Operations[0].Id;
        await WalkOpToApplied(plan.Digest, opId);

        var verifiedAt = Now();
        (await _repo.TryTransitionOperationAsync(
            plan.Digest, opId, PlanOperationState.Applied, PlanOperationState.Verified, verifiedAt))
            .ShouldBeTrue();

        await _repo.SaveOperationResultAsync(plan.Digest, opId, """{"tooLate":true}""");

        var after = (await _repo.GetAsync(plan.Digest))!.Operations.Single(o => o.OpId == opId);
        after.State.ShouldBe(PlanOperationState.Verified);
        after.ResultJson.ShouldBeNull(); // caller wrote too late; terminal-immutability held
        after.VerifiedAt.ShouldBe(verifiedAt);
    }

    [Fact]
    public async Task SaveOperationResult_OnTerminalFailedRow_IsSilentNoOp()
    {
        // Terminal-immutability: once Failed / Indeterminate the outcome is settled, and a
        // stray SaveOperationResult must not overwrite the error or plant a result_json.
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var opId = plan.Plan.Operations[0].Id;
        var failedAt = Now();
        await _repo.SaveOperationErrorAsync(plan.Digest, opId, "boom", PlanOperationState.Failed, failedAt);

        await _repo.SaveOperationResultAsync(plan.Digest, opId, """{"stray":1}""");

        var after = (await _repo.GetAsync(plan.Digest))!.Operations.Single(o => o.OpId == opId);
        after.State.ShouldBe(PlanOperationState.Failed);
        after.Error.ShouldBe("boom");
        after.ResultJson.ShouldBeNull();
        after.VerifiedAt.ShouldBeNull();
    }

    // ─── TryRecordAppliedAsync (atomic Applying → Applied + result + applied_at) ─

    [Fact]
    public async Task TryRecordApplied_OnApplying_AtomicallyMovesToAppliedStampsAppliedAtAndWritesResult()
    {
        // Crash-window-free replacement for TryTransition(Applying→Applied) followed by
        // SaveOperationResult. One row update sets state, applied_at, AND result_json —
        // there is no observable Applied-with-null-result midpoint. VerifiedAt stays null:
        // the Applied → Verified CAS is a separate write.
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var opId = plan.Plan.Operations[0].Id;
        await WalkOpToState(plan.Digest, opId, PlanOperationState.Applying);

        var appliedAt = DateTimeOffset.Parse("2026-08-22T10:00:00Z");
        var recorded = await _repo.TryRecordAppliedAsync(
            plan.Digest, opId, """{"newRevision":42}""", appliedAt);

        recorded.ShouldBeTrue();
        var after = (await _repo.GetAsync(plan.Digest))!.Operations.Single(o => o.OpId == opId);
        after.State.ShouldBe(PlanOperationState.Applied);
        after.AppliedAt.ShouldBe(appliedAt);
        after.ResultJson.ShouldBe("""{"newRevision":42}""");
        after.VerifiedAt.ShouldBeNull();
    }

    [Fact]
    public async Task TryRecordApplied_AllowsNullResultJson_ForCrashRecoveryPath()
    {
        // A recovery pass that proves the effect via readback has no executor result JSON
        // to record — passing null is legal and still stamps applied_at atomically.
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var opId = plan.Plan.Operations[0].Id;
        await WalkOpToState(plan.Digest, opId, PlanOperationState.Applying);

        var appliedAt = Now();
        (await _repo.TryRecordAppliedAsync(plan.Digest, opId, resultJson: null, appliedAt)).ShouldBeTrue();

        var after = (await _repo.GetAsync(plan.Digest))!.Operations.Single(o => o.OpId == opId);
        after.State.ShouldBe(PlanOperationState.Applied);
        after.AppliedAt.ShouldBe(appliedAt);
        after.ResultJson.ShouldBeNull();
    }

    [Theory]
    [InlineData(PlanOperationState.Planned)]
    [InlineData(PlanOperationState.Confirmed)]
    [InlineData(PlanOperationState.Applied)]
    public async Task TryRecordApplied_OnNonApplyingNonTerminalState_ReturnsFalseAndLeavesRowUntouched(
        PlanOperationState startingState)
    {
        // Applying-only precondition. A row that hasn't reached Applying (or already moved
        // past it) is left strictly untouched — the caller reloads and routes off the
        // actual state. This mirrors the shape of every other CAS in the interface.
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var opId = plan.Plan.Operations[0].Id;
        await WalkOpToState(plan.Digest, opId, startingState);

        var before = (await _repo.GetAsync(plan.Digest))!.Operations.Single(o => o.OpId == opId);

        (await _repo.TryRecordAppliedAsync(plan.Digest, opId, """{"stray":1}""", Now())).ShouldBeFalse();

        var after = (await _repo.GetAsync(plan.Digest))!.Operations.Single(o => o.OpId == opId);
        after.State.ShouldBe(before.State);
        after.ResultJson.ShouldBe(before.ResultJson);
        after.AppliedAt.ShouldBe(before.AppliedAt);
    }

    [Fact]
    public async Task TryRecordApplied_OnTerminalRow_ReturnsFalseAndPreservesTerminalOutcome()
    {
        // Terminal-immutability: a stray atomic record must not rewrite a settled failure.
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var opId = plan.Plan.Operations[0].Id;
        await _repo.SaveOperationErrorAsync(plan.Digest, opId, "boom", PlanOperationState.Failed, Now());

        (await _repo.TryRecordAppliedAsync(plan.Digest, opId, """{"stray":1}""", Now())).ShouldBeFalse();

        var after = (await _repo.GetAsync(plan.Digest))!.Operations.Single(o => o.OpId == opId);
        after.State.ShouldBe(PlanOperationState.Failed);
        after.Error.ShouldBe("boom");
        after.ResultJson.ShouldBeNull();
        after.AppliedAt.ShouldBeNull();
    }

    [Fact]
    public async Task TryRecordApplied_TwoRacingCallers_ExactlyOneWins()
    {
        // Two workers observing the same Applying row race atomic records — exactly one
        // wins; the other observes state != Applying and returns false. This is the primary
        // guarantee the concurrent two-apply acceptance test rests on at the repository
        // level.
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var opId = plan.Plan.Operations[0].Id;
        await WalkOpToState(plan.Digest, opId, PlanOperationState.Applying);

        var first = await _repo.TryRecordAppliedAsync(plan.Digest, opId, """{"a":1}""", Now());
        var second = await _repo.TryRecordAppliedAsync(plan.Digest, opId, """{"b":2}""", Now());

        (first ^ second).ShouldBeTrue(); // exactly one
        first.ShouldBeTrue();
        second.ShouldBeFalse();

        var after = (await _repo.GetAsync(plan.Digest))!.Operations.Single(o => o.OpId == opId);
        after.State.ShouldBe(PlanOperationState.Applied);
        after.ResultJson.ShouldBe("""{"a":1}"""); // winner's payload retained
    }

    // ─── SaveOperationError ─────────────────────────────────────────────────────

    [Fact]
    public async Task SaveOperationError_FailedIsTerminal_LaterErrorsDoNotOverwrite()
    {
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var opId = plan.Plan.Operations[0].Id;
        var failedAt = Now();

        await _repo.SaveOperationErrorAsync(plan.Digest, opId, "boom", PlanOperationState.Failed, failedAt);

        var firstFailure = (await _repo.GetAsync(plan.Digest))!.Operations.Single(o => o.OpId == opId);
        firstFailure.State.ShouldBe(PlanOperationState.Failed);
        firstFailure.Error.ShouldBe("boom");

        // Terminal: a subsequent error write must not silently move the message.
        await _repo.SaveOperationErrorAsync(plan.Digest, opId, "later", PlanOperationState.Failed, Now().AddHours(1));
        var stillFirst = (await _repo.GetAsync(plan.Digest))!.Operations.Single(o => o.OpId == opId);
        stillFirst.Error.ShouldBe("boom");
    }

    [Fact]
    public async Task SaveOperationError_RefusesNonTerminalFinalState()
    {
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());
        var opId = plan.Plan.Operations[0].Id;

        // An error paired with a non-terminal state would leave the op looking recoverable while
        // the error column claims something already happened. Refuse.
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            _repo.SaveOperationErrorAsync(plan.Digest, opId, "oops", PlanOperationState.Applying, Now()));
    }

    [Fact]
    public async Task Operations_ReturnedInChronologicalOrder()
    {
        // The plan file lists operations in a definite order; apply must walk them in that
        // order. Storing an ORDINAL and reading back ORDER BY ordinal is the contract.
        var plan = BuildManyOpPlan(count: 5);
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());

        var journal = await _repo.GetAsync(plan.Digest);
        journal.ShouldNotBeNull();
        journal!.Operations.Select(o => o.Ordinal).ShouldBe(new[] { 0, 1, 2, 3, 4 });
        journal.Operations.Select(o => o.OpId).ShouldBe(plan.Plan.Operations.Select(o => o.Id).ToArray());
    }

    [Fact]
    public async Task Confirm_MovesHeaderToConfirmed_OnceOnly()
    {
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/p.json", Now());

        var firstConfirmedAt = Now();
        await _repo.ConfirmAsync(plan.Digest, firstConfirmedAt);

        var confirmed = await _repo.GetAsync(plan.Digest);
        confirmed!.State.ShouldBe(PlanOperationState.Confirmed);
        confirmed.ConfirmedAt.ShouldBe(firstConfirmedAt);

        // A second confirmation must not re-stamp ConfirmedAt: the moment the operator said "go"
        // is a fact that a rerun cannot invent.
        await _repo.ConfirmAsync(plan.Digest, Now().AddDays(1));
        (await _repo.GetAsync(plan.Digest))!.ConfirmedAt.ShouldBe(firstConfirmedAt);
    }

    [Fact]
    public async Task JournalSurvivesAMirrorRebuild()
    {
        // 0013's durability test — the plan journal lives in the sibling pending.db, which a
        // mirror drop-and-rebuild must not be able to reach.
        var dir = Path.Combine(Path.GetTempPath(), $"twig-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "twig.db");

        var plan = BuildTwoOpPlan();
        var previewedAt = Now();
        var opId = plan.Plan.Operations[0].Id;
        var startedAt = Now();

        try
        {
            using (var store = new SqliteCacheStore($"Data Source={dbPath}"))
            {
                var repo = new SqlitePlanJournalRepository(store);
                await repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/plans/p.json", previewedAt);
                (await repo.TryTransitionOperationAsync(plan.Digest, opId,
                    PlanOperationState.Planned, PlanOperationState.Applying, startedAt)).ShouldBeTrue();

                // Force the disposable mirror to be dropped and recreated on the next open.
                using var bump = store.GetConnection().CreateCommand();
                bump.CommandText = "UPDATE metadata SET value = '0' WHERE key = 'schema_version';";
                bump.ExecuteNonQuery();
            }

            using (var reopened = new SqliteCacheStore($"Data Source={dbPath}"))
            {
                reopened.SchemaWasRebuilt.ShouldBeTrue("the mirror must actually have been rebuilt");

                var repo = new SqlitePlanJournalRepository(reopened);
                var reloaded = await repo.GetAsync(plan.Digest);
                reloaded.ShouldNotBeNull();
                reloaded!.State.ShouldBe(PlanOperationState.Planned);
                reloaded.PreviewedAt.ShouldBe(previewedAt);
                reloaded.Operations.Count.ShouldBe(2);

                var op = reloaded.Operations.Single(o => o.OpId == opId);
                op.State.ShouldBe(PlanOperationState.Applying);
                op.StartedAt.ShouldBe(startedAt);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Migration_ToProposalTables_PreservesJournalAndOperationRows()
    {
        // AB#742 / design record T2. The bug this test defends against: a migration that
        // recreated the tables under the new names would silently DISCARD real audit
        // history — the pre-migration record of intents twig staged and outcomes it
        // observed. The durable store is the only copy ADO does not hold, so a rebuilt
        // table is unrecoverable data loss.
        //
        // Strategy: open a store (which lands at DurableSchemaVersion 8), then push the
        // schema BACK to its pre-[8] shape — rename the tables and indexes to their
        // old names and reset pending.user_version to 7. Seed a realistic header + two
        // op rows via raw SQL, populating state and every timestamp column, then close.
        // Reopen: SqliteCacheStore runs migration [8] against real data. Assert the
        // rows survive under the new names with every column intact, and that GetAsync
        // still reconstructs the journal through the repository API.
        var dir = Path.Combine(Path.GetTempPath(), $"twig-plan-mig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "twig.db");

        var plan = BuildTwoOpPlan();
        var previewedAt = DateTimeOffset.Parse("2026-08-24T10:00:00Z").ToUniversalTime();
        var confirmedAt = previewedAt.AddMinutes(1);
        var op0StartedAt = previewedAt.AddMinutes(2);
        var op1AppliedAt = previewedAt.AddMinutes(3);
        var op1VerifiedAt = previewedAt.AddMinutes(4);
        var op0Id = plan.Plan.Operations[0].Id;
        var op1Id = plan.Plan.Operations[1].Id;

        try
        {
            using (var store = new SqliteCacheStore($"Data Source={dbPath}"))
            {
                var conn = store.GetConnection();

                // Roll the durable schema back to its pre-[8] shape so we can seed rows
                // via the OLD table names, then let a reopen run migration [8] for real.
                // The [9] audit columns are dropped too: a genuine v7 database never had
                // them, and leaving them would make the reopen's ADD COLUMN collide.
                using (var rollback = conn.CreateCommand())
                {
                    rollback.CommandText = """
                        ALTER TABLE pending.proposal_journals RENAME TO plan_journals;
                        ALTER TABLE pending.proposal_operations RENAME TO plan_operations;
                        DROP INDEX pending.idx_proposal_journals_state;
                        DROP INDEX pending.idx_proposal_operations_ordinal;
                        DROP INDEX pending.idx_proposal_operations_state;
                        CREATE INDEX pending.idx_plan_journals_state ON plan_journals(state);
                        CREATE UNIQUE INDEX pending.idx_plan_operations_ordinal ON plan_operations(digest, ordinal);
                        CREATE INDEX pending.idx_plan_operations_state ON plan_operations(state);
                        ALTER TABLE pending.plan_journals DROP COLUMN authorization_mode;
                        ALTER TABLE pending.plan_journals DROP COLUMN authorizer_identity;
                        ALTER TABLE pending.plan_journals DROP COLUMN rationale;
                        ALTER TABLE pending.plan_journals DROP COLUMN review_model_json;
                        ALTER TABLE pending.plan_journals DROP COLUMN authorized_at;
                        PRAGMA pending.user_version = 7;
                        """;
                    rollback.ExecuteNonQuery();
                }

                // Seed a Confirmed header row.
                using (var header = conn.CreateCommand())
                {
                    header.CommandText = """
                        INSERT INTO plan_journals
                            (digest, schema_version, organization, project, source_path,
                             canonical_json, state, previewed_at, confirmed_at, completed_at, error)
                        VALUES
                            (@digest, 1, 'acme', 'cache', '/plans/p.json',
                             @canonical, 'Confirmed', @previewedAt, @confirmedAt, NULL, NULL);
                        """;
                    header.Parameters.AddWithValue("@digest", plan.Digest);
                    header.Parameters.AddWithValue("@canonical", plan.CanonicalJson);
                    header.Parameters.AddWithValue("@previewedAt", previewedAt.ToString("o"));
                    header.Parameters.AddWithValue("@confirmedAt", confirmedAt.ToString("o"));
                    header.ExecuteNonQuery();
                }

                // Seed two op rows in distinct non-Planned states, each carrying real
                // timestamp columns and — for op-1 — a warning. The migration must
                // preserve every one of these values.
                using (var ops = conn.CreateCommand())
                {
                    ops.CommandText = """
                        INSERT INTO plan_operations
                            (digest, ordinal, op_id, kind, state, request_json,
                             started_at, applied_at, verified_at, result_json, error, warning)
                        VALUES
                            (@digest, 0, @op0Id, 'batch', 'Applying', '{}',
                             @op0StartedAt, NULL, NULL, NULL, NULL, NULL),
                            (@digest, 1, @op1Id, 'batch', 'Verified', '{}',
                             @op1AppliedAt, @op1AppliedAt, @op1VerifiedAt, '{"ok":true}', NULL, 'closedDate normalized');
                        """;
                    ops.Parameters.AddWithValue("@digest", plan.Digest);
                    ops.Parameters.AddWithValue("@op0Id", op0Id);
                    ops.Parameters.AddWithValue("@op1Id", op1Id);
                    ops.Parameters.AddWithValue("@op0StartedAt", op0StartedAt.ToString("o"));
                    ops.Parameters.AddWithValue("@op1AppliedAt", op1AppliedAt.ToString("o"));
                    ops.Parameters.AddWithValue("@op1VerifiedAt", op1VerifiedAt.ToString("o"));
                    ops.ExecuteNonQuery();
                }
            }

            using (var reopened = new SqliteCacheStore($"Data Source={dbPath}"))
            {
                var conn = reopened.GetConnection();

                // Tables carry the new names. The old names are gone.
                AssertDurableTableExists("proposal_journals");
                AssertDurableTableExists("proposal_operations");
                using (var oldGone = conn.CreateCommand())
                {
                    oldGone.CommandText = "SELECT COUNT(*) FROM pending.sqlite_master WHERE type='table' AND name IN ('plan_journals','plan_operations');";
                    Convert.ToInt32(oldGone.ExecuteScalar()).ShouldBe(0);
                }

                // Header row survived with every column intact — state and both stamped
                // timestamps included.
                using (var header = conn.CreateCommand())
                {
                    header.CommandText = """
                        SELECT organization, project, state, previewed_at, confirmed_at, completed_at, error
                        FROM proposal_journals
                        WHERE digest = @digest;
                        """;
                    header.Parameters.AddWithValue("@digest", plan.Digest);
                    using var r = header.ExecuteReader();
                    r.Read().ShouldBeTrue("the pre-migration header row must survive under proposal_journals");
                    r.GetString(0).ShouldBe("acme");
                    r.GetString(1).ShouldBe("cache");
                    r.GetString(2).ShouldBe("Confirmed");
                    DateTimeOffset.Parse(r.GetString(3)).ShouldBe(previewedAt);
                    DateTimeOffset.Parse(r.GetString(4)).ShouldBe(confirmedAt);
                    r.IsDBNull(5).ShouldBeTrue();
                    r.IsDBNull(6).ShouldBeTrue();
                }

                // Both op rows survived. State, every stamped timestamp column, and the
                // warning payload are all readable under the new table name.
                using (var opsCmd = conn.CreateCommand())
                {
                    opsCmd.CommandText = """
                        SELECT ordinal, op_id, state, started_at, applied_at, verified_at, result_json, warning
                        FROM proposal_operations
                        WHERE digest = @digest
                        ORDER BY ordinal;
                        """;
                    opsCmd.Parameters.AddWithValue("@digest", plan.Digest);
                    using var r = opsCmd.ExecuteReader();

                    r.Read().ShouldBeTrue("op 0 must survive");
                    r.GetInt32(0).ShouldBe(0);
                    r.GetString(1).ShouldBe(op0Id);
                    r.GetString(2).ShouldBe("Applying");
                    DateTimeOffset.Parse(r.GetString(3)).ShouldBe(op0StartedAt);
                    r.IsDBNull(4).ShouldBeTrue();
                    r.IsDBNull(5).ShouldBeTrue();
                    r.IsDBNull(7).ShouldBeTrue();

                    r.Read().ShouldBeTrue("op 1 must survive");
                    r.GetInt32(0).ShouldBe(1);
                    r.GetString(1).ShouldBe(op1Id);
                    r.GetString(2).ShouldBe("Verified");
                    DateTimeOffset.Parse(r.GetString(3)).ShouldBe(op1AppliedAt);
                    DateTimeOffset.Parse(r.GetString(4)).ShouldBe(op1AppliedAt);
                    DateTimeOffset.Parse(r.GetString(5)).ShouldBe(op1VerifiedAt);
                    r.GetString(6).ShouldBe("{\"ok\":true}");
                    r.GetString(7).ShouldBe("closedDate normalized");
                }

                // And the cascade FK survived the rename — a delete of the parent still
                // cascades to the child, which is the invariant apply's crash-recovery
                // path relies on.
                using (var fkOn = conn.CreateCommand())
                {
                    fkOn.CommandText = "PRAGMA foreign_keys = ON;";
                    fkOn.ExecuteNonQuery();
                }
                using (var del = conn.CreateCommand())
                {
                    del.CommandText = "DELETE FROM proposal_journals WHERE digest = @digest;";
                    del.Parameters.AddWithValue("@digest", plan.Digest);
                    del.ExecuteNonQuery();
                }
                CountDurableRows(reopened, "proposal_operations", "digest", plan.Digest).ShouldBe(0);

                // Restore the parent + child rows so the repository read path can be
                // verified against surviving audit data. We re-seed via the repository
                // to keep the check honest — GetAsync reading a hand-rolled row proves
                // nothing about the migrated tables' compatibility with the SQL callers
                // actually run.
                var repo = new SqlitePlanJournalRepository(reopened);
                await repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/plans/p.json", previewedAt);
                (await repo.GetAsync(plan.Digest)).ShouldNotBeNull();
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Migration_ToAuthorizationColumns_PreservesHistoricalRowsAndLeavesThemNull()
    {
        // AB#743 / design record T2 §5.3. The bug this test defends against: an audit
        // migration that backfilled its new columns with a default would MANUFACTURE an
        // authorization that never happened, and a reader could never tell the invented
        // record from a real one. The durable store is never dropped, so rows written
        // before authorization was recorded are genuine history — they must survive the
        // migration with every pre-existing column intact and every new column NULL,
        // because NULL is what "predates authorization recording" looks like.
        //
        // Strategy mirrors the [8] test: open a store (which lands at
        // DurableSchemaVersion 9), drop the five audit columns and reset
        // pending.user_version to 8 to recreate a genuine pre-[9] database, seed a
        // realistic header via raw SQL, close, then reopen so migration [9] runs against
        // real data.
        var dir = Path.Combine(Path.GetTempPath(), $"twig-plan-auth-mig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "twig.db");

        var plan = BuildTwoOpPlan();
        var previewedAt = DateTimeOffset.Parse("2026-08-24T10:00:00Z").ToUniversalTime();
        var completedAt = previewedAt.AddMinutes(5);

        try
        {
            using (var store = new SqliteCacheStore($"Data Source={dbPath}"))
            {
                var conn = store.GetConnection();
                using (var rollback = conn.CreateCommand())
                {
                    rollback.CommandText = """
                        ALTER TABLE pending.proposal_journals DROP COLUMN authorization_mode;
                        ALTER TABLE pending.proposal_journals DROP COLUMN authorizer_identity;
                        ALTER TABLE pending.proposal_journals DROP COLUMN rationale;
                        ALTER TABLE pending.proposal_journals DROP COLUMN review_model_json;
                        ALTER TABLE pending.proposal_journals DROP COLUMN authorized_at;
                        PRAGMA pending.user_version = 8;
                        """;
                    rollback.ExecuteNonQuery();
                }

                using (var header = conn.CreateCommand())
                {
                    header.CommandText = """
                        INSERT INTO proposal_journals
                            (digest, schema_version, organization, project, source_path,
                             canonical_json, state, previewed_at, confirmed_at, completed_at, error)
                        VALUES
                            (@digest, 1, 'acme', 'cache', '/plans/p.json',
                             @canonical, 'Verified', @previewedAt, NULL, @completedAt, NULL);
                        """;
                    header.Parameters.AddWithValue("@digest", plan.Digest);
                    header.Parameters.AddWithValue("@canonical", plan.CanonicalJson);
                    header.Parameters.AddWithValue("@previewedAt", previewedAt.ToString("o"));
                    header.Parameters.AddWithValue("@completedAt", completedAt.ToString("o"));
                    header.ExecuteNonQuery();
                }

                using (var ops = conn.CreateCommand())
                {
                    ops.CommandText = """
                        INSERT INTO proposal_operations
                            (digest, ordinal, op_id, kind, state, request_json,
                             started_at, applied_at, verified_at, result_json, error, warning)
                        VALUES
                            (@digest, 0, @op0Id, 'Batch', 'Verified', '{}',
                             @completedAt, @completedAt, @completedAt, '{"ok":true}', NULL, NULL);
                        """;
                    ops.Parameters.AddWithValue("@digest", plan.Digest);
                    ops.Parameters.AddWithValue("@op0Id", plan.Plan.Operations[0].Id);
                    ops.Parameters.AddWithValue("@completedAt", completedAt.ToString("o"));
                    ops.ExecuteNonQuery();
                }
            }

            using (var reopened = new SqliteCacheStore($"Data Source={dbPath}"))
            {
                var conn = reopened.GetConnection();

                using (var version = conn.CreateCommand())
                {
                    version.CommandText = "PRAGMA pending.user_version;";
                    Convert.ToInt32(version.ExecuteScalar()).ShouldBe(SqliteCacheStore.DurableSchemaVersion);
                }

                using (var header = conn.CreateCommand())
                {
                    header.CommandText = """
                        SELECT state, previewed_at, completed_at, canonical_json,
                               authorization_mode, authorizer_identity, rationale,
                               review_model_json, authorized_at
                        FROM proposal_journals
                        WHERE digest = @digest;
                        """;
                    header.Parameters.AddWithValue("@digest", plan.Digest);
                    using var r = header.ExecuteReader();
                    r.Read().ShouldBeTrue("the pre-migration header row must survive migration [9]");
                    r.GetString(0).ShouldBe("Verified");
                    DateTimeOffset.Parse(r.GetString(1)).ShouldBe(previewedAt);
                    DateTimeOffset.Parse(r.GetString(2)).ShouldBe(completedAt);
                    r.GetString(3).ShouldBe(plan.CanonicalJson);

                    // Every audit column is NULL — never a backfilled default.
                    r.IsDBNull(4).ShouldBeTrue();
                    r.IsDBNull(5).ShouldBeTrue();
                    r.IsDBNull(6).ShouldBeTrue();
                    r.IsDBNull(7).ShouldBeTrue();
                    r.IsDBNull(8).ShouldBeTrue();
                }

                CountDurableRows(reopened, "proposal_operations", "digest", plan.Digest).ShouldBe(1);

                // The repository reads the migrated row without inventing an authorization:
                // a historical row reports "no mode recorded", not a mode it guessed.
                var repo = new SqlitePlanJournalRepository(reopened);
                var journal = (await repo.GetAsync(plan.Digest)).ShouldNotBeNull();
                journal.AuthorizationMode.ShouldBeNull();
                journal.AuthorizerIdentity.ShouldBeNull();
                journal.Rationale.ShouldBeNull();
                journal.ReviewModelJson.ShouldBeNull();
                journal.AuthorizedAt.ShouldBeNull();
                journal.Operations.Count.ShouldBe(1);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task RecordAuthorization_PersistsEveryAuditFieldAndReadsBack()
    {
        var plan = BuildTwoOpPlan();
        var previewedAt = Now();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/plans/p.json", previewedAt);

        var authorizedAt = DateTimeOffset.Parse("2026-08-27T12:34:56Z").ToUniversalTime();
        var authorization = new ProposalAuthorization
        {
            Digest = plan.Digest,
            Mode = ProposalAuthorizationMode.Model,
            AuthorizerIdentity = "twig-agent",
            Rationale = "Blockers cleared; operations match the ticket.",
            AuthorizedAt = authorizedAt,
        };

        await _repo.RecordAuthorizationAsync(plan.Digest, authorization, """{"model":"twig.change-proposal.review"}""");

        var journal = (await _repo.GetAsync(plan.Digest)).ShouldNotBeNull();
        journal.AuthorizationMode.ShouldBe(ProposalAuthorizationMode.Model);
        journal.AuthorizerIdentity.ShouldBe("twig-agent");
        journal.Rationale.ShouldBe("Blockers cleared; operations match the ticket.");
        journal.ReviewModelJson.ShouldBe("""{"model":"twig.change-proposal.review"}""");
        journal.AuthorizedAt.ShouldBe(authorizedAt);

        // The proposal itself is untouched: canonical_json is what was authorized, and the
        // review model is stored beside it rather than over it.
        journal.CanonicalJson.ShouldBe(plan.CanonicalJson);
        journal.Digest.ShouldBe(plan.Digest);
    }

    [Fact]
    public async Task RecordAuthorization_IsWriteOnce_SoAResumedApplyCannotRewriteHistory()
    {
        // Defends against: an apply resumed after a crash overwriting the authorization that
        // actually released the proposal with the moment someone restarted the run — which
        // would erase the only record of who authorized the mutation.
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/plans/p.json", Now());

        var first = new ProposalAuthorization
        {
            Digest = plan.Digest,
            Mode = ProposalAuthorizationMode.Human,
            AuthorizerIdentity = "Daniel Green",
            Rationale = "original",
            AuthorizedAt = DateTimeOffset.Parse("2026-08-27T09:00:00Z").ToUniversalTime(),
        };
        await _repo.RecordAuthorizationAsync(plan.Digest, first, """{"first":true}""");

        await _repo.RecordAuthorizationAsync(
            plan.Digest,
            first with
            {
                Mode = ProposalAuthorizationMode.Model,
                AuthorizerIdentity = "someone-else",
                Rationale = "resumed",
                AuthorizedAt = DateTimeOffset.Parse("2026-08-27T18:00:00Z").ToUniversalTime(),
            },
            """{"second":true}""");

        var journal = (await _repo.GetAsync(plan.Digest)).ShouldNotBeNull();
        journal.AuthorizationMode.ShouldBe(ProposalAuthorizationMode.Human);
        journal.AuthorizerIdentity.ShouldBe("Daniel Green");
        journal.Rationale.ShouldBe("original");
        journal.ReviewModelJson.ShouldBe("""{"first":true}""");
        journal.AuthorizedAt.ShouldBe(first.AuthorizedAt);
    }

    // ─── helpers ────────────────────────────────────────────────────────────────

    private static DateTimeOffset Now() => DateTimeOffset.UtcNow;

    private async Task WalkOpToState(string digest, string opId, PlanOperationState target)
    {
        // State-walker for the SaveOperationResult tests. Places a row into a known
        // non-terminal state via legitimate CAS transitions.
        if (target == PlanOperationState.Planned)
            return;
        (await _repo.TryTransitionOperationAsync(digest, opId, PlanOperationState.Planned, PlanOperationState.Confirmed, Now())).ShouldBeTrue();
        if (target == PlanOperationState.Confirmed)
            return;
        (await _repo.TryTransitionOperationAsync(digest, opId, PlanOperationState.Confirmed, PlanOperationState.Applying, Now())).ShouldBeTrue();
        if (target == PlanOperationState.Applying)
            return;
        (await _repo.TryTransitionOperationAsync(digest, opId, PlanOperationState.Applying, PlanOperationState.Applied, Now())).ShouldBeTrue();
        if (target == PlanOperationState.Applied)
            return;
        throw new ArgumentOutOfRangeException(nameof(target), $"Test helper does not walk to {target}.");
    }

    private Task WalkOpToApplied(string digest, string opId) => WalkOpToState(digest, opId, PlanOperationState.Applied);

    // ── AB#832: inverse lookup by source path ──────────────────────────────

    /// <summary>
    /// A plan file is single-use, so its path legitimately carries exactly one digest for its
    /// whole life. More than one means the file was overwritten — the inverse lookup is what
    /// lets the lifecycle name that instead of silently answering about whichever bytes
    /// happen to be on disk.
    /// </summary>
    [Fact]
    public async Task GetDigestsBySourcePath_ReturnsEveryDigestJournaledAgainstThePath_OldestFirst()
    {
        var original = BuildTwoOpPlan();
        var replacement = PlanFixture.FromSource("""
            {
              "version": 1,
              "workspace": { "organization": "acme", "project": "cache" },
              "operations": [
                { "id": "op-1", "kind": "batch", "workItemId": 831, "expectedRevision": 7,
                  "fields": { "System.State": "Closed" } }
              ]
            }
            """);

        await _repo.ImportAsync(original, original.CanonicalJson, original.Digest, "/plans/020.json", Now());
        await _repo.ImportAsync(
            replacement, replacement.CanonicalJson, replacement.Digest, "/plans/020.json", Now().AddMinutes(5));

        var digests = await _repo.GetDigestsBySourcePathAsync("/plans/020.json");

        digests.ShouldBe([original.Digest, replacement.Digest]);
    }

    [Fact]
    public async Task GetDigestsBySourcePath_DoesNotBleedAcrossPaths()
    {
        var plan = BuildTwoOpPlan();
        await _repo.ImportAsync(plan, plan.CanonicalJson, plan.Digest, "/plans/020.json", Now());

        (await _repo.GetDigestsBySourcePathAsync("/plans/021.json")).ShouldBeEmpty();
    }

    private static PlanFixture BuildTwoOpPlan()
    {
        // Real plan v1 canonical vocabulary — batch, add-link, etc. — round-tripped through
        // PlanDocumentParser so the fixture's (canonical, digest) pair is exactly what the
        // repository will re-check at the boundary. A hand-rolled canonicalizer here would
        // drift from PlanCanonicalizer and give every ImportAsync test a false-positive.
        const string source = """
            {
              "version": 1,
              "workspace": { "organization": "acme", "project": "cache" },
              "operations": [
                { "id": "op-1", "kind": "batch", "workItemId": 100, "expectedRevision": 3,
                  "fields": { "System.State": "Active", "System.Title": "First" } },
                { "id": "op-2", "kind": "batch", "workItemId": 101, "expectedRevision": 5,
                  "fields": { "System.State": "Closed" } }
              ]
            }
            """;
        return PlanFixture.FromSource(source);
    }

    private static PlanFixture BuildLinkPlan()
    {
        const string source = """
            {
              "version": 1,
              "workspace": { "organization": "acme", "project": "cache" },
              "operations": [
                { "id": "L", "kind": "add-link", "workItemId": 1, "expectedRevision": 2,
                  "relation": "successor", "otherId": 9 }
              ]
            }
            """;
        return PlanFixture.FromSource(source);
    }

    private static PlanFixture BuildPublishSeedPlan()
    {
        // publish-seed requires a real staged identity (GUIDv7). We hard-code a stable one
        // so the digest is reproducible across test runs even though the identity is opaque.
        const string source = """
            {
              "version": 1,
              "workspace": { "organization": "acme", "project": "cache" },
              "operations": [
                { "id": "S", "kind": "publish-seed",
                  "stagedIdentity": "01947f00-0000-7000-8000-000000000001",
                  "expectedFingerprint": "aa" }
              ]
            }
            """;
        return PlanFixture.FromSource(source);
    }

    private static PlanFixture BuildMixedPlan()
    {
        // Mixed-subtype canonical plan carrying one of every operation kind (batch, add-link,
        // remove-link, publish-seed, delete). Feeds the semantic-comparator mutation matrix so
        // every branch of the binding can be exercised against a single (canonical, digest)
        // pair.
        const string source = """
            {
              "version": 1,
              "workspace": { "organization": "acme", "project": "cache" },
              "operations": [
                { "id": "op-batch", "kind": "batch", "workItemId": 100, "expectedRevision": 3,
                  "fields": { "System.State": "Active", "System.Title": "First" } },
                { "id": "op-add",   "kind": "add-link", "workItemId": 1, "expectedRevision": 2,
                  "relation": "successor", "otherId": 9 },
                { "id": "op-rem",   "kind": "remove-link", "workItemId": 2, "expectedRevision": 4,
                  "relation": "predecessor", "otherId": 11 },
                { "id": "op-seed",  "kind": "publish-seed",
                  "stagedIdentity": "01947f00-0000-7000-8000-000000000001",
                  "expectedFingerprint": "ff" },
                { "id": "op-del",   "kind": "delete", "workItemId": 5, "expectedRevision": 6 }
              ]
            }
            """;
        return PlanFixture.FromSource(source);
    }

    private static PlanFixture BuildManyOpPlan(int count)
    {
        var ops = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            ops.Add($$"""
                { "id": "op-{{i}}", "kind": "batch", "workItemId": {{1000 + i}},
                  "expectedRevision": 1, "fields": { "System.Title": "row {{i}}" } }
                """);
        }
        var source = $$"""
            {
              "version": 1,
              "workspace": { "organization": "acme", "project": "cache" },
              "operations": [{{string.Join(",", ops)}}]
            }
            """;
        return PlanFixture.FromSource(source);
    }

    private void AssertDurableTableExists(string table)
    {
        using var cmd = _store.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT name FROM pending.sqlite_master WHERE type='table' AND name=@name;";
        cmd.Parameters.AddWithValue("@name", table);
        cmd.ExecuteScalar().ShouldBe(table);
    }

    private void AssertDurableIndexExists(string index)
    {
        using var cmd = _store.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT name FROM pending.sqlite_master WHERE type='index' AND name=@name;";
        cmd.Parameters.AddWithValue("@name", index);
        cmd.ExecuteScalar().ShouldBe(index);
    }

    private static int CountDurableRows(SqliteCacheStore store, string table, string column, string value)
    {
        using var cmd = store.GetConnection().CreateCommand();
        // Table and column names are compile-time constants from THIS test file — no injection surface.
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = @value;";
        cmd.Parameters.AddWithValue("@value", value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void InsertRawPlanJournalRow(string digest, string canonicalJson)
    {
        // Bypass the repository to seed a row whose (digest, canonical_json) pair would never
        // be producible through ImportAsync. Only the defense-in-depth test uses this — every
        // other test drives the API.
        using var cmd = _store.GetConnection().CreateCommand();
        cmd.CommandText = """
            INSERT INTO proposal_journals
                (digest, schema_version, organization, project, source_path,
                 canonical_json, state, previewed_at, confirmed_at, completed_at, error)
            VALUES
                (@digest, 1, 'seeded', 'seeded', '/seeded',
                 @canonical, 'Planned', @previewedAt, NULL, NULL, NULL);
            """;
        cmd.Parameters.AddWithValue("@digest", digest);
        cmd.Parameters.AddWithValue("@canonical", canonicalJson);
        cmd.Parameters.AddWithValue("@previewedAt", Now().ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// A parser-produced plan artifact: canonical bytes, digest, and PlanDefinition all come
    /// from <see cref="PlanDocumentParser"/> so tests exercise exactly the same (canonical,
    /// digest) binding the repository enforces at the boundary. Implicitly converts to
    /// <see cref="PlanDefinition"/> for concise call sites.
    /// </summary>
    private sealed class PlanFixture
    {
        public string CanonicalJson { get; }
        public string Digest { get; }
        public PlanDefinition Plan { get; }

        private PlanFixture(PlanDefinition plan, string canonicalJson, string digest)
        {
            Plan = plan;
            CanonicalJson = canonicalJson;
            Digest = digest;
        }

        public static PlanFixture FromSource(string sourceJson)
        {
            var result = new PlanDocumentParser().Parse(sourceJson);
            if (!result.IsValid || result.Plan is null || result.CanonicalJson is null || result.Digest is null)
            {
                var first = result.Issues.FirstOrDefault();
                throw new InvalidOperationException(
                    $"Test plan source failed to parse: {first?.Code} at {first?.Path} — {first?.Message}");
            }
            return new PlanFixture(result.Plan, result.CanonicalJson, result.Digest);
        }

        public static implicit operator PlanDefinition(PlanFixture fixture) => fixture.Plan;
    }
}
