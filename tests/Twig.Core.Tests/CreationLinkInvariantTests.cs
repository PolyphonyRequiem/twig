using System.Text.Json;
using Shouldly;
using Twig.Core;
using Xunit;
using Rules = Twig.Core.Core;

namespace Twig.Core.Tests;

public sealed class CreationLinkInvariantTests
{
    private static readonly Connection Connection = new(new("test"), "scope", "destination", "actor");
    private static readonly ItemKey Seed = ItemKey.Local(Guid.Parse("01900000-0000-7000-8000-000000000001"));
    private static readonly ItemKey Target = ItemKey.Remote(Connection.Provider, Connection.ResourceScope, "1");
    private static readonly FieldValues Fields = new(
        [KeyValuePair.Create("phase", FieldValue.String("open"))]);
    private static readonly ProcessSchema Schema = new(Connection,
        [new("record", "phase", [new("phase", FieldKind.Text)],
            [new("open", StateCategory.InProgress), new("closed", StateCategory.Completed)],
            [new("open", "closed")], [])],
        [new("dependency", RelationShape.Dependency, true),
            new("related", RelationShape.Association, false),
            new("hierarchy", RelationShape.Hierarchy, true),
            new("resource", RelationShape.Resource, false)]);

    private static ProviderCapabilities Capabilities(bool atomic = true, IEnumerable<string>? native = null)
        => new(Enum.GetValues<OpKind>(), native ?? ["dependency", "related", "hierarchy"], [], true, atomic);

    private static WorkItem Item(ItemKey key, bool complete = true)
        => new(key, "record", Fields, new Revision(key, "rev-1"), complete);

    private static Op Create(IEnumerable<CreationLink>? links = null)
        => new("create", OpKind.Create, Seed, fields: Fields, type: "record",
            concurrency: ConcurrencyMode.ObservedNonAtomic, links: links);

    private static Plan Plan(Op operation)
        => new(Connection, Schema.Fingerprint, 0, [operation]);

    private static StoreSnapshot Store(IEnumerable<CreationLink>? links = null, IEnumerable<WorkItem>? items = null)
        => new(0, items ?? [Item(Target)], drafts: [new(Seed, "record", null, Fields, links: links)]);

    [Fact]
    public void CreationLinkRequiresAnEndpointAndCanonicalRelation()
    {
        Should.Throw<ArgumentNullException>(() => new CreationLink(null!, "dependency", true));
        Should.Throw<ArgumentException>(() => new CreationLink(Target, "", true));
        Should.Throw<ArgumentException>(() => new CreationLink(Target, " dependency ", true));
    }

    [Fact]
    public void CapturedLinksFreezeCallerCollectionsAndBindExactOrderedIntent()
    {
        var links = new List<CreationLink> { new(Target, "dependency", true) };
        var operation = Create(links);
        var store = Store(links);
        links.Add(new(ItemKey.Remote(Connection.Provider, Connection.ResourceScope, "2"), "dependency", true));
        operation.Links.Length.ShouldBe(1);
        store.Drafts[0].Links.Length.ShouldBe(1);
        var plan = Plan(operation);
        using var json = JsonDocument.Parse(Rules.PlanBytes(plan));
        json.RootElement.GetProperty("operations")[0].GetProperty("links").GetArrayLength().ShouldBe(1);
        plan.Digest.ShouldNotBe(Plan(Create([new(Target, "dependency", false)])).Digest);
        plan.Digest.ShouldNotBe(Plan(Create([new(Target, "related", true)])).Digest);
        plan.Digest.ShouldNotBe(Plan(Create(links)).Digest);
        using var empty = JsonDocument.Parse(Rules.PlanBytes(Plan(Create())));
        empty.RootElement.GetProperty("operations")[0].TryGetProperty("links", out _).ShouldBeFalse();
    }

    [Fact]
    public void CreationMustMatchItsRetainedLinkCapture()
    {
        CreationLink[] captured = [new(Target, "dependency", true)];
        var store = Store(captured);
        var original = Plan(Create(captured));
        Rules.Validate(original, original.Digest, Schema, store, Capabilities()).ShouldBeEmpty();

        var changed = Plan(Create([new(Target, "dependency", false)]));
        Rules.Validate(changed, changed.Digest, Schema, store, Capabilities())
            .ShouldContain(error => error.Code == ErrorCode.Conflict);
        var omitted = Plan(Create());
        Rules.Validate(omitted, omitted.Digest, Schema, store, Capabilities())
            .ShouldContain(error => error.Code == ErrorCode.Conflict);
    }

    [Fact]
    public void CreationLinksRequireCompleteEndpointObservations()
    {
        CreationLink[] links = [new(Target, "dependency", true)];
        var plan = Plan(Create(links));
        Rules.Validate(plan, plan.Digest, Schema, Store(links), Capabilities()).ShouldBeEmpty();
        Rules.Validate(plan, plan.Digest, Schema, Store(links, [Item(Target, false)]), Capabilities())
            .ShouldContain(error => error.Code == ErrorCode.Unavailable);
        Rules.Validate(plan, plan.Digest, Schema, Store(links, []), Capabilities())
            .ShouldContain(error => error.Code == ErrorCode.Unavailable);
    }

    [Fact]
    public void CreationLinksRequireDeclaredNativeAtomicSupport()
    {
        CreationLink[] links = [new(Target, "dependency", true)];
        var plan = Plan(Create(links));
        var store = Store(links);
        Rules.Validate(plan, plan.Digest, Schema, store, Capabilities()).ShouldBeEmpty();
        Rules.Validate(plan, plan.Digest, Schema, store, Capabilities(atomic: false))
            .ShouldContain(error => error.Code == ErrorCode.Unsupported);
        Rules.Validate(plan, plan.Digest, Schema, store, Capabilities(native: []))
            .ShouldContain(error => error.Code == ErrorCode.Unsupported);
    }

    [Fact]
    public void CreationLinksCannotAdoptAnotherProviderScope()
    {
        var foreign = ItemKey.Remote(Connection.Provider, "other-scope", "1");
        CreationLink[] links = [new(foreign, "dependency", true)];
        var plan = Plan(Create(links));
        Rules.Validate(plan, plan.Digest, Schema, Store(links, [Item(foreign)]), Capabilities())
            .ShouldContain(error => error.Code == ErrorCode.Unsupported);
    }

    [Fact]
    public void CompoundLinksCannotAppearOnANonBatchExistingItemMutation()
    {
        var other = ItemKey.Remote(Connection.Provider, Connection.ResourceScope, "2");
        var source = Item(Target);
        var operation = new Op("comment", OpKind.Comment, Target, source.Revision,
            body: FieldValue.String("note"), links: [new(other, "dependency", true)]);
        var plan = Plan(operation);
        Rules.Validate(plan, plan.Digest, Schema, new(0, [source, Item(other)]), Capabilities())
            .ShouldContain(error => error.Code == ErrorCode.InvalidInput);
    }

    [Fact]
    public void UnpublishedEndpointMustBeAnEarlierRetainedCreation()
    {
        var parent = ItemKey.Local(Guid.Parse("01900000-0000-7000-8000-000000000002"));
        CreationLink[] links = [new(parent, "dependency", true)];
        var parentDraft = new Draft(parent, "record", null, Fields);
        var childDraft = new Draft(Seed, "record", null, Fields, links: links);
        var store = new StoreSnapshot(0, drafts: [parentDraft, childDraft]);
        var parentCreate = new Op("parent", OpKind.Create, parent, fields: Fields, type: "record",
            concurrency: ConcurrencyMode.ObservedNonAtomic);
        var ordered = new Plan(Connection, Schema.Fingerprint, 0, [parentCreate, Create(links)]);
        Rules.Validate(ordered, ordered.Digest, Schema, store, Capabilities()).ShouldBeEmpty();

        var reversed = new Plan(Connection, Schema.Fingerprint, 0, [Create(links), parentCreate]);
        Rules.Validate(reversed, reversed.Digest, Schema, store, Capabilities())
            .ShouldContain(error => error.Code == ErrorCode.Unsupported);
    }
}
