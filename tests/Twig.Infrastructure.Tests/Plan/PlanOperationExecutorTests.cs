using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.Plan;

/// <summary>
/// Focused tests for the review blockers on <see cref="PlanOperationExecutor"/> and its
/// <see cref="PlanSeedPublisher"/> seed helper: canonical batch-field resolution,
/// deterministic strict-CAS relation failure, seed alias-to-identity drift, publish
/// intent/map recovery agreement, and seed link warning classification. Each fake is
/// stubbed to exercise exactly one classification so a regression fails one test rather
/// than a big integration matrix.
/// </summary>
public sealed class PlanOperationExecutorTests
{
    private readonly IAdoWorkItemService _ado = Substitute.For<IAdoWorkItemService>();
    private readonly IRevisionBoundAdoWorkItemService _revisionBound = Substitute.For<IRevisionBoundAdoWorkItemService>();
    private readonly IFieldDefinitionStore _fieldDefinitions = Substitute.For<IFieldDefinitionStore>();
    private readonly IWorkItemRepository _workItems = Substitute.For<IWorkItemRepository>();
    private readonly ISeedLinkRepository _seedLinks = Substitute.For<ISeedLinkRepository>();
    private readonly IStagedIdentityRegistry _stagedRegistry = Substitute.For<IStagedIdentityRegistry>();
    private readonly IPublishIdMapRepository _publishIdMap = Substitute.For<IPublishIdMapRepository>();
    private readonly IPublishIntentRepository _publishIntent = Substitute.For<IPublishIntentRepository>();
    private readonly PlanOperationExecutor _executor;
    private readonly List<int> _publishInvocations = new();
    private Func<int, SeedPublishResult> _publishBehaviour = _ => new SeedPublishResult
    {
        Status = SeedPublishStatus.Error,
        ErrorMessage = "PlanSeedPublisher invoked publish unexpectedly.",
    };

    public PlanOperationExecutorTests()
    {
        // Baseline stubs for the collaborators the seed publisher ALWAYS touches, so tests
        // opt-in to overrides for the invariants they exercise. NSubstitute's default
        // Task<IReadOnly...> return is a null-wrapping task; every reachable path here calls
        // GetAllMappingsAsync (via SeedFingerprintCalculator) or GetLinksForItemAsync, so a
        // null return there is uncaught nullref inside the calculator, not a test signal.
        _publishIdMap.GetAllMappingsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<PublishMapping>)Array.Empty<PublishMapping>());
        _seedLinks.GetLinksForItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedLink>)Array.Empty<SeedLink>());

        var publisher = new PlanSeedPublisher(
            _ado, _workItems, _seedLinks, _stagedRegistry, _publishIdMap, _publishIntent,
            (seedId, _) =>
            {
                _publishInvocations.Add(seedId);
                return Task.FromResult(_publishBehaviour(seedId));
            });
        _executor = new PlanOperationExecutor(_ado, _revisionBound, _fieldDefinitions, publisher);
    }

    private void StubFieldDefinition(string referenceName, string dataType)
    {
        _fieldDefinitions.GetByReferenceNameAsync(referenceName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FieldDefinition?>(
                new FieldDefinition(referenceName, referenceName, dataType, IsReadOnly: false)));
    }

    /// <summary>
    /// Stubs a field ADO declares as an identity. Deliberately typed <c>string</c>: that is
    /// what ADO actually reports for identity fields, so a fixture typing them anything
    /// else would not exercise the real discrimination (AB#802).
    /// </summary>
    private void StubIdentityField(string referenceName)
    {
        _fieldDefinitions.GetByReferenceNameAsync(referenceName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FieldDefinition?>(
                new FieldDefinition(referenceName, referenceName, "string", IsReadOnly: false)
                {
                    IsIdentity = true,
                }));
    }


    // ── batch readback: canonical fields ───────────────────────────────────

    [Fact]
    public async Task ReadbackBatch_StateResolvedFromProperty_WhenFieldsMapMissing()
    {
        // The ADO response mapper is authoritative on canonical core fields; the arbitrary
        // Fields dictionary is a mirror the readback should never depend on. If the mapper
        // populated State but not Fields, the batch must still verify.
        var op = new BatchOperation
        {
            Id = "b",
            WorkItemId = 42,
            ExpectedRevision = 3,
            Fields = new Dictionary<string, string?> { ["System.State"] = "Active" },
        };
        var wi = new WorkItem { Id = 42, Title = "T" };
        wi.MarkSynced(4);
        wi.ChangeState("Active"); // sets property; ImportFields is NOT called → Fields dict empty
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadbackBatch_NullExpected_VerifiesAbsentAndEmpty()
    {
        // A plan value of null asks ADO to clear the field. Both absence AND empty string
        // are legitimate representations of "cleared" — either way the readback verifies.
        var op = new BatchOperation
        {
            Id = "b",
            WorkItemId = 1,
            ExpectedRevision = 1,
            Fields = new Dictionary<string, string?>
            {
                ["System.AssignedTo"] = null, // absent from wi
                ["Custom.Reviewer"] = null,   // present but empty
            },
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(2);
        wi.UpdateField("Custom.Reviewer", string.Empty);
        _ado.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadbackBatch_NullExpected_FailsWhenValuePresent()
    {
        var op = new BatchOperation
        {
            Id = "b",
            WorkItemId = 1,
            ExpectedRevision = 1,
            Fields = new Dictionary<string, string?> { ["System.State"] = null },
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(2);
        wi.ChangeState("Active");
        _ado.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Error!.ShouldContain("cleared");
    }

    [Fact]
    public async Task ReadbackBatch_SystemDescriptionHtml_VerifiesEquivalentAdoNormalization()
    {
        const string expected = "<p data-kind=\"notice\" class=\"summary\">Ready &amp; waiting<br /></p>";
        const string actual = "<P class='summary' data-kind=notice>Ready &#38; waiting<br></P>";

        StubFieldDefinition("System.Description", "HTML");
        var op = new BatchOperation
        {
            Id = "html", WorkItemId = 1, ExpectedRevision = 1,
            Fields = new Dictionary<string, string?> { ["System.Description"] = expected },
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(2);
        wi.UpdateField("System.Description", actual);
        _ado.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        // AB#755: equivalence is not enough — the normalization must be RECORDED, or the
        // ledger silently loses the fact that ADO rewrote the markup.
        outcome.Warning.ShouldNotBeNull();
        outcome.Warning.ShouldContain("System.Description");
        outcome.Warning.ShouldContain("canonicalized HTML");
        await _fieldDefinitions.Received(1).GetByReferenceNameAsync("System.Description", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadbackBatch_CustomHtmlField_VerifiesEquivalentAdoNormalization()
    {
        const string expected = "<a href=https://example.test/path data-level=\"1\">Verified</a>";
        const string actual = "<A data-level='1' href=\"https://example.test/path\">Verified</A>";

        StubFieldDefinition("Custom.WayfinderAnswer", "html");
        var op = new BatchOperation
        {
            Id = "html", WorkItemId = 1, ExpectedRevision = 1,
            Fields = new Dictionary<string, string?> { ["Custom.WayfinderAnswer"] = expected },
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(2);
        wi.UpdateField("Custom.WayfinderAnswer", actual);
        _ado.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.Warning.ShouldNotBeNull();
        outcome.Warning.ShouldContain("Custom.WayfinderAnswer");
        await _fieldDefinitions.Received(1).GetByReferenceNameAsync("Custom.WayfinderAnswer", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadbackBatch_HtmlSemanticDifference_RemainsIndeterminate()
    {
        StubFieldDefinition("System.Description", "html");
        var op = new BatchOperation
        {
            Id = "html", WorkItemId = 1, ExpectedRevision = 1,
            Fields = new Dictionary<string, string?> { ["System.Description"] = "<p><strong>Verified</strong></p>" },
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(2);
        wi.UpdateField("System.Description", "<p><em>Verified</em></p>");
        _ado.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Error!.ShouldContain("System.Description");
    }

    // ── batch readback: ADO-normalized identities (AB#802) ─────────────────

    /// <summary>
    /// The exact AB#802 reproduction, observed live on 2026-08-26 claiming work item 727:
    /// the plan staged the email form, ADO stored the display form, the revision advanced,
    /// and the readback still reported the op as Indeterminate.
    /// </summary>
    [Fact]
    public async Task ReadbackBatch_IdentityStagedAsEmail_VerifiesAgainstAdoDisplayForm()
    {
        StubIdentityField("System.AssignedTo");
        var op = new BatchOperation
        {
            Id = "claim", WorkItemId = 727, ExpectedRevision = 4,
            Fields = new Dictionary<string, string?>
            {
                ["System.AssignedTo"] = "daniel@danielgreen.net",
            },
        };
        var wi = new WorkItem { Id = 727, Title = "T" };
        wi.MarkSynced(5);
        wi.UpdateField("System.AssignedTo", "Daniel Green (daniel danielgreen.net)");
        _ado.FetchAsync(727, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        // Equivalence alone is not enough: the ledger must record WHICH account ADO
        // resolved the write to, or the rewrite disappears from the audit trail.
        outcome.Warning.ShouldNotBeNull();
        outcome.Warning.ShouldContain("System.AssignedTo");
        outcome.Warning.ShouldContain("identity");
        outcome.Warning.ShouldContain("Daniel Green (daniel danielgreen.net)");
        await _fieldDefinitions.Received(1)
            .GetByReferenceNameAsync("System.AssignedTo", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The comparison is driven by field metadata, never by the value's shape. A field ADO
    /// does NOT declare as an identity keeps ordinal comparison even when both values look
    /// exactly like identity renderings.
    /// </summary>
    [Fact]
    public async Task ReadbackBatch_IdentityShapedValueOnNonIdentityField_RemainsIndeterminate()
    {
        StubFieldDefinition("Custom.FreeText", "string");
        var op = new BatchOperation
        {
            Id = "text", WorkItemId = 1, ExpectedRevision = 1,
            Fields = new Dictionary<string, string?>
            {
                ["Custom.FreeText"] = "daniel@danielgreen.net",
            },
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(2);
        wi.UpdateField("Custom.FreeText", "Daniel Green (daniel danielgreen.net)");
        _ado.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Error!.ShouldContain("Custom.FreeText");
    }

    /// <summary>
    /// A DIFFERENT account is a contradiction, not a normalization. This is the assertion
    /// that stops AB#802's fix from becoming a blanket excuse for identity differences.
    /// </summary>
    [Fact]
    public async Task ReadbackBatch_DifferentIdentity_RemainsIndeterminate()
    {
        StubIdentityField("System.AssignedTo");
        var op = new BatchOperation
        {
            Id = "claim", WorkItemId = 1, ExpectedRevision = 1,
            Fields = new Dictionary<string, string?>
            {
                ["System.AssignedTo"] = "daniel@danielgreen.net",
            },
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(2);
        wi.UpdateField("System.AssignedTo", "Someone Else (someone elsewhere.net)");
        _ado.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Error!.ShouldContain("System.AssignedTo");
    }

    /// <summary>
    /// The unproven-mutation guard sits ABOVE the identity comparator exactly as it does
    /// for html and server-generated stamps: no revision advance, no warning-verify.
    /// </summary>
    [Fact]
    public async Task ReadbackBatch_IdentityNormalizedButStaleReadback_RemainsIndeterminate()
    {
        StubIdentityField("System.AssignedTo");
        var op = new BatchOperation
        {
            Id = "claim", WorkItemId = 1, ExpectedRevision = 5,
            Fields = new Dictionary<string, string?>
            {
                ["System.AssignedTo"] = "daniel@danielgreen.net",
            },
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(5); // did NOT advance past ExpectedRevision
        wi.UpdateField("System.AssignedTo", "Daniel Green (daniel danielgreen.net)");
        _ado.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Error!.ShouldContain("revision");
    }

    /// <summary>
    /// A requested CLEAR on an identity field is never excused by the comparator — a field
    /// that still holds a value did not clear, whatever it renders as.
    /// </summary>
    [Fact]
    public async Task ReadbackBatch_IdentityClearRequestedButStillHeld_RemainsIndeterminate()
    {
        StubIdentityField("System.AssignedTo");
        var op = new BatchOperation
        {
            Id = "release", WorkItemId = 1, ExpectedRevision = 1,
            Fields = new Dictionary<string, string?> { ["System.AssignedTo"] = null },
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(2);
        wi.UpdateField("System.AssignedTo", "Daniel Green (daniel danielgreen.net)");
        _ado.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Error!.ShouldContain("cleared");
    }

    [Fact]
    public async Task ReadbackBatch_UnknownField_RemainsOrdinal()
    {
        _fieldDefinitions.GetByReferenceNameAsync("System.Tags", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FieldDefinition?>(null));
        var op = new BatchOperation
        {
            Id = "plain", WorkItemId = 1, ExpectedRevision = 1,
            Fields = new Dictionary<string, string?> { ["System.Tags"] = "<P>literal</P>" },
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(2);
        wi.UpdateField("System.Tags", "<p>literal</p>");
        _ado.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
    }

    [Fact]
    public async Task ReadbackBatch_HtmlLookingPlainText_RemainsOrdinal()
    {
        StubFieldDefinition("Custom.Template", "plainText");
        var op = new BatchOperation
        {
            Id = "plain", WorkItemId = 1, ExpectedRevision = 1,
            Fields = new Dictionary<string, string?> { ["Custom.Template"] = "<P>literal</P>" },
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(2);
        wi.UpdateField("Custom.Template", "<p>literal</p>");
        _ado.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
    }

    // ── link readback: friendly-relation normalization ─────────────────────

    [Fact]
    public async Task ReadbackAddLink_MatchesWhenAdoReturnsFriendlyLinkType()
    {
        // Production AdoResponseMapper normalises non-hierarchy relations to friendly
        // short names ("Successor"). The plan carries "successor". Case-insensitive
        // ordinal match already handles this identity — the test pins it.
        var op = new AddLinkOperation
        {
            Id = "L", WorkItemId = 1, OtherId = 9, ExpectedRevision = 2, Relation = "successor",
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(3);
        _ado.FetchWithLinksAsync(1, Arg.Any<CancellationToken>()).Returns(
            (wi, (IReadOnlyList<WorkItemLink>)new[] { new WorkItemLink(1, 9, "Successor") }));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadbackAddLink_MatchesWhenAdoReturnsRawAdoRelation()
    {
        // Some paths still surface the raw ADO relation reference name. The readback
        // must recognise both forms and NOT report the edge missing.
        var op = new AddLinkOperation
        {
            Id = "L", WorkItemId = 1, OtherId = 9, ExpectedRevision = 2, Relation = "successor",
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(3);
        _ado.FetchWithLinksAsync(1, Arg.Any<CancellationToken>()).Returns(
            (wi, (IReadOnlyList<WorkItemLink>)new[]
            {
                new WorkItemLink(1, 9, "System.LinkTypes.Dependency-Forward"),
            }));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
    }

    // ── batch execute: determinate ADO rejection ───────────────────────────

    [Fact]
    public async Task ExecuteBatch_AdoBadRequest_IsDeterministicFailure()
    {
        var op = new BatchOperation
        {
            Id = "b",
            WorkItemId = 720,
            ExpectedRevision = 2,
            Fields = new Dictionary<string, string?>
            {
                ["System.AssignedTo"] = "Unknown identity",
                ["System.State"] = "Doing",
            },
        };
        _ado.PatchAsync(720, Arg.Any<IReadOnlyList<FieldChange>>(), 2,
                Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new AdoBadRequestException(
                "The identity value 'Unknown identity' for field 'Assigned To' is an unknown identity."));

        var outcome = await _executor.ExecuteAsync(op, CancellationToken.None);

        outcome.Outcome.ShouldBe(PlanExecutionOutcome.Failed);
        outcome.Error.ShouldNotBeNull();
        outcome.Error.ShouldContain("unknown identity");
        await _ado.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecutePublishSeed_AdoBadRequest_RemainsIndeterminateForRecovery()
    {
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000777"));
        var alias = MakeAlias(-77);
        var seed = new WorkItem
        {
            Id = alias.Value,
            Title = "seed",
            Type = WorkItemType.Parse("Task").Value,
            IsSeed = true,
            StagedIdentity = identity,
        };
        seed.MarkSynced(1);
        _stagedRegistry.FindAliasAsync(identity, Arg.Any<CancellationToken>()).Returns(alias);
        _workItems.GetByIdAsync(alias.Value, Arg.Any<CancellationToken>()).Returns(seed);
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns((int?)null);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>()).Returns((PublishIntent?)null);
        var fingerprint = await SeedFingerprintCalculator.ComputeAsync(
            seed, [], _stagedRegistry, _publishIdMap, CancellationToken.None);
        var op = new PublishSeedOperation
        {
            Id = "S",
            StagedIdentity = identity,
            ExpectedFingerprint = fingerprint,
        };
        _publishBehaviour = _ => throw new AdoBadRequestException("post-create promotion failed");

        var outcome = await _executor.ExecuteAsync(op, CancellationToken.None);

        outcome.Outcome.ShouldBe(PlanExecutionOutcome.Indeterminate);
        outcome.Error.ShouldBe("post-create promotion failed");
    }

    // ── strict-CAS relation not found ──────────────────────────────────────

    [Fact]
    public async Task ExecuteRemoveLink_MissingRelation_IsDeterministicFailure()
    {
        // Strict-CAS remove refuses when the exact (rel, target) is not present at the
        // expected revision. That is a plan-shape violation, not an ambient uncertainty:
        // no readback resurrects a link the server said did not exist. Mapping through
        // AdoException would leak as Indeterminate — the specialised exception fixes it.
        var op = new RemoveLinkOperation
        {
            Id = "L", WorkItemId = 1, OtherId = 9, ExpectedRevision = 2, Relation = "successor",
        };
        _revisionBound.RemoveLinkAtRevisionAsync(1, "System.LinkTypes.Dependency-Forward", 9, 2,
                Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new AdoRelationNotFoundException(
                1, "System.LinkTypes.Dependency-Forward", 9, 2));

        var result = await _executor.ExecuteAsync(op, CancellationToken.None);

        result.Outcome.ShouldBe(PlanExecutionOutcome.Failed);
        result.Error!.ShouldContain("not present");
    }

    [Fact]
    public async Task ExecuteRemoveLink_MissingParent_IsDeterministicFailure()
    {
        // Unparent-of-nothing rides the same seam and MUST be determinate. Parent maps to
        // Hierarchy-Reverse; the strict-CAS surface throws the same exception.
        var op = new RemoveLinkOperation
        {
            Id = "L", WorkItemId = 1, OtherId = 5, ExpectedRevision = 2, Relation = "parent",
        };
        _revisionBound.RemoveLinkAtRevisionAsync(1, "System.LinkTypes.Hierarchy-Reverse", 5, 2,
                Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new AdoRelationNotFoundException(
                1, "System.LinkTypes.Hierarchy-Reverse", 5, 2));

        var result = await _executor.ExecuteAsync(op, CancellationToken.None);

        result.Outcome.ShouldBe(PlanExecutionOutcome.Failed);
    }

    // ── seed publish: cached identity drift ────────────────────────────────

    [Fact]
    public async Task ExecutePublishSeed_CachedIdentityMismatch_FailsBeforeFingerprint()
    {
        // A cache rebuild reissued the alias to a different staged identity than the plan
        // named. The fingerprint below could still coincidentally match; refuse on the
        // identity mismatch itself so a stale plan cannot publish the wrong seed.
        var planned = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000101"));
        var other = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000102"));
        var alias = MakeAlias(-42);
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = planned, ExpectedFingerprint = "irrelevant",
        };

        _publishIdMap.GetNewIdAsync(planned, Arg.Any<CancellationToken>()).Returns((int?)null);
        _publishIntent.GetIntentAsync(planned, Arg.Any<CancellationToken>()).Returns((PublishIntent?)null);
        _stagedRegistry.FindAliasAsync(planned, Arg.Any<CancellationToken>()).Returns(alias);
        _workItems.GetByIdAsync(alias.Value, Arg.Any<CancellationToken>())
            .Returns(new WorkItem
            {
                Id = alias.Value,
                Title = "seed",
                Type = WorkItemType.Parse("Task").Value,
                IsSeed = true,
                StagedIdentity = other, // <-- drift
            });

        var result = await _executor.ExecuteAsync(op, CancellationToken.None);

        result.Outcome.ShouldBe(PlanExecutionOutcome.Failed);
        result.Error!.ShouldContain("identity");
    }

    // ── seed publish: fresh Confirmed fingerprint-first ordering ──────────

    [Fact]
    public async Task ExecutePublishSeed_ExternalPrepublishAfterEdit_FailsClosed()
    {
        // The seed drifted locally after the plan was captured and a map row already
        // exists for this identity — an external publish, or a stale MappedPublish that no
        // longer describes the seed the plan named. Under the old check-map-first ordering
        // the executor would MappedPublish onto that row; the new ordering computes the
        // fingerprint over the current cache FIRST and fails closed on the drift.
        var planned = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000401"));
        var alias = MakeAlias(-42);
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = planned, ExpectedFingerprint = "captured-at-plan-time",
        };
        _stagedRegistry.FindAliasAsync(planned, Arg.Any<CancellationToken>()).Returns(alias);
        _workItems.GetByIdAsync(alias.Value, Arg.Any<CancellationToken>())
            .Returns(new WorkItem
            {
                Id = alias.Value,
                Title = "edited-after-plan",
                Type = WorkItemType.Parse("Task").Value,
                IsSeed = true,
                StagedIdentity = planned,
            });
        // A map row that would previously have been ratified without any fingerprint check.
        _publishIdMap.GetNewIdAsync(planned, Arg.Any<CancellationToken>()).Returns(9999);
        _publishIntent.GetIntentAsync(planned, Arg.Any<CancellationToken>()).Returns((PublishIntent?)null);

        var result = await _executor.ExecuteAsync(op, CancellationToken.None);

        result.Outcome.ShouldBe(PlanExecutionOutcome.Failed);
        result.Error!.ShouldContain("drift");
        // The map lookup MUST NOT short-circuit before fingerprint attestation.
        result.MappedPublishId.ShouldBeNull();
        _publishInvocations.ShouldBeEmpty();
    }


    // ── seed publish readback: intent/map recovery agreement ───────────────

    [Fact]
    public async Task ReadbackPublishSeed_MapAndIntentAgree_VerifiesRemote()
    {
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000201"));
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns(1234);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>())
            .Returns(new PublishIntent
            {
                Identity = identity, Title = "T", TypeName = "Task",
                RecordedAt = DateTimeOffset.UtcNow, PublishedId = 1234,
                CompletedAt = DateTimeOffset.UtcNow,
            });
        var wi = new WorkItem { Id = 1234, Title = "T" };
        wi.MarkSynced(1);
        _ado.FetchWithLinksAsync(1234, Arg.Any<CancellationToken>())
            .Returns((wi, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.Deterministic.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadbackPublishSeed_MapAndIntentDisagree_FailsDeterministically()
    {
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000202"));
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns(1234);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>())
            .Returns(new PublishIntent
            {
                Identity = identity, Title = "T", TypeName = "Task",
                RecordedAt = DateTimeOffset.UtcNow, PublishedId = 9999,
                CompletedAt = DateTimeOffset.UtcNow,
            });

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Deterministic.ShouldBeTrue();
        outcome.Error!.ShouldContain("disagree");
    }

    [Fact]
    public async Task ReadbackPublishSeed_IntentOnly_InvokesOrchestratorAndDoesNotDuplicate()
    {
        // Crash between wire (step 7) and local UoW (step 10): the intent completed with a
        // real ADO id but the id map never landed. Recovery re-drives the orchestrator —
        // the completed intent forces step 7 to skip CreateAsync (idempotent by contract),
        // and step 10a lands the missed map row. The readback then verifies remote against
        // that recovered map.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000203"));
        var alias = MakeAlias(-7);
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        var seed = new WorkItem
        {
            Id = alias.Value, Title = "T", Type = WorkItemType.Parse("Task").Value,
            IsSeed = true, StagedIdentity = identity,
        };
        seed.MarkSynced(1);
        var publishedId = 7777;

        int? mapReturn = null;
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>())
            .Returns(_ => mapReturn);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>())
            .Returns(new PublishIntent
            {
                Identity = identity, Title = "T", TypeName = "Task",
                RecordedAt = DateTimeOffset.UtcNow, PublishedId = publishedId,
                CompletedAt = DateTimeOffset.UtcNow,
            });
        _stagedRegistry.FindAliasAsync(identity, Arg.Any<CancellationToken>()).Returns(alias);
        _workItems.GetByIdAsync(alias.Value, Arg.Any<CancellationToken>()).Returns(seed);
        _publishBehaviour = seedId =>
        {
            // Emulates the orchestrator's step-7 idempotent branch: an intent already
            // records the wire outcome, no CreateAsync is issued, and step 10a records
            // the map. Flip the map return so the follow-up readback sees the row.
            mapReturn = publishedId;
            return new SeedPublishResult
            {
                OldId = seedId, NewId = publishedId, Title = "T",
                Status = SeedPublishStatus.Created,
                LinkWarnings = Array.Empty<string>(),
            };
        };
        var remote = new WorkItem { Id = publishedId, Title = "T" };
        remote.MarkSynced(2);
        _ado.FetchWithLinksAsync(publishedId, Arg.Any<CancellationToken>())
            .Returns((remote, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        _publishInvocations.ShouldBe(new[] { alias.Value });
        // No wire-level create was issued through this test's ADO surface — the recovery
        // path never bypasses the orchestrator into a fresh CreateAsync.
        await _ado.DidNotReceive().CreateAsync(
            Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
        outcome.Ok.ShouldBeTrue();
        outcome.Deterministic.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadbackPublishSeed_IntentOnly_RecoveryFailsWithoutMapRow_IsIndeterminate()
    {
        // Recovery ran but the orchestrator returned success without recording a map row
        // (rollback inside step 10 with no #270 fix, or a stub-shaped result). We keep
        // Indeterminate rather than Verified — the local commit is the proof this readback
        // needs before it can claim the outcome.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000206"));
        var alias = MakeAlias(-8);
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns((int?)null);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>())
            .Returns(new PublishIntent
            {
                Identity = identity, Title = "T", TypeName = "Task",
                RecordedAt = DateTimeOffset.UtcNow, PublishedId = 4242,
                CompletedAt = DateTimeOffset.UtcNow,
            });
        _stagedRegistry.FindAliasAsync(identity, Arg.Any<CancellationToken>()).Returns(alias);
        _workItems.GetByIdAsync(alias.Value, Arg.Any<CancellationToken>())
            .Returns(new WorkItem
            {
                Id = alias.Value, Title = "T", Type = WorkItemType.Parse("Task").Value,
                IsSeed = true, StagedIdentity = identity,
            });
        _publishBehaviour = seedId => new SeedPublishResult
        {
            OldId = seedId, NewId = 4242, Title = "T",
            Status = SeedPublishStatus.Created,
            LinkWarnings = Array.Empty<string>(),
        };

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Deterministic.ShouldBeFalse();
        outcome.Error!.ShouldContain("id map");
    }

    [Fact]
    public async Task ReadbackPublishSeed_NoIntentAndNoMap_IsIndeterminate()
    {
        // Neither ledger records an outcome and the apply carried no MappedPublish id.
        // Nothing local proves the wire was touched; the readback cannot claim Verified
        // and cannot deterministically fail either.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000205"));
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns((int?)null);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>()).Returns((PublishIntent?)null);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Deterministic.ShouldBeFalse();
        outcome.Error!.ShouldContain("evidence");
        _publishInvocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadbackPublishSeed_MapPresentRemoteMissing_IsIndeterminate()
    {
        // The map recorded a new id but ADO says 404 — this cannot be a determinate
        // failure (the map remains a valid local commit) but Verified is unreachable
        // until the remote catches up.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000204"));
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns(4242);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>()).Returns((PublishIntent?)null);
        _ado.FetchWithLinksAsync(4242, Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new AdoNotFoundException(4242));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Deterministic.ShouldBeFalse();
    }

    // ── seed publish readback: graph verification ──────────────────────────

    [Fact]
    public async Task ReadbackPublishSeed_MissingRemoteNonHierarchyLink_IsIndeterminate()
    {
        // The item exists on ADO but a promoted non-hierarchy relation the local seed
        // still names is absent from its remote edges. Marking Verified on the mere
        // existence of the id would silently ratify a broken graph — Indeterminate makes
        // the reconcile pass name the missing edge.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000601"));
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        var newId = 4242;
        var targetId = 5555;
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns(newId);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>()).Returns((PublishIntent?)null);
        var remote = new WorkItem { Id = newId, Title = "T" };
        remote.MarkSynced(3);
        _ado.FetchWithLinksAsync(newId, Arg.Any<CancellationToken>())
            .Returns((remote, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));
        _seedLinks.GetLinksForItemAsync(newId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedLink>)new[]
            {
                new SeedLink(newId, targetId, SeedLinkTypes.Successor, DateTimeOffset.UtcNow),
            });

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Deterministic.ShouldBeFalse();
        outcome.Error!.ShouldContain(SeedLinkTypes.Successor);
        outcome.Error!.ShouldContain("missing");
    }

    [Fact]
    public async Task ReadbackPublishSeed_MissingRemoteParent_IsIndeterminate()
    {
        // parent-child is set at CREATE time (Hierarchy-Reverse), not by the promoter.
        // Verification reads the remote item's ParentId — a divergence there is the same
        // broken-graph classification as a missing non-hierarchy edge.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000603"));
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        var newId = 4242;
        var parentId = 3333;
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns(newId);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>()).Returns((PublishIntent?)null);
        // Remote has no ParentId.
        var remote = new WorkItem { Id = newId, Title = "T" };
        remote.MarkSynced(3);
        _ado.FetchWithLinksAsync(newId, Arg.Any<CancellationToken>())
            .Returns((remote, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));
        _seedLinks.GetLinksForItemAsync(newId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedLink>)new[]
            {
                new SeedLink(newId, parentId, SeedLinkTypes.ParentChild, DateTimeOffset.UtcNow),
            });

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Deterministic.ShouldBeFalse();
        outcome.Error!.ShouldContain("parent");
    }

    [Fact]
    public async Task ReadbackPublishSeed_CompleteGraphAcrossParentAndNonHierarchyRelations_Verifies()
    {
        // Happy path: fetched item reflects every intended promoted edge — parent via
        // ParentId, non-hierarchy via WorkItemLink surfaced in the friendly short-name
        // form. The readback verifies once ALL are covered.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000602"));
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        var newId = 4242;
        var parentId = 3333;
        var successorId = 5555;
        var relatedId = 6666;
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns(newId);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>()).Returns((PublishIntent?)null);
        var remote = new WorkItem { Id = newId, Title = "T", ParentId = parentId };
        remote.MarkSynced(3);
        _ado.FetchWithLinksAsync(newId, Arg.Any<CancellationToken>())
            .Returns((remote, (IReadOnlyList<WorkItemLink>)new[]
            {
                new WorkItemLink(newId, successorId, "Successor"),
                new WorkItemLink(newId, relatedId, "System.LinkTypes.Related"), // raw form
            }));
        _seedLinks.GetLinksForItemAsync(newId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedLink>)new[]
            {
                new SeedLink(newId, parentId, SeedLinkTypes.ParentChild, DateTimeOffset.UtcNow),
                new SeedLink(newId, successorId, SeedLinkTypes.Successor, DateTimeOffset.UtcNow),
                new SeedLink(newId, relatedId, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
            });

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.Deterministic.ShouldBeTrue();
    }

    // ── seed publish link warnings classification ──────────────────────────

    [Fact]
    public void ClassifySeedPublishSuccess_CacheOnlyWarning_StaysApplied()
    {
        // A "relationship cache refresh failed" note is cosmetic — the remote work item
        // and its edges already reflect the intent; only the local cache mirror needs a
        // follow-up sync. The publish is Applied and the readback promotes to Verified.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000301"));
        var result = new SeedPublishResult
        {
            OldId = -1, NewId = 4242, Title = "T",
            Status = SeedPublishStatus.Created,
            LinkWarnings = new[]
            {
                "Work item #4242 was published, but relationship cache refresh failed: db locked",
            },
        };

        var classified = PlanOperationExecutor.ClassifySeedPublishSuccess(result, identity);

        classified.Outcome.ShouldBe(PlanExecutionOutcome.Applied);
    }

    [Fact]
    public void ClassifySeedPublishSuccess_RemoteLinkFailure_IsIndeterminate()
    {
        // A "Failed to create ADO link ..." warning is a remote link-promotion failure:
        // the item exists but a promised edge is missing. Applied → Verified would
        // silently ratify a broken graph; we surface Indeterminate so the reconcile
        // pass names the missing edge before Verified is possible.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000302"));
        var result = new SeedPublishResult
        {
            OldId = -1, NewId = 4242, Title = "T",
            Status = SeedPublishStatus.Created,
            LinkWarnings = new[]
            {
                "Failed to create ADO link (Successor) between 4242 and 5555: server 500.",
            },
        };

        var classified = PlanOperationExecutor.ClassifySeedPublishSuccess(result, identity);

        classified.Outcome.ShouldBe(PlanExecutionOutcome.Indeterminate);
        classified.Error!.ShouldContain("link");
    }

    [Fact]
    public void ClassifySeedPublishSuccess_UnknownLinkType_IsIndeterminate()
    {
        // An unmapped seed link type is a link-promotion failure too: the local seed
        // named an edge the promoter cannot land on ADO. Same classification.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000303"));
        var result = new SeedPublishResult
        {
            OldId = -1, NewId = 4242, Title = "T",
            Status = SeedPublishStatus.Created,
            LinkWarnings = new[]
            {
                "Unknown link type 'MysteryEdge' between 4242 and 5555; skipped.",
            },
        };

        var classified = PlanOperationExecutor.ClassifySeedPublishSuccess(result, identity);

        classified.Outcome.ShouldBe(PlanExecutionOutcome.Indeterminate);
    }

    [Fact]
    public void ClassifySeedPublishSuccess_MixedWarnings_TakesFirstRemoteAsIndeterminate()
    {
        // Even one non-cache warning downgrades the whole result — the reviewer flagged
        // "never ignore link-promotion failure" as an invariant.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000304"));
        var result = new SeedPublishResult
        {
            OldId = -1, NewId = 4242, Title = "T",
            Status = SeedPublishStatus.Created,
            LinkWarnings = new[]
            {
                "Work item #4242 was published, but relationship cache refresh failed: harmless.",
                "Failed to create ADO link (Related) between 4242 and 5555: 502 Bad Gateway.",
            },
        };

        var classified = PlanOperationExecutor.ClassifySeedPublishSuccess(result, identity);

        classified.Outcome.ShouldBe(PlanExecutionOutcome.Indeterminate);
    }

    [Fact]
    public void ClassifySeedPublishSuccess_NoWarnings_IsApplied()
    {
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000305"));
        var result = new SeedPublishResult
        {
            OldId = -1, NewId = 4242, Title = "T",
            Status = SeedPublishStatus.Created,
            LinkWarnings = Array.Empty<string>(),
        };

        var classified = PlanOperationExecutor.ClassifySeedPublishSuccess(result, identity);

        classified.Outcome.ShouldBe(PlanExecutionOutcome.Applied);
        classified.ResultJson!.ShouldContain("4242");
    }

    // ── canonical readback ResultJson for recovered-Verified rows ─────────
    //
    // Every readback that proves a recovered operation Verified MUST carry the
    // canonical ResultJson the lifecycle threads into the atomic Applying→Applied
    // record. A recovered Verified row with a NULL result_json would silently break
    // CLI/MCP status which reads the raw column.

    [Fact]
    public async Task ReadbackBatch_Verified_CarriesCurrentServerRevision()
    {
        var op = new BatchOperation
        {
            Id = "b", WorkItemId = 42, ExpectedRevision = 3,
            Fields = new Dictionary<string, string?> { ["System.State"] = "Active" },
        };
        var wi = new WorkItem { Id = 42, Title = "T" };
        wi.MarkSynced(7);
        wi.ChangeState("Active");
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.ResultJson.ShouldBe("{\"revision\":7}");
    }

    [Fact]
    public async Task ReadbackAddLink_Parent_Verified_CarriesCurrentServerRevision()
    {
        var op = new AddLinkOperation
        {
            Id = "L", WorkItemId = 1, OtherId = 5, ExpectedRevision = 2, Relation = "parent",
        };
        var wi = new WorkItem { Id = 1, Title = "T", ParentId = 5 };
        wi.MarkSynced(11);
        _ado.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.ResultJson.ShouldBe("{\"revision\":11}");
    }

    [Fact]
    public async Task ReadbackAddLink_NonParent_Verified_CarriesCurrentServerRevision()
    {
        var op = new AddLinkOperation
        {
            Id = "L", WorkItemId = 1, OtherId = 9, ExpectedRevision = 2, Relation = "successor",
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(13);
        _ado.FetchWithLinksAsync(1, Arg.Any<CancellationToken>()).Returns(
            (wi, (IReadOnlyList<WorkItemLink>)new[] { new WorkItemLink(1, 9, "Successor") }));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.ResultJson.ShouldBe("{\"revision\":13}");
    }

    [Fact]
    public async Task ReadbackRemoveLink_Verified_CarriesCurrentServerRevision()
    {
        var op = new RemoveLinkOperation
        {
            Id = "L", WorkItemId = 1, OtherId = 9, ExpectedRevision = 2, Relation = "successor",
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(17);
        _ado.FetchWithLinksAsync(1, Arg.Any<CancellationToken>()).Returns(
            (wi, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.ResultJson.ShouldBe("{\"revision\":17}");
    }

    [Fact]
    public async Task ReadbackDelete_NotFound_Verified_CarriesDeletedMarker()
    {
        var op = new DeleteOperation { Id = "D", WorkItemId = 5, ExpectedRevision = 6 };
        _ado.FetchAsync(5, Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new AdoNotFoundException(5));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.ResultJson.ShouldBe("{\"deleted\":true}");
    }

    [Fact]
    public async Task ReadbackPublishSeed_MappedVerifies_CarriesIdentityAndPublishedId()
    {
        // The mapped-seed crash: executor already MappedPublish'd this row, the process
        // crashed mid-Applying, and recovery finds the map row still there. The readback
        // is what will settle the recovered Verified row's result — it MUST carry the
        // canonical {"identity":<planned>,"publishedId":<map>} shape.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-0000000009a1"));
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns(4242);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>()).Returns((PublishIntent?)null);
        var remote = new WorkItem { Id = 4242, Title = "T" };
        remote.MarkSynced(1);
        _ado.FetchWithLinksAsync(4242, Arg.Any<CancellationToken>())
            .Returns((remote, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.ResultJson.ShouldNotBeNull();
        outcome.ResultJson.ShouldBe($"{{\"identity\":\"{identity}\",\"publishedId\":4242}}");
    }

    // ── AB#754: server-owned normalized fields verify with warning detail ──
    //
    // Every test below drives the PUBLIC readback outcome, never the private comparator,
    // per the spec's testing decisions. The invariant under test is a pair: a proven
    // mutation whose ONLY difference is a field ADO's own revision machinery owns must
    // be Ok (Verified) AND must carry warning detail — and every other shape must not.

    [Fact]
    public async Task ReadbackBatch_GeneratedClosedDate_VerifiesWithWarning()
    {
        // The evidence case from spec #753: a terminal close lands, and ADO stamps its own
        // ClosedDate from the server clock instead of the authored timestamp. The intended
        // mutation (State=Done, TerminalOutcome=completed) is proven on the refreshed read,
        // so this must be Verified-with-warning rather than a false Indeterminate.
        StubFieldDefinition("Microsoft.VSTS.Common.ClosedDate", "dateTime");
        var op = new BatchOperation
        {
            Id = "close",
            WorkItemId = 7,
            ExpectedRevision = 4,
            Fields = new Dictionary<string, string?>
            {
                ["System.State"] = "Done",
                ["Custom.TerminalOutcome"] = "completed",
                ["Microsoft.VSTS.Common.ClosedDate"] = "2026-08-25T00:00:00Z",
            },
        };
        var wi = new WorkItem { Id = 7, Title = "T" };
        wi.MarkSynced(5);
        wi.ChangeState("Done");
        wi.UpdateField("Custom.TerminalOutcome", "completed");
        wi.UpdateField("Microsoft.VSTS.Common.ClosedDate", "2026-08-25T22:45:08.85Z");
        _ado.FetchAsync(7, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.Error.ShouldBeNull();
        outcome.ResultJson.ShouldBe("{\"revision\":5}");
        outcome.Warning.ShouldNotBeNull();
        outcome.Warning.ShouldContain("Microsoft.VSTS.Common.ClosedDate");
    }

    [Fact]
    public async Task ReadbackBatch_UserAuthoredScalarMismatch_RemainsNonVerifiedWithoutWarning()
    {
        // The strictness half. A user-authored scalar that did not land is a genuine
        // contradiction and must never be downgraded, even though the same batch also
        // carries a server-generated field that DID normalize.
        StubFieldDefinition("Microsoft.VSTS.Common.ClosedDate", "dateTime");
        StubFieldDefinition("Custom.TerminalOutcome", "string");
        var op = new BatchOperation
        {
            Id = "close",
            WorkItemId = 7,
            ExpectedRevision = 4,
            Fields = new Dictionary<string, string?>
            {
                ["Custom.TerminalOutcome"] = "completed",
                ["Microsoft.VSTS.Common.ClosedDate"] = "2026-08-25T00:00:00Z",
            },
        };
        var wi = new WorkItem { Id = 7, Title = "T" };
        wi.MarkSynced(5);
        wi.UpdateField("Custom.TerminalOutcome", "abandoned");
        wi.UpdateField("Microsoft.VSTS.Common.ClosedDate", "2026-08-25T22:45:08.85Z");
        _ado.FetchAsync(7, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Warning.ShouldBeNull();
        outcome.Error.ShouldNotBeNull();
        outcome.Error.ShouldContain("Custom.TerminalOutcome");
    }

    [Fact]
    public async Task ReadbackBatch_RequestedStateDidNotLand_IsNeverWarningVerified()
    {
        // Terminal-outcome coupling stays strict: a generated stamp only ever rides ALONG
        // WITH a proven lifecycle transition. If State did not land, the normalization is
        // not evidence of anything and the row must stay non-Verified.
        StubFieldDefinition("Microsoft.VSTS.Common.ClosedDate", "dateTime");
        var op = new BatchOperation
        {
            Id = "close",
            WorkItemId = 7,
            ExpectedRevision = 4,
            Fields = new Dictionary<string, string?>
            {
                ["System.State"] = "Done",
                ["Microsoft.VSTS.Common.ClosedDate"] = "2026-08-25T00:00:00Z",
            },
        };
        var wi = new WorkItem { Id = 7, Title = "T" };
        wi.MarkSynced(5);
        wi.ChangeState("Doing"); // the requested transition did NOT land
        wi.UpdateField("Microsoft.VSTS.Common.ClosedDate", "2026-08-25T22:45:08.85Z");
        _ado.FetchAsync(7, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Warning.ShouldBeNull();
    }

    [Fact]
    public async Task ReadbackBatch_StaleRevision_RemainsIndeterminateNotWarningVerified()
    {
        // An unproven readback never reaches the policy at all. The revision guard fires
        // first, so even an all-server-generated batch stays retryable.
        StubFieldDefinition("Microsoft.VSTS.Common.ClosedDate", "dateTime");
        var op = new BatchOperation
        {
            Id = "close",
            WorkItemId = 7,
            ExpectedRevision = 5,
            Fields = new Dictionary<string, string?>
            {
                ["Microsoft.VSTS.Common.ClosedDate"] = "2026-08-25T00:00:00Z",
            },
        };
        var wi = new WorkItem { Id = 7, Title = "T" };
        wi.MarkSynced(5); // did NOT advance past ExpectedRevision
        _ado.FetchAsync(7, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Deterministic.ShouldBeFalse();
        outcome.Warning.ShouldBeNull();
        outcome.Error.ShouldNotBeNull();
        outcome.Error.ShouldContain("revision");
    }

    [Fact]
    public async Task ReadbackBatch_UnavailableReadback_RemainsIndeterminateNotWarningVerified()
    {
        // A readback that could not be performed is the "unknown outcome" the spec keeps
        // fail-closed. It must not be warning-verified on the strength of the field set.
        var op = new BatchOperation
        {
            Id = "close",
            WorkItemId = 7,
            ExpectedRevision = 4,
            Fields = new Dictionary<string, string?>
            {
                ["Microsoft.VSTS.Common.ClosedDate"] = "2026-08-25T00:00:00Z",
            },
        };
        _ado.FetchAsync(7, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ADO unreachable"));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Deterministic.ShouldBeFalse();
        outcome.Warning.ShouldBeNull();
    }

    [Fact]
    public async Task ReadbackBatch_UndeclaredServerGeneratedField_IsNotWarningVerified()
    {
        // Field-aware evidence, not a name-only ignore list: a field this process does not
        // even declare cannot be warning-verified, so the store is genuinely consulted.
        _fieldDefinitions
            .GetByReferenceNameAsync("Microsoft.VSTS.Common.ClosedDate", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FieldDefinition?>(null));
        var op = new BatchOperation
        {
            Id = "close",
            WorkItemId = 7,
            ExpectedRevision = 4,
            Fields = new Dictionary<string, string?>
            {
                ["Microsoft.VSTS.Common.ClosedDate"] = "2026-08-25T00:00:00Z",
            },
        };
        var wi = new WorkItem { Id = 7, Title = "T" };
        wi.MarkSynced(5);
        wi.UpdateField("Microsoft.VSTS.Common.ClosedDate", "2026-08-25T22:45:08.85Z");
        _ado.FetchAsync(7, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Warning.ShouldBeNull();
    }

    [Fact]
    public async Task ReadbackBatch_NoNormalization_VerifiesWithNoWarning()
    {
        // Guards against an always-warning implementation: the clean path must stay clean,
        // or "carries a warning" would be worthless as a signal.
        var op = new BatchOperation
        {
            Id = "clean",
            WorkItemId = 7,
            ExpectedRevision = 4,
            Fields = new Dictionary<string, string?> { ["System.State"] = "Done" },
        };
        var wi = new WorkItem { Id = 7, Title = "T" };
        wi.MarkSynced(5);
        wi.ChangeState("Done");
        _ado.FetchAsync(7, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.Warning.ShouldBeNull();
    }

    [Fact]
    public async Task ReadbackBatch_HtmlAndScalarTogether_VerifiesWithBothWarningKinds()
    {
        // AB#755's integration point with AB#754: one batch carrying BOTH a canonicalized
        // HTML field and a server-generated stamp must verify once, with a single warning
        // naming both — not two mechanisms racing, and not one kind silently dropped.
        StubFieldDefinition("System.Description", "html");
        StubFieldDefinition("Microsoft.VSTS.Common.ClosedDate", "dateTime");
        var op = new BatchOperation
        {
            Id = "mixed",
            WorkItemId = 9,
            ExpectedRevision = 2,
            Fields = new Dictionary<string, string?>
            {
                ["System.State"] = "Done",
                ["System.Description"] = "<p class=\"x\">Body &amp; tail</p>",
                ["Microsoft.VSTS.Common.ClosedDate"] = "2026-08-25T00:00:00Z",
            },
        };
        var wi = new WorkItem { Id = 9, Title = "T" };
        wi.MarkSynced(3);
        wi.ChangeState("Done");
        wi.UpdateField("System.Description", "<P class='x'>Body &#38; tail</P>");
        wi.UpdateField("Microsoft.VSTS.Common.ClosedDate", "2026-08-25T22:45:08.85Z");
        _ado.FetchAsync(9, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.Warning.ShouldNotBeNull();
        outcome.Warning.ShouldContain("System.Description");
        outcome.Warning.ShouldContain("Microsoft.VSTS.Common.ClosedDate");
    }

    [Fact]
    public async Task ReadbackBatch_HtmlNormalizedButScalarMismatched_RemainsNonVerified()
    {
        // Strictness survives the HTML extension: an equivalent description must NOT drag a
        // genuinely contradicted scalar across the line with it.
        StubFieldDefinition("System.Description", "html");
        StubFieldDefinition("Custom.TerminalOutcome", "string");
        var op = new BatchOperation
        {
            Id = "mixed",
            WorkItemId = 9,
            ExpectedRevision = 2,
            Fields = new Dictionary<string, string?>
            {
                ["System.Description"] = "<p class=\"x\">Body</p>",
                ["Custom.TerminalOutcome"] = "completed",
            },
        };
        var wi = new WorkItem { Id = 9, Title = "T" };
        wi.MarkSynced(3);
        wi.UpdateField("System.Description", "<P class='x'>Body</P>");
        wi.UpdateField("Custom.TerminalOutcome", "abandoned");
        _ado.FetchAsync(9, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Warning.ShouldBeNull();
        outcome.Error.ShouldNotBeNull();
        outcome.Error.ShouldContain("Custom.TerminalOutcome");
    }

    [Fact]
    public async Task ReadbackBatch_HtmlNormalizedButStaleReadback_RemainsIndeterminate()
    {
        // The unavailable/unproven guard sits ABOVE the comparator for HTML exactly as it
        // does for server-generated stamps: no revision advance, no warning-verify.
        StubFieldDefinition("System.Description", "html");
        var op = new BatchOperation
        {
            Id = "html",
            WorkItemId = 9,
            ExpectedRevision = 3,
            Fields = new Dictionary<string, string?>
            {
                ["System.Description"] = "<p class=\"x\">Body</p>",
            },
        };
        var wi = new WorkItem { Id = 9, Title = "T" };
        wi.MarkSynced(3); // did NOT advance
        wi.UpdateField("System.Description", "<P class='x'>Body</P>");
        _ado.FetchAsync(9, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Deterministic.ShouldBeFalse();
        outcome.Warning.ShouldBeNull();
    }

    [Fact]
    public async Task ReadbackBatch_HtmlAttributeValueChanged_RemainsNonVerified()
    {
        // "Structurally equivalent" must not mean "same tags": a changed ATTRIBUTE VALUE is
        // a content change and stays a contradiction. Guards against a comparer that only
        // compares element names.
        StubFieldDefinition("System.Description", "html");
        var op = new BatchOperation
        {
            Id = "html",
            WorkItemId = 9,
            ExpectedRevision = 2,
            Fields = new Dictionary<string, string?>
            {
                ["System.Description"] = "<a href=\"https://example.test/a\">Link</a>",
            },
        };
        var wi = new WorkItem { Id = 9, Title = "T" };
        wi.MarkSynced(3);
        wi.UpdateField("System.Description", "<a href=\"https://example.test/b\">Link</a>");
        _ado.FetchAsync(9, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Warning.ShouldBeNull();
    }

    [Fact]
    public async Task ReadbackBatch_ClearOfServerGeneratedFieldThatDidNotTake_IsNotWarningVerified()
    {
        // A requested CLEAR is an intent that must be PROVEN, not excused. Even on a
        // server-generated field, a value still present means the mutation did not land —
        // reporting that as a normalized success would be the exact false green Spec #753
        // exists to abolish.
        StubFieldDefinition("Microsoft.VSTS.Common.ClosedDate", "dateTime");
        var op = new BatchOperation
        {
            Id = "clear",
            WorkItemId = 7,
            ExpectedRevision = 4,
            Fields = new Dictionary<string, string?>
            {
                ["Microsoft.VSTS.Common.ClosedDate"] = null,
            },
        };
        var wi = new WorkItem { Id = 7, Title = "T" };
        wi.MarkSynced(5);
        wi.UpdateField("Microsoft.VSTS.Common.ClosedDate", "2026-08-25T22:45:08.85Z");
        _ado.FetchAsync(7, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Warning.ShouldBeNull();
        outcome.Error.ShouldNotBeNull();
        outcome.Error.ShouldContain("cleared");
    }

    // ── AB#754: the terminal contract is protected STATICALLY ──────────────────

    [Fact]
    public void TerminalContractFieldsAreNeverServerGenerated()
    {
        // Spec #753 user story 7: "System.State=Done and Custom.TerminalOutcome=completed
        // remain an atomic, strict terminal contract."
        //
        // That contract holds because neither field is server-generated, so a batch whose
        // transition did not land fails strict comparison before normalization is ever
        // considered. This test is the guard on that reasoning: adding a lifecycle field to
        // the generated set would silently let a close be warning-verified without the
        // transition having landed, and it must break here instead.
        foreach (var field in ServerGeneratedFieldPolicy.TerminalContractFields)
        {
            ServerGeneratedFieldPolicy.IsServerGenerated(field).ShouldBeFalse(
                $"{field} is part of the strict terminal contract and must never be " +
                "excusable as server-generated normalization.");
        }
    }

    [Fact]
    public void OnlyExplainedDifferencesRemain_RejectsANormalizationTheBatchNeverRequested()
    {
        // Defence in depth against a future caller recording a difference on a field the
        // plan did not ask for — that is not this batch's business and must never justify
        // verifying it.
        var batch = new BatchOperation
        {
            Id = "b",
            WorkItemId = 1,
            ExpectedRevision = 1,
            Fields = new Dictionary<string, string?> { ["System.State"] = "Done" },
        };
        var stray = new[]
        {
            new PlanReadbackNormalization(
                "Microsoft.VSTS.Common.ClosedDate", "x", "y", NormalizationKind.ServerGenerated),
        };

        ServerGeneratedFieldPolicy.OnlyExplainedDifferencesRemain(batch, stray).ShouldBeFalse();
    }

    [Fact]
    public void OnlyExplainedDifferencesRemain_RejectsAServerGeneratedClaimOutsideTheJustifiedSet()
    {
        // A ServerGenerated classification must still satisfy the justified set; the kind
        // label alone is not evidence.
        var batch = new BatchOperation
        {
            Id = "b",
            WorkItemId = 1,
            ExpectedRevision = 1,
            Fields = new Dictionary<string, string?> { ["Custom.TerminalOutcome"] = "completed" },
        };
        var mislabelled = new[]
        {
            new PlanReadbackNormalization(
                "Custom.TerminalOutcome", "completed", "abandoned", NormalizationKind.ServerGenerated),
        };

        ServerGeneratedFieldPolicy.OnlyExplainedDifferencesRemain(batch, mislabelled).ShouldBeFalse();
    }

    private static StagedAlias MakeAlias(int negative)
    {
        StagedAlias.TryFrom(negative, out var alias).ShouldBeTrue();
        return alias;
    }
}
