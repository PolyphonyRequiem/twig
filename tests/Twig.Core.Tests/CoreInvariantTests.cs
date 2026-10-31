using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Twig.Core;
using Xunit;
using Rules = Twig.Core.Core;

namespace Twig.Core.Tests;

public sealed class CoreInvariantTests
{
    private static readonly ProviderId Provider = new("test");
    private static readonly Connection Connection = new(Provider, "scope", "immutable-target", "principal");
    private static readonly ItemKey Key = ItemKey.Remote(Provider, "scope", "1");
    private static readonly ProviderCapabilities Capabilities = new(Enum.GetValues<OpKind>(),
        ["hierarchy", "dependency"], ["related"], true, true);

    private static FieldValues Fields(params (string Name, FieldValue Value)[] entries)
        => new(entries.Select(e => KeyValuePair.Create(e.Name, e.Value)));

    private static WorkItem Item(string token = "rev-1", FieldValues? fields = null, ItemKey? key = null,
        bool complete = true, string type = "record")
    {
        var target = key ?? Key;
        return new(target, type, fields ?? Fields(("phase", FieldValue.String("open")), ("gate", FieldValue.String("evidence"))),
            new Revision(target, token), complete);
    }

    private static ProcessSchema Schema(IEnumerable<FieldRule>? rules = null, IEnumerable<string>? gaps = null)
        => new(Connection,
        [
            new("record", "phase",
                [new("phase", FieldKind.Text), new("gate", FieldKind.Text), new("count", FieldKind.Integer)],
                [new("open", StateCategory.InProgress), new("finished", StateCategory.Completed)],
                [new("open", "finished")],
                rules ?? [new("gate", RuleAction.Required, [new(ConditionKind.Equal, "phase", FieldValue.String("finished"))])]),
        ],
        [new("hierarchy", RelationShape.Hierarchy, true), new("dependency", RelationShape.Dependency, true),
            new("related", RelationShape.Association, false)], gaps);

    private static Proposal Batch(FieldValues fields, ProcessSchema schema, string token = "rev-1",
        ConcurrencyMode concurrency = ConcurrencyMode.ExactRevision)
        => new(Connection, schema.Fingerprint, 0,
            [new("edit", OpKind.Batch, Key, new Revision(Key, token), fields, concurrency: concurrency)]);

    [Fact]
    public void IdentityAndRevisionAreScopedAndRejectSyntheticLocalRevisions()
    {
        Key.ShouldNotBe(ItemKey.Remote(Provider, "different-scope", "1"));
        Key.ShouldNotBe(ItemKey.Remote(new ProviderId("different-provider"), "scope", "1"));
        Key.ShouldNotBe(ItemKey.Remote(Provider, "scope", "01"));
        Should.Throw<ArgumentException>(() => new Revision(ItemKey.Local(Guid.NewGuid()), "0"));
        Should.Throw<ArgumentException>(() => new Revision(Key, ""));
        Should.Throw<ArgumentException>(() => new WorkItem(Key, "record", FieldValues.Empty,
            new Revision(ItemKey.Remote(Provider, "scope", "2"), "rev-1")));
        Should.Throw<ArgumentException>(() => new ProviderId("MixedCase"));
    }

    [Fact]
    public void TypedFieldsFreezeCallerCollectionsAndDistinguishAbsentClearEmptyAndType()
    {
        var list = new List<string> { "a" };
        var pairs = new Dictionary<string, FieldValue> { ["list"] = FieldValue.Strings(list) };
        var values = new FieldValues(pairs);
        list.Add("b"); pairs["list"] = FieldValue.Null;
        values["list"].ShouldBe(FieldValue.Strings(["a"]));
        values.ContainsKey("clear").ShouldBeFalse();
        values.Set("clear", FieldValue.Null)["clear"].ShouldNotBe(FieldValue.String(""));
        FieldValue.Integer(1).ShouldNotBe(FieldValue.String("1"));
        FieldValue.Decimal(1.0m).ShouldBe(FieldValue.Decimal(1.00m));
        FieldValue.At(DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"))
            .ShouldNotBe(FieldValue.At(DateTimeOffset.Parse("2026-01-01T01:00:00+01:00")));
        Should.Throw<ArgumentException>(() => new FieldValues(
            [KeyValuePair.Create("x", FieldValue.Null), KeyValuePair.Create("x", FieldValue.String(""))]));
        Should.Throw<ArgumentNullException>(() => values.Set("x", null!));
    }

    [Fact]
    public void CanonicalProposalHasGoldenBytesAndAnIndependentSha256Oracle()
    {
        var connection = new Connection(new("p"), "s", "d", "i");
        var key = ItemKey.Remote(connection.Provider, "s", "1");
        var proposal = new Proposal(connection, "schema", 0,
            [new("op", OpKind.Batch, key, new Revision(key, "r"), Fields(("state", FieldValue.String("open"))))]);
        const string expected = """{"version":2,"connection":{"provider":"p","scope":"s","destination":"d","identity":"i"},"schema":"schema","storeVersion":0,"operations":[{"id":"op","kind":1,"item":["remote","p","s","1"],"revision":{"item":["remote","p","s","1"],"token":"r"},"type":null,"fields":{"state":[1,"open"]},"other":null,"relation":null,"origin":0,"concurrency":0,"localVersion":null,"body":null,"resource":null}]}""";
        Encoding.UTF8.GetString(Rules.ProposalBytes(proposal)).ShouldBe(expected);
        proposal.Digest.ShouldBe(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expected))).ToLowerInvariant());
        var output = Rules.ProposalBytes(proposal);
        output[0] = 0;
        Encoding.UTF8.GetString(Rules.ProposalBytes(proposal)).ShouldBe(expected);
    }

    [Fact]
    public void ResourceLinksKeepLocatorsSeparateFromItemIdentityAndDigestExactAttributes()
    {
        var schema = Schema();
        schema = new(Connection, schema.Types, schema.Relations.Append(new("resource", RelationShape.Resource, false)));
        var source = new WorkItem(Key, "record", Item().Fields, Item().Revision, true, [], true);
        var resource = new ResourceLink("resource", "https://example.test/a%2Fb", "", null);
        Proposal Link(ResourceLink link) => new(Connection, schema.Fingerprint, 0,
            [new("link", OpKind.LinkResource, Key, source.Revision, resource: link)]);
        var proposal = Link(resource);
        Rules.Validate(proposal, proposal.Digest, schema, new(0, [source]), Capabilities).ShouldBeEmpty();
        proposal.Operations[0].Other.ShouldBeNull();
        proposal.Digest.ShouldNotBe(Link(new("resource", "https://example.test/a%2Fb", null, null)).Digest);
        proposal.Digest.ShouldNotBe(Link(new("resource", "https://example.test/a/b", "", null)).Digest);
        var incomplete = new WorkItem(Key, source.Type, source.Fields, source.Revision, true);
        Rules.Validate(proposal, proposal.Digest, schema, new(0, [incomplete]), Capabilities)
            .ShouldContain(e => e.Code == ErrorCode.Unavailable);
        var existing = new WorkItem(Key, source.Type, source.Fields, source.Revision, true, [resource], true);
        Rules.Validate(proposal, proposal.Digest, schema, new(0, [existing]), Capabilities)
            .ShouldContain(e => e.Code == ErrorCode.Conflict);
        Should.Throw<ArgumentException>(() => new ResourceLink("resource", "https://user:secret@example.test/a"));
    }

    [Fact]
    public void DigestNormalizesMapOrderAndDecimalScaleButBindsEveryExecutionChoice()
    {
        var schema = Schema();
        var one = Batch(Fields(("count", FieldValue.Integer(2)), ("gate", FieldValue.String("x"))), schema);
        var reordered = Batch(Fields(("gate", FieldValue.String("x")), ("count", FieldValue.Integer(2))), schema);
        one.Digest.ShouldBe(reordered.Digest);
        var variants = new[]
        {
            new Proposal(new(Provider, "scope", "other-destination", "principal"), one.SchemaFingerprint, 0, one.Operations),
            new Proposal(new(Provider, "scope", "immutable-target", "other-principal"), one.SchemaFingerprint, 0, one.Operations),
            new Proposal(Connection, "other-schema", 0, one.Operations),
            new Proposal(Connection, one.SchemaFingerprint, 1, one.Operations),
            Batch(one.Operations[0].Fields, schema, "rev-2"),
            Batch(one.Operations[0].Fields, schema, concurrency: ConcurrencyMode.ObservedNonAtomic),
            Batch(Fields(("count", FieldValue.Integer(3)), ("gate", FieldValue.String("x"))), schema),
        };
        variants.All(v => v.Digest != one.Digest).ShouldBeTrue();
        var a = new Op("a", OpKind.Delete, Key, new Revision(Key, "rev-1"));
        var b = new Op("b", OpKind.Delete, ItemKey.Remote(Provider, "scope", "2"),
            new Revision(ItemKey.Remote(Provider, "scope", "2"), "rev-1"));
        new Proposal(Connection, "schema", 0, [a, b]).Digest.ShouldNotBe(new Proposal(Connection, "schema", 0, [b, a]).Digest);
        Batch(Fields(("x", FieldValue.Decimal(1.00m))), schema).Digest
            .ShouldBe(Batch(Fields(("x", FieldValue.Decimal(1.0m))), schema).Digest);
    }

    [Fact]
    public void ProposalCopiesIntentAndSchemaFingerprintIncludesRulesAndCompleteness()
    {
        var schema = Schema();
        var ops = new List<Op> { new("op", OpKind.Delete, Key, new Revision(Key, "rev-1")) };
        var proposal = new Proposal(Connection, schema.Fingerprint, 0, ops);
        var digest = proposal.Digest;
        ops.Clear();
        proposal.Operations.Length.ShouldBe(1);
        proposal.Digest.ShouldBe(digest);
        Schema(rules: []).Fingerprint.ShouldNotBe(schema.Fingerprint);
        Schema(gaps: ["unknown rules"]).Fingerprint.ShouldNotBe(schema.Fingerprint);
        var type = schema.Types[0];
        var reordered = new ProcessSchema(Connection,
            [new(type.Reference, type.StateField, type.Fields.Reverse(), type.States.Reverse(), type.Transitions, type.Rules)],
            schema.Relations.Reverse());
        reordered.Fingerprint.ShouldBe(schema.Fingerprint);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ClosingNeedsGatesInTheSameAtomicBatch(bool includeGate, bool atomic)
    {
        var schema = Schema();
        var fields = Fields(("phase", FieldValue.String("finished")));
        if (includeGate) fields = fields.Set("gate", FieldValue.String("verified"));
        var proposal = Batch(fields, schema);
        var capabilities = new ProviderCapabilities(Enum.GetValues<OpKind>(), ["hierarchy"], ["related"], true, atomic);
        var errors = Rules.Validate(proposal, proposal.Digest, schema, new(0, [Item()]), capabilities);
        errors.IsEmpty.ShouldBe(includeGate && atomic);
        if (!includeGate) errors.ShouldContain(e => e.Message.Contains("same batch", StringComparison.Ordinal));
        if (includeGate && !atomic) errors.ShouldContain(e => e.Code == ErrorCode.Unsupported);
    }

    [Fact]
    public void ObservedIndependentFieldsDoNotGrantAtomicCloseGateOrTypeChangeAuthority()
    {
        var schema = Schema();
        var capabilities = new ProviderCapabilities(Enum.GetValues<OpKind>(), ["hierarchy"], ["related"], false, false);
        var independent = Batch(Fields(("gate", FieldValue.String("edited")), ("count", FieldValue.Integer(2))), schema,
            concurrency: ConcurrencyMode.ObservedNonAtomic);
        Rules.Validate(independent, independent.Digest, schema, new(0, [Item()]), capabilities).ShouldBeEmpty();
        var close = Batch(Fields(("phase", FieldValue.String("finished")), ("gate", FieldValue.String("evidence"))),
            schema, concurrency: ConcurrencyMode.ObservedNonAtomic);
        Rules.Validate(close, close.Digest, schema, new(0, [Item()]), capabilities)
            .ShouldContain(e => e.Code == ErrorCode.Unsupported);
        var typed = new Proposal(Connection, schema.Fingerprint, 0,
            [new("type", OpKind.Batch, Key, new Revision(Key, "rev-1"), independent.Operations[0].Fields,
                type: "record", concurrency: ConcurrencyMode.ObservedNonAtomic)]);
        Rules.Validate(typed, typed.Digest, schema, new(0, [Item()]), capabilities)
            .ShouldContain(e => e.Code == ErrorCode.Unsupported);
    }

    [Fact]
    public void PairedSupplierIsNotAnAuthoredCloseGateAndDisabledSupplierDoesNotCount()
    {
        var when = new[] { new RuleCondition(ConditionKind.Equal, "phase", FieldValue.String("finished")) };
        var requirement = new FieldRule("gate", RuleAction.Required, when);
        var supplied = Schema([requirement, new("gate", RuleAction.Supplied, when)]);
        var source = Item(fields: Fields(("phase", FieldValue.String("open")), ("gate", FieldValue.Null)));
        var proposal = Batch(Fields(("phase", FieldValue.String("finished"))), supplied);
        Rules.Validate(proposal, proposal.Digest, supplied, new(0, [source]), Capabilities).ShouldBeEmpty();
        var disabled = Schema([requirement, new("gate", RuleAction.Supplied, when, disabled: true)]);
        proposal = Batch(proposal.Operations[0].Fields, disabled);
        Rules.Validate(proposal, proposal.Digest, disabled, new(0, [source]), Capabilities)
            .ShouldContain(e => e.Code == ErrorCode.InvalidInput);
    }

    [Fact]
    public void MissingSchemaUnknownPredicatesAndUnobservedValuesCannotPermitWrites()
    {
        var unknown = Schema([new("gate", RuleAction.Required,
            [new(ConditionKind.Unknown, "phase"), new(ConditionKind.Equal, "phase", FieldValue.String("never"))])]);
        var proposal = Batch(Fields(("count", FieldValue.Integer(1))), unknown);
        Rules.Validate(proposal, proposal.Digest, unknown, new(0, [Item()]), Capabilities)
            .ShouldContain(e => e.Code == ErrorCode.Unavailable);
        var missing = Schema([new("gate", RuleAction.Required, [new(ConditionKind.Changed, "gate")])]);
        proposal = Batch(Fields(("gate", FieldValue.String("value"))), missing);
        Rules.Validate(proposal, proposal.Digest, missing,
            new(0, [Item(fields: Fields(("phase", FieldValue.String("open"))))]), Capabilities)
            .ShouldContain(e => e.Code == ErrorCode.Unavailable);
        var incomplete = Schema(gaps: ["requiredness unavailable"]);
        proposal = Batch(Fields(("count", FieldValue.Integer(1))), incomplete);
        Rules.Validate(proposal, proposal.Digest, incomplete, new(0, [Item()]), Capabilities)
            .ShouldContain(e => e.Code == ErrorCode.Unavailable);
    }

    [Fact]
    public void MalformedSchemaCannotAuthorizeNonFieldEffectsOrInventSuppliedValues()
    {
        var schemas = new[]
        {
            Schema([new("gate", RuleAction.Unknown, [])]),
            Schema([new("gate", RuleAction.Supplied, [], [FieldValue.Integer(42)])]),
            Schema([new("gate", RuleAction.Supplied, [], [FieldValue.String("a")]),
                new("gate", RuleAction.Supplied, [], [FieldValue.String("b")])]),
        };
        foreach (var schema in schemas)
        {
            var proposal = Batch(Fields(("count", FieldValue.Integer(1))), schema);
            Rules.Validate(proposal, proposal.Digest, schema, new(0, [Item()]), Capabilities)
                .ShouldContain(e => e.Code == ErrorCode.Unavailable);
        }
        var unknown = schemas[0];
        var deletion = new Proposal(Connection, unknown.Fingerprint, 0,
            [new("delete", OpKind.Delete, Key, new Revision(Key, "rev-1"))]);
        Rules.Validate(deletion, deletion.Digest, unknown, new(0, [Item()]), Capabilities)
            .ShouldContain(e => e.Code == ErrorCode.Unavailable);
    }

    [Fact]
    public void ChangedAndWasPredicatesUseRealPreAndPostViewsAndAllowedValuesAreTyped()
    {
        var schema = Schema([
            new("gate", RuleAction.Required, [new(ConditionKind.Changed, "count"), new(ConditionKind.Was, "count", FieldValue.Integer(1))]),
            new("count", RuleAction.Allowed, [], [FieldValue.Integer(2)])]);
        var source = Item(fields: Fields(("phase", FieldValue.String("open")), ("count", FieldValue.Integer(1)), ("gate", FieldValue.Null)));
        var proposal = Batch(Fields(("count", FieldValue.Integer(2))), schema);
        Rules.Validate(proposal, proposal.Digest, schema, new(0, [source]), Capabilities)
            .ShouldContain(e => e.Message.Contains("Required field", StringComparison.Ordinal));
        proposal = Batch(Fields(("count", FieldValue.Integer(3)), ("gate", FieldValue.String("evidence"))), schema);
        Rules.Validate(proposal, proposal.Digest, schema, new(0, [source]), Capabilities)
            .ShouldContain(e => e.Message.Contains("allowed values", StringComparison.Ordinal));
        proposal = Batch(Fields(("count", FieldValue.Integer(2)), ("gate", FieldValue.String("evidence"))), schema);
        Rules.Validate(proposal, proposal.Digest, schema, new(0, [source]), Capabilities).ShouldBeEmpty();
    }

    [Fact]
    public void DigestSchemaStoreAndRevisionPreconditionsAreIndependent()
    {
        var schema = Schema();
        var proposal = Batch(Fields(("count", FieldValue.Integer(1))), schema);
        Rules.Validate(proposal, "not-the-digest", Schema(rules: []), new(1, [Item("moved")]), Capabilities)
            .Select(e => e.Code).ShouldContain(ErrorCode.DigestMismatch);
        var errors = Rules.Validate(proposal, proposal.Digest, Schema(rules: []), new(1, [Item("moved")]), Capabilities);
        errors.ShouldContain(e => e.Code == ErrorCode.SchemaChanged);
        errors.Count(e => e.Code == ErrorCode.Conflict).ShouldBe(2);
    }

    [Fact]
    public void EffectValidationUsesTheCurrentCasAfterImportWithoutResettingTheBoundBaseline()
    {
        var schema = Schema();
        var item = Item();
        var proposal = Batch(Fields(("count", FieldValue.Integer(1))), schema);
        var planned = JournalEntry.Planned(proposal.Digest, 0);
        var imported = new StoreSnapshot(1, [item], proposals: [proposal], journal: [planned]);
        Rules.Validate(proposal, proposal.Digest, schema, imported, Capabilities)
            .ShouldContain(e => e.Code == ErrorCode.Conflict);
        Rules.ValidateEffect(proposal, 0, schema, imported, Capabilities).ShouldBeEmpty();
        var attempted = new StoreSnapshot(2, [item], proposals: [proposal],
            journal: [planned.Begin(Guid.NewGuid()).Value]);
        Rules.ValidateEffect(proposal, 0, schema, attempted, Capabilities)
            .ShouldContain(e => e.Code == ErrorCode.Conflict);
    }

    [Fact]
    public void ObservedNonAtomicMustBeExplicitAndCannotWaiveAtomicGateRequirements()
    {
        var schema = Schema();
        var capabilities = new ProviderCapabilities([OpKind.Batch], [], [], false, false);
        var store = new StoreSnapshot(0, [Item()]);
        var proposal = Batch(Fields(("count", FieldValue.Integer(1))), schema);
        Rules.Validate(proposal, proposal.Digest, schema, store, capabilities)
            .ShouldContain(e => e.Code == ErrorCode.Unsupported);
        proposal = Batch(proposal.Operations[0].Fields, schema, concurrency: ConcurrencyMode.ObservedNonAtomic);
        Rules.Validate(proposal, proposal.Digest, schema, store, capabilities).ShouldBeEmpty();
        proposal = Batch(Fields(("phase", FieldValue.String("finished")), ("gate", FieldValue.String("evidence"))),
            schema, concurrency: ConcurrencyMode.ObservedNonAtomic);
        Rules.Validate(proposal, proposal.Digest, schema, store, capabilities)
            .ShouldContain(e => e.Message.Contains("atomically", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("base", "local", "base", "local", false)]
    [InlineData("base", "base", "remote", "remote", false)]
    [InlineData("base", "same", "same", "same", false)]
    [InlineData("base", "local", "remote", "remote", true)]
    public void ThreeWayMergeUsesStagedIntentNotTheCacheMirror(string baseline, string local, string remote, string expected, bool conflict)
    {
        var source = Item(fields: Fields(("gate", FieldValue.String(baseline))));
        var moved = Item("rev-2", Fields(("gate", FieldValue.String(remote)), ("count", FieldValue.Integer(3))));
        source.Revision.ShouldNotBe(moved.Revision);
        var draft = new Draft(Key, "record", source, Fields(("gate", FieldValue.String(local))));
        var result = Rules.Merge(draft, moved);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Fields["gate"].ShouldBe(FieldValue.String(expected));
        result.Value.Fields["count"].ShouldBe(FieldValue.Integer(3));
        result.Value.Conflicts.Contains("gate").ShouldBe(conflict);
    }

    [Fact]
    public void MergeDoesNotGuessUnobservedBaseOrRemoteAndKeepsExplicitClears()
    {
        var source = Item(fields: Fields(("gate", FieldValue.String("base"))));
        var draft = new Draft(Key, "record", source, Fields(("gate", FieldValue.Null)));
        var moved = Item("rev-2", Fields(("gate", FieldValue.String("base"))));
        Rules.Merge(draft, moved).Value.Fields["gate"].ShouldBe(FieldValue.Null);
        var unknown = Item("rev-2", FieldValues.Empty);
        Rules.Merge(draft, unknown).Value.Conflicts.ShouldBe(["gate"]);
        draft = new(Key, "record", Item(fields: FieldValues.Empty), Fields(("gate", FieldValue.String("new"))));
        Rules.Merge(draft, moved).Value.Conflicts.ShouldBe(["gate"]);
    }

    [Fact]
    public void ResultNeverExposesBothSuccessAndFailure()
    {
        var ok = Result<int>.Ok(0);
        ok.Value.ShouldBe(0);
        Should.Throw<InvalidOperationException>(() => ok.Error);
        var failed = Result<int>.Fail(ErrorCode.Indeterminate, "Unknown after dispatch.");
        failed.IsSuccess.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => failed.Value);
    }

    [Fact]
    public void CreationBindsTheRetainedSeedAndFencesItsUuidAcrossProposalDigests()
    {
        var schema = Schema();
        var key = ItemKey.Local(Guid.NewGuid());
        var fields = Fields(("phase", FieldValue.String("open")), ("gate", FieldValue.String("evidence")));
        var draft = new Draft(key, "record", null, fields);
        var create = new Op("seed", OpKind.Create, key, fields: fields, type: "record",
            concurrency: ConcurrencyMode.ObservedNonAtomic);
        var proposal = new Proposal(Connection, schema.Fingerprint, 0, [create]);
        Rules.Validate(proposal, proposal.Digest, schema, new(0, drafts: [draft]), Capabilities).ShouldBeEmpty();
        var edited = new Draft(key, "record", null, fields.Set("gate", FieldValue.String("changed")));
        Rules.Validate(proposal, proposal.Digest, schema, new(0, drafts: [edited]), Capabilities)
            .ShouldContain(e => e.Code == ErrorCode.Conflict);
        var attempting = JournalEntry.Planned(proposal.Digest, 0).Begin(Guid.NewGuid()).Value;
        var replacement = new Proposal(Connection, schema.Fingerprint, 1, [create]);
        replacement.Digest.ShouldNotBe(proposal.Digest);
        Rules.Validate(replacement, replacement.Digest, schema,
            new(1, drafts: [draft], proposals: [proposal], journal: [attempting]), Capabilities)
            .ShouldContain(e => e.Message.Contains("already been attempted", StringComparison.Ordinal));
    }
}
