using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Core;
using Twig.Providers.GitHub;
using Twig.Store.Sqlite;
using Xunit;
using Provider = Twig.Providers.GitHub.GitHubProvider;

namespace Twig.Providers.GitHub.Tests;

public sealed class GitHubProviderTests
{
    [Theory]
    [InlineData(GitHubCredentialSource.Gh)]
    [InlineData(GitHubCredentialSource.GitHubApp)]
    public async Task DiscoveryReadsAndObservedWritesUseActualAdapterAndDurableStore(GitHubCredentialSource source)
    {
        using var f = await Fixture.Create();
        f.Access.Source = source;
        var schema = await f.Schema();
        schema.Gaps.ShouldBeEmpty();
        schema.Types.Select(t => t.Reference).ShouldContain("T1");
        schema.Types.Single(t => t.Reference == "T1").States.Select(s => s.Name).ShouldBe(["CLOSED", "OPEN"]);
        schema.Types.Single(t => t.Reference == "T1").Rules.ShouldContain(r => r.Disabled
            && r.Values.Any(v => v.DataType == "github.native-rule-discovery" && v.Text == Provider.RuleDiscovery));
        f.Provider.Capabilities.ExactRevisions.ShouldBeFalse();
        f.Provider.Capabilities.AtomicFieldBatches.ShouldBeFalse();
        f.Provider.Capabilities.NativeRelations.ShouldContain(Provider.Hierarchy);
        f.Provider.Capabilities.NativeRelations.ShouldContain(Provider.Dependency);
        f.Provider.Capabilities.LocalRelations.ShouldBe([Provider.Related], ignoreOrder: true);
        var item = await f.Item();
        item.Revision!.Token.ShouldStartWith("github-observation-v1:");
        item.Fields[Provider.Body].ShouldBe(FieldValue.RichText("", FieldKind.Markdown));
        item.Fields[Provider.Milestone].ShouldBe(FieldValue.Null);
        item.Fields[Provider.Labels].ShouldBe(FieldValue.Strings([]));
        item.Fields.ContainsKey("not.observed").ShouldBeFalse();
        var proposal = await f.Edit();
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        ack.Status.ShouldBe(OpStatus.Applied);
        ack.Revision.ShouldBeNull();
        f.Access.SawDurableAttempt.ShouldBeTrue();
        f.Http.Writes.ShouldBe(1);
        f.Access.Observed.ShouldBe(1);
        (await f.Stored()).Journal.Single().Status.ShouldBe(OpStatus.Applied);
        var result = (await f.Provider.VerifyAsync(proposal, ack, default)).Value;
        result.Status.ShouldBe(OpStatus.Verified);
        result.Revision!.Token.ShouldStartWith("github-observation-v1:");
        (await f.Stored()).Journal.Single().Receipts.Length.ShouldBe(2);
        (await f.Stored()).Items.Single(i => i.Key == item.Key).Fields[Provider.Title].Text.ShouldBe("changed");
        f.Http.LastPayload.ShouldNotBeNull().ShouldContain("clientMutationId");
        f.Http.LastPayload.ShouldNotBeNull().ShouldNotContain("revision");
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("repository")]
    [InlineData("actor")]
    [InlineData("context")]
    [InlineData("credential")]
    [InlineData("credential-switch")]
    [InlineData("lease")]
    public async Task ImmutableTargetActorAndCredentialChangesNeverSwitchAccounts(string change)
    {
        using var f = await Fixture.Create();
        var proposal = await f.Edit();
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        switch (change)
        {
            case "owner": f.Http.Owner = "other-owner"; break;
            case "repository": f.Http.RepositoryId = "other-repo"; break;
            case "actor": f.Access.Actor = f.Http.Actor = "other-actor"; break;
            case "context": f.Access.Context = "new-selection"; break;
            case "credential": f.Access.Source = (GitHubCredentialSource)99; break;
            case "credential-switch": f.Access.Source = GitHubCredentialSource.GitHubApp; break;
            case "lease": f.Access.InvalidLease = true; break;
        }
        var result = await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBeOneOf(ErrorCode.Conflict, ErrorCode.SchemaChanged, ErrorCode.Unavailable);
        f.Http.Writes.ShouldBe(0);
        (await f.Stored()).Journal.Single().Status.ShouldBe(OpStatus.Planned);
    }

    [Theory]
    [InlineData("rules-unsupported")]
    [InlineData("rules-stale")]
    [InlineData("rules-incomplete")]
    [InlineData("rules-scope")]
    [InlineData("unknown-rule")]
    [InlineData("unknown-state")]
    [InlineData("types-page")]
    [InlineData("metadata-denied")]
    [InlineData("field-signature")]
    [InlineData("partial-relations")]
    [InlineData("type-disabled")]
    public async Task SchemaDenialsAndUnsupportedRulesCannotBecomeEmptyPermission(string defect)
    {
        using var f = await Fixture.Create();
        var item = await f.Item();
        f.Access.RuleDefect = defect;
        f.Http.Defect = defect;
        var discovery = await f.Provider.DiscoverSchemaAsync(f.Connection, default);
        if (discovery.IsSuccess)
        {
            var proposal = await f.Proposal(new("edit", OpKind.Batch, item.Key, item.Revision,
                Fields((Provider.Title, FieldValue.String("changed"))), concurrency: ConcurrencyMode.ObservedNonAtomic),
                discovery.Value);
            (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeFalse();
        }
        else discovery.Error.Code.ShouldBeOneOf(ErrorCode.Unsupported, ErrorCode.Unavailable);
        f.Http.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task ExactRevisionAndSameOperationCloseGatesAreRefused()
    {
        using var f = await Fixture.Create();
        var item = await f.Item();
        var exact = await f.Proposal(new("exact", OpKind.Batch, item.Key, item.Revision,
            Fields((Provider.Title, FieldValue.String("changed")))));
        (await f.Provider.ImportAsync(exact, exact.Digest)).Error.Code.ShouldBe(ErrorCode.Unsupported);
        f.Access.CloseGate = true;
        var close = await f.Proposal(new("close", OpKind.Batch, item.Key, item.Revision,
            Fields((Provider.StateField, FieldValue.String("CLOSED"))), concurrency: ConcurrencyMode.ObservedNonAtomic));
        var denied = await f.Provider.ImportAsync(close, close.Digest);
        denied.IsSuccess.ShouldBeFalse();
        denied.Error.Evidence.ShouldContain(e => e.Contains("same batch", StringComparison.Ordinal));
        var coupled = await f.Proposal(new("close-with-gate", OpKind.Batch, item.Key, item.Revision,
            Fields((Provider.StateField, FieldValue.String("CLOSED")), (Provider.Title, FieldValue.String("closure evidence"))),
            concurrency: ConcurrencyMode.ObservedNonAtomic));
        (await f.Provider.ImportAsync(coupled, coupled.Digest)).Error.Code.ShouldBe(ErrorCode.Unsupported);
        f.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData(false, OpStatus.Verified)]
    [InlineData(true, OpStatus.Partial)]
    public async Task IndependentObservedFieldsUseOneDurableEffectWithoutClaimingAtomicity(bool interleaved, OpStatus expected)
    {
        using var f = await Fixture.Create();
        var item = await f.Item();
        f.Provider.Capabilities.AtomicFieldBatches.ShouldBeFalse();
        item.Fields[Provider.Title].ShouldBe(FieldValue.String("original"));
        item.Fields[Provider.Body].ShouldBe(FieldValue.RichText("", FieldKind.Markdown));
        var proposal = await f.Proposal(new("independent-fields", OpKind.Batch, item.Key, item.Revision,
            Fields((Provider.Title, FieldValue.String("changed")),
                (Provider.Body, FieldValue.RichText("**reviewed body**", FieldKind.Markdown))),
            concurrency: ConcurrencyMode.ObservedNonAtomic));
        await f.Import(proposal);
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        ack.Status.ShouldBe(OpStatus.Applied);
        f.Access.SawDurableAttempt.ShouldBeTrue();
        f.Http.Writes.ShouldBe(1);
        f.Http.LastPayload.ShouldNotBeNull().ShouldContain("**reviewed body**");
        ack.Evidence.ShouldContain(e => e.Contains("no remote CAS or atomic batch guarantee", StringComparison.Ordinal));
        if (interleaved) f.Http.Issues["I1"]["body"] = "another writer";
        (await f.Provider.VerifyAsync(proposal, ack, default)).Value.Status.ShouldBe(expected);
        var stored = await f.Stored();
        stored.Journal.Single().Receipts.Select(r => r.Status).ShouldBe([OpStatus.Applied, expected]);
        var replay = await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default);
        replay.IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(1);
    }

    [Theory]
    [InlineData(Provider.Title, "text")]
    [InlineData(Provider.Body, "markdown")]
    [InlineData(Provider.Labels, "labels")]
    [InlineData(Provider.Assignees, "assignees")]
    [InlineData(Provider.Milestone, "milestone")]
    [InlineData(Provider.StateField, "state")]
    public async Task SupportedOneRequestFieldPayloadsRoundTripWithTypedEvidence(string field, string kind)
    {
        using var f = await Fixture.Create();
        var value = kind switch
        {
            "text" => FieldValue.String("exact text"),
            "markdown" => FieldValue.RichText("**exact markdown**", FieldKind.Markdown),
            "labels" => FieldValue.Strings(["L1"]),
            "assignees" => FieldValue.Strings(["U1"]),
            "milestone" => FieldValue.Identity("M1"),
            _ => FieldValue.String("CLOSED"),
        };
        var item = await f.Item();
        var proposal = await f.Proposal(new("field", OpKind.Batch, item.Key, item.Revision,
            Fields((field, value)), concurrency: ConcurrencyMode.ObservedNonAtomic));
        await f.Import(proposal);
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        ack.Status.ShouldBe(OpStatus.Applied);
        (await f.Provider.VerifyAsync(proposal, ack, default)).Value.Status.ShouldBe(OpStatus.Verified);
        f.Http.Writes.ShouldBe(1);
    }

    [Theory]
    [InlineData("field-kind")]
    [InlineData("resource-owner")]
    [InlineData("unknown-input")]
    [InlineData("body-null")]
    [InlineData("read-only")]
    public async Task InvalidPayloadsAndForeignResourcesAreRefusedBeforeAttempt(string defect)
    {
        using var f = await Fixture.Create();
        var item = await f.Item();
        var field = defect == "read-only" ? "github.updatedAt" : defect == "body-null" ? Provider.Body : Provider.Milestone;
        var value = defect == "field-kind" ? FieldValue.String("M1") : defect == "body-null" ? FieldValue.Null
            : defect == "read-only" ? FieldValue.At(DateTimeOffset.UtcNow) : FieldValue.Identity("M1");
        f.Http.ForeignResource = defect == "resource-owner";
        f.Http.Defect = defect;
        var proposal = await f.Proposal(new("field", OpKind.Batch, item.Key, item.Revision, Fields((field, value)),
            concurrency: ConcurrencyMode.ObservedNonAtomic));
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(0);
        (await f.Stored()).Journal.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("issues-page")]
    [InlineData("labels-page")]
    [InlineData("relationships-page")]
    [InlineData("duplicate-candidates")]
    [InlineData("partial-graphql")]
    public async Task QueriesNeverClaimCompletenessOverTruncatedOrUnavailableCandidates(string defect)
    {
        using var f = await Fixture.Create();
        f.Http.Defect = defect;
        var result = await f.Provider.QueryAsync(f.Connection,
            new(QueryKind.Not, children: [new(QueryKind.Equal, Provider.Title, FieldValue.String("excluded"))]), null, default);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBeOneOf(ErrorCode.Unsupported, ErrorCode.Unavailable);
        (await f.Stored()).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryEvaluatesExactTypedAstAndPersistsCompleteNativeObservations()
    {
        using var f = await Fixture.Create();
        var query = new Query(QueryKind.And, children: [
            new(QueryKind.Equal, Provider.Body, FieldValue.RichText("", FieldKind.Markdown)),
            new(QueryKind.Not, children: [new(QueryKind.Equal, Provider.Title, FieldValue.String("excluded"))])]);
        var result = (await f.Provider.QueryAsync(f.Connection, query, null, default)).Value;
        result.Complete.ShouldBeTrue();
        result.Continuation.ShouldBeNull();
        result.Items.Length.ShouldBe(2);
        (await f.Stored()).Items.Length.ShouldBe(2);
        (await f.Provider.QueryAsync(f.Connection, new(QueryKind.Equal, "missing", FieldValue.Null), null, default))
            .Error.Code.ShouldBe(ErrorCode.Unsupported);
        (await f.Provider.QueryAsync(f.Connection, query, "unbound", default)).Error.Code.ShouldBe(ErrorCode.Unsupported);
    }

    [Theory]
    [InlineData(Provider.Hierarchy)]
    [InlineData(Provider.Dependency)]
    public async Task NativeRelationsUseRealSchemaAndTransportAndNeverFallBackOnDenial(string kind)
    {
        using var f = await Fixture.Create();
        var item = await f.Item();
        var proposal = await f.Proposal(new("link", OpKind.Relate, item.Key, item.Revision,
            other: f.Key("I2"), relation: kind, concurrency: ConcurrencyMode.ObservedNonAtomic));
        await f.Import(proposal);
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        ack.Status.ShouldBe(OpStatus.Applied);
        (await f.Provider.VerifyAsync(proposal, ack, default)).Value.Status.ShouldBe(OpStatus.Verified);
        var stored = await f.Stored();
        stored.Relationships.Single().Origin.ShouldBe(RelationshipOrigin.Provider);
        var refreshed = await f.Item();
        var remove = await f.Proposal(new("unlink", OpKind.Unrelate, refreshed.Key, refreshed.Revision,
            other: f.Key("I2"), relation: kind, concurrency: ConcurrencyMode.ObservedNonAtomic));
        await f.Import(remove);
        var removed = (await f.Provider.ApplyAsync(remove, 0, Guid.NewGuid(), default)).Value;
        removed.Status.ShouldBe(OpStatus.Applied);
        (await f.Provider.VerifyAsync(remove, removed, default)).Value.Status.ShouldBe(OpStatus.Verified);
        (await f.Stored()).Relationships.ShouldBeEmpty();
        refreshed = await f.Item();
        var deniedLink = await f.Proposal(new("denied-link", OpKind.Relate, refreshed.Key, refreshed.Revision,
            other: f.Key("I2"), relation: kind, concurrency: ConcurrencyMode.ObservedNonAtomic));
        await f.Import(deniedLink);
        f.Http.WriteStatus = 403;
        var denied = (await f.Provider.ApplyAsync(deniedLink, 0, Guid.NewGuid(), default)).Value;
        denied.Status.ShouldBe(OpStatus.Failed);
        (await f.Stored()).Relationships.ShouldNotContain(r => r.Origin == RelationshipOrigin.Local);
        f.Provider.Capabilities.LocalRelations.ShouldNotContain(kind);
    }

    [Theory]
    [InlineData(Provider.Hierarchy)]
    [InlineData(Provider.Dependency)]
    public async Task NativeMutationWithoutReadShapeIsNotProofOfLocallyAbsentSemantics(string kind)
    {
        using var f = await Fixture.Create();
        f.Http.Native = false;
        f.Http.MutationOnlyRelation = kind;
        f.Http.Native.ShouldBeFalse();
        f.Http.MutationOnlyRelation.ShouldBe(kind);
        var result = await f.Provider.DiscoverSchemaAsync(f.Connection, default);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(ErrorCode.Unsupported);
        f.Http.Writes.ShouldBe(0);
        (await f.Stored()).Relationships.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(Provider.Related, true)]
    [InlineData(Provider.Hierarchy, false)]
    [InlineData(Provider.Dependency, false)]
    public async Task ExplicitLocalRelationsAndTombstonesSurviveRefreshReopenDraftsAndBenches(string kind, bool native)
    {
        using var f = await Fixture.Create();
        f.Http.Native = native;
        var item = await f.Item();
        (await f.Item("I2")).Key.ShouldBe(f.Key("I2"));
        var before = await f.Stored();
        var bench = new Bench(Guid.NewGuid(), "retained", [new(SelectorKind.Pin, item.Key)]);
        var draft = new Draft(item.Key, item.Type, item, Fields((Provider.Title, FieldValue.String("local intent"))));
        (await f.Store.CommitAsync(f.Connection, before.Version, new(before.Version + 1, before.Items, before.Relationships,
            [draft], benches: [bench]), default)).IsSuccess.ShouldBeTrue();
        var proposal = await f.Proposal(new("local", OpKind.Relate, item.Key, other: f.Key("I2"), relation: kind,
            origin: RelationshipOrigin.Local));
        await f.Import(proposal);
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        ack.Status.ShouldBe(OpStatus.Applied);
        (await f.Provider.VerifyAsync(proposal, ack, default)).Value.Status.ShouldBe(OpStatus.Verified);
        var remove = await f.Proposal(new("remove", OpKind.Unrelate, item.Key, other: f.Key("I2"), relation: kind,
            origin: RelationshipOrigin.Local, expectedLocalVersion: 1));
        await f.Import(remove);
        var removed = (await f.Provider.ApplyAsync(remove, 0, Guid.NewGuid(), default)).Value;
        (await f.Provider.VerifyAsync(remove, removed, default)).Value.Status.ShouldBe(OpStatus.Verified);
        f.Reopen();
        await f.Item();
        var stored = await f.Stored();
        stored.Relationships.Single().ShouldBe(new Relationship(item.Key, f.Key("I2"), kind, RelationshipOrigin.Local, 2, true));
        stored.Drafts.Single().Changes.ShouldBe(draft.Changes);
        stored.Benches.Single().Name.ShouldBe("retained");
        f.Http.Writes.ShouldBe(0);
        var stale = await f.Proposal(new("stale", OpKind.Relate, item.Key, other: f.Key("I2"), relation: kind,
            origin: RelationshipOrigin.Local, expectedLocalVersion: 1));
        (await f.Provider.ImportAsync(stale, stale.Digest)).Error.Code.ShouldBe(ErrorCode.Conflict);
    }

    [Theory]
    [InlineData(403, OpStatus.Failed)]
    [InlineData(429, OpStatus.Failed)]
    [InlineData(500, OpStatus.Indeterminate)]
    [InlineData(503, OpStatus.Indeterminate)]
    public async Task HttpRejectionAndUncertainExecutionHaveDistinctDurableOutcomes(int status, OpStatus expected)
    {
        using var f = await Fixture.Create();
        var proposal = await f.Edit();
        await f.Import(proposal);
        f.Http.WriteStatus = status;
        var receipt = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        receipt.Status.ShouldBe(expected);
        (await f.Stored()).Journal.Single().Status.ShouldBe(expected);
        f.Reopen();
        (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(1);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("cancel")]
    [InlineData("graphql")]
    [InlineData("lost-ack")]
    [InlineData("wrong-attempt")]
    [InlineData("receipt-storage")]
    public async Task UnknownCreateIsFencedAcrossCrashesReopenAndReplacementDigests(string failure)
    {
        using var f = await Fixture.Create();
        var local = ItemKey.Local(Guid.NewGuid());
        var fields = Fields((Provider.Title, FieldValue.String("seed")), (Provider.StateField, FieldValue.String("OPEN")));
        var draft = new Draft(local, "T1", null, fields);
        (await f.Store.CommitAsync(f.Connection, 0, new(1, drafts: [draft]), default)).IsSuccess.ShouldBeTrue();
        var proposal = await f.Proposal(new("create", OpKind.Create, local, fields: fields, type: "T1",
            concurrency: ConcurrencyMode.ObservedNonAtomic));
        await f.Import(proposal);
        f.Http.WriteFailure = failure;
        if (failure == "receipt-storage") await f.FailReceiptPersistence();
        var outcome = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        outcome.Status.ShouldBe(OpStatus.Indeterminate);
        f.Http.Writes.ShouldBe(1);
        f.Http.Issues.Count.ShouldBe(3);
        if (failure == "receipt-storage") await f.ClearReceiptFailure();
        f.Reopen();
        (await f.Provider.RecoverAsync(proposal, 0)).Value.Status.ShouldBe(OpStatus.Indeterminate);
        var replacement = await f.Proposal(new("replacement", OpKind.Create, local, fields: fields, type: "T1",
            concurrency: ConcurrencyMode.ObservedNonAtomic));
        replacement.Digest.ShouldNotBe(proposal.Digest);
        (await f.Provider.ImportAsync(replacement, replacement.Digest)).IsSuccess.ShouldBeFalse();
        (await f.Stored()).Mappings.ShouldBeEmpty();
        f.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task CreateAcknowledgementMappingAndSeparateReadbackAreDurableWithoutTitleAdoption()
    {
        using var f = await Fixture.Create();
        var local = ItemKey.Local(Guid.NewGuid());
        var fields = Fields((Provider.Title, FieldValue.String("seed")), (Provider.StateField, FieldValue.String("OPEN")));
        (await f.Store.CommitAsync(f.Connection, 0, new(1, drafts: [new(local, "T1", null, fields)]), default)).IsSuccess.ShouldBeTrue();
        var proposal = await f.Proposal(new("create", OpKind.Create, local, fields: fields, type: "T1",
            concurrency: ConcurrencyMode.ObservedNonAtomic));
        await f.Import(proposal);
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        ack.Status.ShouldBe(OpStatus.Applied);
        ack.PublishedKey.ShouldBe(f.Key("I3"));
        var stored = await f.Stored();
        stored.Journal.Single().Status.ShouldBe(OpStatus.Applied);
        stored.Mappings.Single().Local.ShouldBe(local);
        stored.Mappings.Single().Remote.ShouldBe(ack.PublishedKey);
        f.Reopen();
        (await f.Provider.RecoverAsync(proposal, 0)).Value.Status.ShouldBe(OpStatus.Verified);
        (await f.Stored()).Items.Single().Key.ShouldBe(ack.PublishedKey);
        f.Http.Writes.ShouldBe(1);
    }

    [Theory]
    [InlineData(false, OpStatus.Verified)]
    [InlineData(true, OpStatus.Indeterminate)]
    public async Task CommentsUseAttributedNativeIdsAndCannotBeReplayedOrVerifiedUnderAnotherActor(bool changedActor, OpStatus expected)
    {
        using var f = await Fixture.Create();
        var item = await f.Item();
        var proposal = await f.Proposal(new("comment", OpKind.Comment, item.Key, item.Revision,
            body: FieldValue.RichText("exact **comment**", FieldKind.Markdown), concurrency: ConcurrencyMode.ObservedNonAtomic));
        await f.Import(proposal);
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        ack.Status.ShouldBe(OpStatus.Applied);
        ack.Evidence.ShouldContain("comment-id:C1");
        f.Reopen();
        if (changedActor) f.Access.Actor = f.Http.Actor = "other-actor";
        (await f.Provider.RecoverAsync(proposal, 0)).Value.Status.ShouldBe(expected);
        (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task SeparateEffectsStayOrderedAndOriginalObservationCannotBeCarriedForward()
    {
        using var f = await Fixture.Create();
        var item = await f.Item();
        var schema = await f.Schema();
        var proposal = new Proposal(f.Connection, schema.Fingerprint, (await f.Stored()).Version, [
            new("title", OpKind.Batch, item.Key, item.Revision, Fields((Provider.Title, FieldValue.String("first"))),
                concurrency: ConcurrencyMode.ObservedNonAtomic),
            new("body", OpKind.Batch, item.Key, item.Revision, Fields((Provider.Body, FieldValue.RichText("second", FieldKind.Markdown))),
                concurrency: ConcurrencyMode.ObservedNonAtomic)]);
        await f.Import(proposal);
        (await f.Provider.ApplyAsync(proposal, 1, Guid.NewGuid(), default)).IsSuccess.ShouldBeFalse();
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        (await f.Provider.VerifyAsync(proposal, ack, default)).Value.Status.ShouldBe(OpStatus.Verified);
        (await f.Provider.ApplyAsync(proposal, 1, Guid.NewGuid(), default)).Error.Code.ShouldBe(ErrorCode.Conflict);
        f.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task AdmissionRefusalIsNoDispatchWhileLostNativeResponseDurabilityIsUnknown()
    {
        using var f = await Fixture.Create();
        var proposal = await f.Edit();
        await f.Import(proposal);
        f.Access.Deny = true;
        var result = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        result.Status.ShouldBe(OpStatus.Failed);
        result.Evidence.ShouldContain(e => e.StartsWith("no-dispatch:", StringComparison.Ordinal));
        f.Http.Writes.ShouldBe(0);
        f.Access.SawDurableAttempt.ShouldBeTrue();
    }

    [Fact]
    public async Task CompetingAdapterAttemptLosesDurableCasBeforeTransport()
    {
        using var f = await Fixture.Create();
        var proposal = await f.Edit();
        await f.Import(proposal);
        f.Access.PauseAdmission = true;
        var winner = f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default);
        await f.Access.AdmissionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var competitor = new Provider(f.Connection, f.Access, f.Store, f.Http);
        var loser = await competitor.ApplyAsync(proposal, 0, Guid.NewGuid(), default);
        loser.IsSuccess.ShouldBeFalse();
        loser.Error.Code.ShouldBe(ErrorCode.Indeterminate);
        f.Access.ResumeAdmission.TrySetResult();
        (await winner).Value.Status.ShouldBe(OpStatus.Applied);
        f.Http.Writes.ShouldBe(1);
        (await f.Stored()).Journal.Single().Receipts.Length.ShouldBe(1);
    }

    [Theory]
    [InlineData("readback-denied", OpStatus.Indeterminate)]
    [InlineData("readback-different", OpStatus.Partial)]
    public async Task ReadbackFailureRetainsAcknowledgementAndNeverPermitsReplay(string defect, OpStatus expected)
    {
        using var f = await Fixture.Create();
        var proposal = await f.Edit();
        await f.Import(proposal);
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        ack.Status.ShouldBe(OpStatus.Applied);
        if (defect == "readback-denied") f.Http.Defect = "metadata-denied";
        else f.Http.Issues["I1"]["title"] = "changed by another writer";
        (await f.Provider.VerifyAsync(proposal, ack, default)).Value.Status.ShouldBe(expected);
        var receipts = (await f.Stored()).Journal.Single().Receipts;
        receipts.Length.ShouldBe(2);
        receipts[0].Status.ShouldBe(OpStatus.Applied);
        f.Http.Writes.ShouldBe(1);
    }

    private static FieldValues Fields(params (string Name, FieldValue Value)[] fields)
        => new(fields.Select(f => KeyValuePair.Create(f.Name, f.Value)));

    private sealed class Fixture : IDisposable
    {
        internal Connection Connection { get; } = new(new("github"), "https://api.github.com", Provider.Destination("O1", "R1"), "selected-identity");
        internal NativeHttp Http { get; } = new();
        internal Access Access { get; }
        internal SqliteStore Store { get; private set; }
        internal Provider Provider { get; private set; }
        private readonly string _directory;
        private readonly string _path;

        private Fixture(string directory, SqliteStore store)
        {
            _directory = directory; _path = Path.Combine(directory, "core.db"); Store = store;
            Access = new(this); Provider = new(Connection, Access, Store, Http);
        }

        internal static async Task<Fixture> Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "twig-github-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var result = await SqliteStore.CreateAsync(Path.Combine(directory, "core.db"));
            result.IsSuccess.ShouldBeTrue();
            return new(directory, result.Value);
        }

        internal ItemKey Key(string id) => ItemKey.Remote(Connection.Provider, Connection.ResourceScope, id);
        internal async Task<ProcessSchema> Schema()
        {
            var result = await Provider.DiscoverSchemaAsync(Connection, default);
            result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
            return result.Value;
        }
        internal async Task<StoreSnapshot> Stored() => (await Store.ReadAsync(Connection, default)).Value;
        internal async Task<WorkItem> Item(string id = "I1")
        {
            var result = await Provider.ReadAsync(Connection, [Key(id)], default);
            result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
            return result.Value.Items.Single();
        }
        internal async Task<Proposal> Proposal(Op op, ProcessSchema? schema = null)
            => new(Connection, (schema ?? await Schema()).Fingerprint, (await Stored()).Version, [op]);
        internal async Task<Proposal> Edit()
        {
            var item = await Item();
            return await Proposal(new("edit", OpKind.Batch, item.Key, item.Revision, Fields((Provider.Title, FieldValue.String("changed"))),
                concurrency: ConcurrencyMode.ObservedNonAtomic));
        }
        internal async Task Import(Proposal proposal)
        {
            var result = await Provider.ImportAsync(proposal, proposal.Digest);
            result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        }
        internal void Reopen()
        {
            Provider.Dispose();
            Store = SqliteStore.OpenAsync(_path).GetAwaiter().GetResult().Value;
            Provider = new(Connection, Access, Store, Http);
        }
        internal async Task FailReceiptPersistence() => await Sql(
            "CREATE TRIGGER reject_receipt BEFORE UPDATE ON core_snapshots WHEN instr(NEW.document, 'github-response:') > 0 BEGIN SELECT RAISE(ABORT, 'receipt unavailable'); END;");
        internal async Task ClearReceiptFailure() => await Sql("DROP TRIGGER reject_receipt;");
        private async Task Sql(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        public void Dispose()
        {
            Provider.Dispose();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class Access(Fixture fixture) : GitHubAccess
    {
        internal string Actor = "A1";
        internal string Context = "binding-7/attachment-3/selected-gh-account";
        internal GitHubCredentialSource Source = GitHubCredentialSource.Gh;
        internal bool InvalidLease, Deny, CloseGate, SawDurableAttempt, PauseAdmission;
        internal TaskCompletionSource AdmissionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ResumeAdmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? RuleDefect;
        internal int Observed;
        public override Task<Result<GitHubSession>> AcquireAsync(Connection connection, CancellationToken ct)
            => Task.FromResult(Result<GitHubSession>.Ok(new Session(this, fixture)));

        private sealed class Session(Access access, Fixture fixture) : GitHubSession
        {
            public override Connection Connection => fixture.Connection;
            public override Uri Endpoint => new("https://api.github.com/graphql");
            public override string ActorId => access.Actor;
            public override string AcquisitionContext => access.Context;
            public override GitHubCredentialSource CredentialSource => access.Source;
            public override AuthenticationHeaderValue Authorization => new("Bearer", "synthetic-token");
            public override Task ValidateAsync(CancellationToken ct)
            {
                if (access.InvalidLease) throw new InvalidOperationException("Selected lease changed.");
                ct.ThrowIfCancellationRequested(); return Task.CompletedTask;
            }
            public override Task<Result<GitHubRuleProjection>> ReadRuleProjectionAsync(ProcessSchema nativeProjection, CancellationToken ct)
            {
                if (access.RuleDefect == "rules-unsupported")
                    return Task.FromResult(Result<GitHubRuleProjection>.Fail(ErrorCode.Unsupported, "Configured Twig policy evidence unavailable."));
                var map = nativeProjection.Types.ToImmutableDictionary(t => t.Reference, _ => ImmutableArray<FieldRule>.Empty);
                if (access.RuleDefect == "rules-incomplete") map = map.Remove("T1");
                if (access.RuleDefect == "unknown-rule")
                    map = map.SetItem("T1", [new(Provider.Title, RuleAction.Unknown, [])]);
                if (access.CloseGate)
                    map = map.ToImmutableDictionary(p => p.Key, _ => ImmutableArray.Create(new FieldRule(Provider.Title, RuleAction.Required,
                        [new(ConditionKind.Equal, Provider.StateField, FieldValue.String("CLOSED"))])));
                return Task.FromResult(Result<GitHubRuleProjection>.Ok(new(fixture.Connection, access.Actor,
                    access.RuleDefect == "rules-stale" ? "stale" : nativeProjection.Fingerprint,
                    access.RuleDefect == "rules-scope" ? GitHubValidationScope.Unspecified
                        : GitHubValidationScope.NativeApiAndDeclaredTwigPolicies,
                    "synthetic-test-only-policy", "v1", map)));
            }
            public override async Task<Result<GitHubWriteAdmission>> AdmitAsync(GitHubWriteIntent intent, CancellationToken ct)
            {
                var stored = await fixture.Stored();
                access.SawDurableAttempt = stored.Journal.Any(j => j.Digest == intent.Proposal.Digest
                    && j.Operation == intent.Operation && j.Attempt == intent.Attempt && j.Status == OpStatus.Applying);
                access.SawDurableAttempt.ShouldBeTrue();
                intent.Payload.ShouldContain(intent.Attempt.ToString("D"));
                if (access.PauseAdmission)
                {
                    access.AdmissionEntered.TrySetResult();
                    await access.ResumeAdmission.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                }
                return access.Deny ? Result<GitHubWriteAdmission>.Fail(ErrorCode.Conflict, "Native host refused")
                    : Result<GitHubWriteAdmission>.Ok(new Admission(access, fixture));
            }
            public override void Dispose() { }
        }

        private sealed class Admission(Access access, Fixture fixture) : GitHubWriteAdmission
        {
            public override Task ObserveAsync(int statusCode, string body, CancellationToken ct)
            {
                access.Observed++;
                if (fixture.Http.WriteFailure == "lost-ack") throw new IOException("Synthetic host native-response durability failure.");
                return Task.CompletedTask;
            }
            public override void Dispose() { }
        }
    }

    private sealed class NativeHttp : HttpMessageHandler
    {
        internal string Owner = "O1", RepositoryId = "R1", Actor = "A1";
        internal bool Native = true, ForeignResource;
        internal string? Defect, WriteFailure, LastPayload, MutationOnlyRelation;
        internal int Writes, WriteStatus = 200;
        internal Dictionary<string, JsonObject> Issues { get; } = new()
        {
            ["I1"] = Issue("I1"), ["I2"] = Issue("I2"),
        };
        private readonly HashSet<(string From, string To, string Kind)> _edges = [];
        private readonly Dictionary<string, JsonObject> _comments = [];
        private int _clock;

        private static JsonObject Issue(string id) => new()
        {
            ["id"] = id, ["__typename"] = "Issue", ["title"] = "original", ["body"] = "", ["state"] = "OPEN",
            ["updatedAt"] = "2026-10-30T12:00:00Z", ["issueType"] = new JsonObject { ["id"] = "T1" },
            ["milestone"] = null, ["labelIds"] = new JsonArray(), ["assigneeIds"] = new JsonArray(),
        };
        private JsonObject Repo() => new() { ["id"] = RepositoryId, ["owner"] = new JsonObject { ["id"] = Owner } };
        private static JsonArray Names(IEnumerable<string> names) => new(names.Select(n => (JsonNode)new JsonObject
        {
            ["name"] = n, ["type"] = NativeType(n),
        }).ToArray());
        private static JsonObject NativeType(string name)
        {
            if (name is "labelIds" or "assigneeIds")
                return new() { ["kind"] = "LIST", ["name"] = null, ["ofType"] = new JsonObject { ["kind"] = "SCALAR", ["name"] = "ID" } };
            var type = name switch
            {
                "id" or "repositoryId" or "issueTypeId" or "milestoneId" => "ID",
                "state" => "IssueState", "updatedAt" => "DateTime", "repository" => "Repository", "issueType" => "IssueType",
                "milestone" => "Milestone", "labels" => "LabelConnection", "assignees" => "UserConnection",
                "parent" => "Issue", "subIssues" or "blockedBy" or "blocking" => "IssueConnection",
                _ => "String",
            };
            return new() { ["kind"] = "SCALAR", ["name"] = type };
        }
        private static JsonObject Page(IEnumerable<JsonNode> nodes, bool next = false) => new()
        {
            ["nodes"] = new JsonArray(nodes.ToArray()), ["pageInfo"] = new JsonObject { ["hasNextPage"] = next, ["endCursor"] = next ? "next" : null },
        };
        private JsonObject Peer(string id) => new() { ["id"] = id, ["repository"] = Repo() };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri!.AbsoluteUri.ShouldBe("https://api.github.com/graphql");
            request.Headers.Authorization!.Parameter.ShouldBe("synthetic-token");
            var raw = await request.Content!.ReadAsStringAsync(cancellationToken);
            var root = JsonNode.Parse(raw)!;
            var query = root["query"]!.GetValue<string>();
            var variables = root["variables"]!;
            var id = variables["id"]?.GetValue<string>() ?? "I1";
            if (query.StartsWith("mutation", StringComparison.Ordinal))
                return Write(root, raw);
            if (Defect == "partial-graphql") return Json(new JsonObject { ["data"] = new JsonObject(), ["errors"] = new JsonArray(new JsonObject { ["message"] = "denied" }) });
            JsonObject data;
            if (query.Contains("viewer {", StringComparison.Ordinal))
            {
                var repo = Repo(); repo["__typename"] = "Repository";
                data = new() { ["viewer"] = new JsonObject { ["id"] = Actor }, ["node"] = repo };
            }
            else if (query.Contains("__type(", StringComparison.Ordinal))
            {
                if (Defect == "metadata-denied") return new(HttpStatusCode.Forbidden);
                var fields = new List<string> { "id", "repository", "title", "body", "state", "updatedAt", "labels", "assignees", "milestone", "issueType" };
                if (Native) fields.AddRange(["parent", "subIssues", "blockedBy", "blocking"]);
                if (Defect == "partial-relations") fields.Remove("subIssues");
                var inputs = new[] { "id", "title", "body", "state", "labelIds", "assigneeIds", "milestoneId" };
                var mutations = new List<string> { "createIssue", "updateIssue", "addComment" };
                if (Native || MutationOnlyRelation == Provider.Hierarchy) mutations.AddRange(["addSubIssue", "removeSubIssue"]);
                if (Native || MutationOnlyRelation == Provider.Dependency) mutations.AddRange(["addBlockedBy", "removeBlockedBy"]);
                data = new()
                {
                    ["issue"] = new JsonObject { ["fields"] = Names(fields) },
                    ["issueType"] = new JsonObject { ["fields"] = Names(["id", "name", "isEnabled"]) },
                    ["repository"] = new JsonObject { ["fields"] = Names(["issues", "issueTypes"]) },
                    ["states"] = new JsonObject { ["enumValues"] = Names(Defect == "unknown-state" ? ["OPEN", "UNKNOWN"] : ["OPEN", "CLOSED"]) },
                    ["mutations"] = new JsonObject { ["fields"] = Names(mutations) },
                    ["update"] = new JsonObject { ["inputFields"] = Names(Defect == "unknown-input" ? inputs.Where(i => i != "milestoneId") : inputs) },
                    ["create"] = new JsonObject { ["inputFields"] = Names(["repositoryId", "title", "body", "issueTypeId", "labelIds", "assigneeIds", "milestoneId"]) },
                };
                if (Defect == "field-signature") data["issue"]!["fields"]!.AsArray()
                    .Single(n => n!["name"]!.GetValue<string>() == "title")!["type"]!["name"] = "Int";
            }
            else if (query.Contains("issueTypes(first:", StringComparison.Ordinal))
            {
                var repo = Repo();
                repo["issueTypes"] = Page([new JsonObject { ["id"] = "T1", ["name"] = "Custom native type",
                    ["isEnabled"] = Defect != "type-disabled" }], Defect == "types-page");
                data = new() { ["node"] = repo };
            }
            else if (query.Contains("issues(first:", StringComparison.Ordinal))
            {
                var repo = Repo();
                var ids = Issues.Keys.Select(i => (JsonNode)new JsonObject { ["id"] = i }).ToList();
                if (Defect == "duplicate-candidates") ids.Add(new JsonObject { ["id"] = "I1" });
                repo["issues"] = Page(ids, Defect == "issues-page");
                data = new() { ["node"] = repo };
            }
            else if (query.Contains("... on Label", StringComparison.Ordinal))
            {
                var kind = id.StartsWith('L') ? "Label" : id.StartsWith('M') ? "Milestone" : "User";
                var resource = new JsonObject { ["__typename"] = kind, ["id"] = id };
                if (kind != "User")
                {
                    resource["repository"] = Repo();
                    if (ForeignResource) resource["repository"]!["owner"]!["id"] = "foreign";
                }
                data = new() { ["node"] = resource };
            }
            else if (query.Contains("... on IssueComment", StringComparison.Ordinal))
                data = new() { ["node"] = _comments[id].DeepClone() };
            else if (query.Contains("(first: 100)", StringComparison.Ordinal))
            {
                var field = new[] { "labels", "assignees", "subIssues", "blockedBy", "blocking" }.Single(f => query.Contains(f + "(first:", StringComparison.Ordinal));
                IEnumerable<JsonNode> nodes = field switch
                {
                    "labels" => Issues[id]["labelIds"]!.AsArray().Select(i => (JsonNode)new JsonObject { ["id"] = i!.GetValue<string>() }),
                    "assignees" => Issues[id]["assigneeIds"]!.AsArray().Select(i => (JsonNode)new JsonObject { ["id"] = i!.GetValue<string>() }),
                    "subIssues" => _edges.Where(e => e.From == id && e.Kind == Provider.Hierarchy).Select(e => (JsonNode)Peer(e.To)),
                    "blockedBy" => _edges.Where(e => e.To == id && e.Kind == Provider.Dependency).Select(e => (JsonNode)Peer(e.From)),
                    _ => _edges.Where(e => e.From == id && e.Kind == Provider.Dependency).Select(e => (JsonNode)Peer(e.To)),
                };
                data = new() { ["node"] = new JsonObject { ["id"] = id, ["__typename"] = "Issue",
                    [field] = Page(nodes, Defect == "labels-page" && field == "labels" || Defect == "relationships-page" && field == "subIssues") } };
            }
            else
            {
                var issue = (JsonObject)Issues[id].DeepClone();
                issue["repository"] = Repo();
                var parent = _edges.SingleOrDefault(e => e.To == id && e.Kind == Provider.Hierarchy);
                issue["parent"] = parent.From is null ? null : Peer(parent.From);
                data = new() { ["node"] = issue };
            }
            return Json(new JsonObject { ["data"] = data });
        }

        private HttpResponseMessage Write(JsonNode root, string raw)
        {
            Writes++; LastPayload = raw;
            if (WriteStatus is 400 or 401 or 403 or 404 or 405 or 415 or 422 or 429)
                return new((HttpStatusCode)WriteStatus) { Content = new StringContent("{\"message\":\"rejected\"}") };
            var query = root["query"]!.GetValue<string>();
            var input = root["variables"]!["input"]!;
            var mutation = new[] { "createIssue", "updateIssue", "addComment", "addSubIssue", "removeSubIssue", "addBlockedBy", "removeBlockedBy" }
                .Single(m => query.Contains(" " + m + "(", StringComparison.Ordinal));
            var payload = new JsonObject { ["clientMutationId"] = WriteFailure == "wrong-attempt" ? "unrelated" : input["clientMutationId"]!.GetValue<string>() };
            if (mutation is "createIssue" or "updateIssue")
            {
                var id = mutation == "createIssue" ? "I3" : input["id"]!.GetValue<string>();
                if (mutation == "createIssue")
                {
                    Issues.Add(id, Issue(id));
                    Issues[id]["issueType"] = input["issueTypeId"] is { } type ? new JsonObject { ["id"] = type.GetValue<string>() } : null;
                }
                foreach (var field in new[] { "title", "body", "state", "labelIds", "assigneeIds" })
                    if (input.AsObject().ContainsKey(field)) Issues[id][field] = input[field]?.DeepClone();
                if (input.AsObject().ContainsKey("milestoneId"))
                    Issues[id]["milestone"] = input["milestoneId"] is { } milestone ? new JsonObject { ["id"] = milestone.GetValue<string>() } : null;
                Issues[id]["updatedAt"] = $"2026-10-30T12:00:{++_clock:00}Z";
                payload["issue"] = Peer(id);
            }
            else if (mutation == "addComment")
            {
                var id = input["subjectId"]!.GetValue<string>();
                _comments["C1"] = new() { ["id"] = "C1", ["__typename"] = "IssueComment", ["body"] = input["body"]!.GetValue<string>(),
                    ["author"] = new JsonObject { ["id"] = Actor }, ["issue"] = Peer(id) };
                payload["commentEdge"] = new JsonObject { ["node"] = new JsonObject { ["id"] = "C1" } };
            }
            else
            {
                var hierarchy = mutation.EndsWith("SubIssue", StringComparison.Ordinal);
                var from = input[hierarchy ? "issueId" : "blockingIssueId"]!.GetValue<string>();
                var to = input[hierarchy ? "subIssueId" : "issueId"]!.GetValue<string>();
                var edge = (from, to, hierarchy ? Provider.Hierarchy : Provider.Dependency);
                if (mutation.StartsWith("add", StringComparison.Ordinal)) _edges.Add(edge); else _edges.Remove(edge);
                Issues[from]["updatedAt"] = $"2026-10-30T12:00:{++_clock:00}Z";
            }
            if (WriteFailure == "network") throw new HttpRequestException("Synthetic connection loss after dispatch.");
            if (WriteFailure == "cancel") throw new OperationCanceledException("Synthetic cancellation after dispatch.");
            if (WriteFailure == "graphql") return Json(new JsonObject { ["data"] = new JsonObject { [mutation] = payload },
                ["errors"] = new JsonArray(new JsonObject { ["message"] = "resolver failed after effect" }) });
            var response = Json(new JsonObject { ["data"] = new JsonObject { [mutation] = payload } });
            response.StatusCode = (HttpStatusCode)WriteStatus;
            return response;
        }
        private static HttpResponseMessage Json(JsonObject root) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        protected override void Dispose(bool disposing) { /* Reused synthetic HTTP boundary across adapter reopen. */ }
    }
}
