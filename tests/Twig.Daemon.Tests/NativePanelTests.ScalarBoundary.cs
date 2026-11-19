using System.Net;
using System.Text.Json.Nodes;
using Shouldly;
using Twig.Core;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Daemon.Tests.Native;

public sealed class NativePanelScalarBoundaryTests
{
    [Fact]
    public Task NativePanelUnpinRetainsOrderedQueryAndOtherPinAndExactTypedStateColors()
        => NativePanelTests.ScalarBoundaryComposition();
}

internal static partial class NativePanelTests
{
    // native-process-browser.mjs, Canvas blob 768e815154034bddffec38a24f4f93cbffea8fa6, 242-282 and 316-318.
    internal static async Task ScalarBoundaryComposition()
    {
        await using var fixture = new ReadCompositionFixture(process: true);
        await fixture.StartAsync();
        var source = await fixture.BenchSource();
        var initialKeys = ItemKeys(source);
        initialKeys.Length.ShouldBe(3);
        var sourceId = source["sourceId"]!.GetValue<Guid>();
        var initial = (await fixture.Ok("GET", "/benches"))["benches"]!.AsArray().Single()!;
        initial["id"]!.GetValue<Guid>().ShouldBe(fixture.BenchId);
        var originalSelectors = initial["selectors"]!.GetValue<string>();
        var query = BenchCodec.DeserializeSelectors(originalSelectors).Single();
        query.Kind.ShouldBe(SelectorKind.Query, "This counterfactual starts from actual query membership.");
        query.Query.ShouldNotBeNull().Kind.ShouldBe(QueryKind.All);
        var rows = source["items"]!.AsArray();
        var firstPinWire = rows[0]!["pin"]!.GetValue<string>();
        var otherPinWire = rows[1]!["pin"]!.GetValue<string>();
        var firstPin = BenchCodec.DeserializeSelectors(firstPinWire).Single();
        var otherPin = BenchCodec.DeserializeSelectors(otherPinWire).Single();
        firstPin.Kind.ShouldBe(SelectorKind.Pin);
        otherPin.Kind.ShouldBe(SelectorKind.Pin);
        firstPin.Item.ShouldNotBe(otherPin.Item);
        firstPin.Connection.ShouldBe(query.Connection);
        otherPin.Connection.ShouldBe(query.Connection);
        firstPin.Item.ShouldNotBeNull().NativeId.ShouldBe(rows[0]!["locator"]!.GetValue<string>());
        otherPin.Item.ShouldNotBeNull().NativeId.ShouldBe(rows[1]!["locator"]!.GetValue<string>());
        var augmented = BenchCodec.SerializeSelectors([query, firstPin, otherPin]);
        var revision = initial["revision"]!.GetValue<long>();
        var name = initial["name"]!.GetValue<string>();
        JsonObject Definition(long expected, string selectors) => new()
        {
            ["name"] = name, ["expectedRevision"] = expected, ["selectors"] = selectors,
        };
        async Task<JsonNode> ReadDefinition() => (await fixture.Ok("GET", "/benches"))["benches"]!.AsArray().Single()!;
        async Task AssertStored(string expected)
        {
            var actual = await ReadDefinition();
            actual["selectors"]!.GetValue<string>().ShouldBe(expected);
            var stored = Required(await fixture.Store.ReadAsync(fixture.Token)).Benches.Single();
            stored.Id.ShouldBe(fixture.BenchId);
            stored.Name.ShouldBe(name);
            BenchCodec.SerializeSelectors(stored.Selectors).ShouldBe(expected,
                "The HTTP definition and canonical native SQLite store must agree in exact selector order.");
            stored.Revision.ShouldBe(actual["revision"]!.GetValue<long>());
        }
        await fixture.Ok("PUT", $"/benches/{fixture.BenchId:D}", Definition(revision, augmented));
        await AssertStored(augmented);
        var pinned = await ReadDefinition();
        var currentRevision = pinned["revision"]!.GetValue<long>();
        currentRevision.ShouldBeGreaterThan(revision);

        var stale = await fixture.Send("PUT", $"/benches/{fixture.BenchId:D}", Definition(revision, originalSelectors));
        stale.Status.ShouldBe(HttpStatusCode.Conflict,
            "Unlike the browser's intercepted 409, this is a genuine native stale-revision PUT.");
        await AssertStored(augmented);
        (await ReadDefinition())["revision"]!.GetValue<long>().ShouldBe(currentRevision);

        var remaining = BenchCodec.SerializeSelectors([query, otherPin]);
        await fixture.Ok("PUT", $"/benches/{fixture.BenchId:D}", Definition(currentRevision, remaining));
        await AssertStored(remaining);
        var unpinned = await fixture.BenchSource();
        unpinned["sourceId"]!.GetValue<Guid>().ShouldBe(sourceId);
        ItemKeys(unpinned).ShouldBe(initialKeys,
            "Removing only the direct pin cannot exclude any of the three query members.");
        unpinned["items"]!.AsArray().Count.ShouldBe(3);
        var after = await ReadDefinition();
        BenchCodec.DeserializeSelectors(after["selectors"]!.GetValue<string>()).ShouldBe([query, otherPin],
            "Unpin must preserve precisely the original query followed by the other item's direct pin.");
        await fixture.Ok("PUT", $"/benches/{fixture.BenchId:D}",
            Definition(after["revision"]!.GetValue<long>(), originalSelectors));
        await AssertStored(originalSelectors);
        ItemKeys(await fixture.BenchSource()).ShouldBe(initialKeys);

        var native = Required(await fixture.Gateway.ReadAsync(fixture.Root, null, true, fixture.Token));
        native.Schema.ShouldNotBeNull();
        native.Schema.IsSuccess.ShouldBeTrue();
        var expectedTypes = native.Schema.Value.Types;
        expectedTypes.Length.ShouldBe(2);
        var expected = expectedTypes.ToDictionary(type => type.Reference, type => type.States.Single(), StringComparer.Ordinal);
        expected.Values.Select(state => state.Name).Distinct(StringComparer.Ordinal).Count().ShouldBe(1,
            "The genuine compact gateway intentionally gives two custom types the same state name.");
        expected.Values.Select(state => state.Color).Distinct(StringComparer.Ordinal).Count().ShouldBe(2,
            "Distinct colors are a fixture precondition, not a rule that all native types must obey.");
        var wireTypes = source["types"]!.AsArray();
        wireTypes.Count.ShouldBe(expected.Count);
        foreach (var type in wireTypes)
        {
            var reference = type!["reference"]!.GetValue<string>();
            expected.ContainsKey(reference).ShouldBeTrue();
            var state = type["states"]!.AsArray().Single()!;
            state["name"]!.GetValue<string>().ShouldBe(expected[reference].Name);
            state["color"]!.GetValue<string>().ShouldBe(expected[reference].Color,
                "Type-qualified native color must survive the adapter and actual source DTO without name-only collapse.");
        }
    }
}
