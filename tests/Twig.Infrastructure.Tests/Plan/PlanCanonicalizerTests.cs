using System.Text.Json;
using Shouldly;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.Plan;

/// <summary>
/// Behavioural tests for <see cref="PlanCanonicalizer"/>. The canonical digest is the
/// single seam by which the plan file binds to its journal — determinism and the exact
/// equivalence rules (whitespace / property order collapse, array order preserves) are
/// therefore load-bearing.
/// </summary>
public sealed class PlanCanonicalizerTests
{
    [Fact]
    public void Whitespace_differences_produce_the_same_digest()
    {
        var pretty = /*lang=json,strict*/ """
        {
          "a": 1,
          "b": "hello",
          "c": [1, 2, 3]
        }
        """;
        var compact = "{\"a\":1,\"b\":\"hello\",\"c\":[1,2,3]}";

        var d1 = Digest(pretty);
        var d2 = Digest(compact);

        d1.ShouldBe(d2);
    }

    [Fact]
    public void Object_property_order_does_not_affect_the_digest()
    {
        var a = "{\"a\":1,\"b\":2,\"c\":3}";
        var b = "{\"c\":3,\"a\":1,\"b\":2}";

        Digest(a).ShouldBe(Digest(b));
    }

    [Fact]
    public void Nested_object_property_order_does_not_affect_the_digest()
    {
        var a = "{\"outer\":{\"a\":1,\"b\":2},\"list\":[{\"x\":1,\"y\":2}]}";
        var b = "{\"list\":[{\"y\":2,\"x\":1}],\"outer\":{\"b\":2,\"a\":1}}";

        Digest(a).ShouldBe(Digest(b));
    }

    [Fact]
    public void Array_order_changes_the_digest()
    {
        var a = "{\"xs\":[1,2,3]}";
        var b = "{\"xs\":[3,2,1]}";

        Digest(a).ShouldNotBe(Digest(b));
    }

    [Fact]
    public void Digest_is_deterministic_across_calls()
    {
        var json = "{\"a\":1,\"b\":[true,false,null,\"s\"]}";

        var first = Digest(json);
        var second = Digest(json);
        var third = Digest(json);

        first.ShouldBe(second);
        second.ShouldBe(third);
    }

    [Fact]
    public void Digest_is_lowercase_hex_and_thirty_two_bytes()
    {
        var digest = Digest("{\"a\":1}");

        digest.Length.ShouldBe(64);
        digest.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Canonical_form_sorts_property_names_ordinally()
    {
        using var doc = JsonDocument.Parse("{\"z\":1,\"a\":2,\"m\":3}");
        var (canonical, _) = PlanCanonicalizer.Canonicalize(doc.RootElement);

        canonical.ShouldBe("{\"a\":2,\"m\":3,\"z\":1}");
    }

    [Fact]
    public void Canonical_form_preserves_array_order()
    {
        using var doc = JsonDocument.Parse("{\"xs\":[3,1,2]}");
        var (canonical, _) = PlanCanonicalizer.Canonicalize(doc.RootElement);

        canonical.ShouldBe("{\"xs\":[3,1,2]}");
    }

    [Fact]
    public void Reordering_operations_in_a_plan_changes_the_digest()
    {
        // Sanity: the plan file's operations array is order-sensitive, and the digest
        // must reflect that. If two plans differ only in the order of their operations,
        // they are DIFFERENT plans and must have different digests.
        var planA = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            { "id": "1", "kind": "delete", "workItemId": 1, "expectedRevision": 1 },
            { "id": "2", "kind": "delete", "workItemId": 2, "expectedRevision": 1 }
          ]
        }
        """;
        var planB = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            { "id": "2", "kind": "delete", "workItemId": 2, "expectedRevision": 1 },
            { "id": "1", "kind": "delete", "workItemId": 1, "expectedRevision": 1 }
          ]
        }
        """;

        Digest(planA).ShouldNotBe(Digest(planB));
    }

    [Fact]
    public void CanonicalizeToString_matches_the_json_returned_by_Canonicalize()
    {
        using var doc = JsonDocument.Parse("{\"b\":2,\"a\":1}");

        var (canonical, _) = PlanCanonicalizer.Canonicalize(doc.RootElement);
        var only = PlanCanonicalizer.CanonicalizeToString(doc.RootElement);

        only.ShouldBe(canonical);
    }

    [Fact]
    public void ComputeDigest_over_the_canonical_bytes_matches_Canonicalize()
    {
        using var doc = JsonDocument.Parse("{\"a\":1,\"b\":\"x\"}");
        var (canonical, digest) = PlanCanonicalizer.Canonicalize(doc.RootElement);

        var bytes = System.Text.Encoding.UTF8.GetBytes(canonical);
        PlanCanonicalizer.ComputeDigest(bytes).ShouldBe(digest);
    }

    [Fact]
    public void Rejects_top_level_duplicate_property_directly()
    {
        // The canonicalizer is a public seam — a direct caller that bypasses the parser
        // must still be prevented from producing a digest over a document with duplicate
        // property names (JsonDocument silently keeps the last value).
        using var doc = JsonDocument.Parse("{\"a\":1,\"a\":2}");

        var ex = Should.Throw<InvalidOperationException>(() => PlanCanonicalizer.Canonicalize(doc.RootElement));
        ex.Message.ShouldContain("duplicate property 'a'");
    }

    [Fact]
    public void Rejects_nested_duplicate_property_directly()
    {
        // A duplicate deep inside must still trip the recursive walk — this is the case
        // that fields (batch operations) most naturally exposes.
        using var doc = JsonDocument.Parse(
            "{\"outer\":{\"fields\":{\"System.Title\":\"a\",\"System.Title\":\"b\"}}}");

        var ex = Should.Throw<InvalidOperationException>(() => PlanCanonicalizer.Canonicalize(doc.RootElement));
        ex.Message.ShouldContain("duplicate property 'System.Title'");
    }

    [Fact]
    public void Rejects_duplicate_property_inside_array_element_directly()
    {
        // The walk must recurse through arrays too — not only through property values.
        using var doc = JsonDocument.Parse(
            "{\"operations\":[{\"id\":\"x\",\"id\":\"y\"}]}");

        var ex = Should.Throw<InvalidOperationException>(() => PlanCanonicalizer.Canonicalize(doc.RootElement));
        ex.Message.ShouldContain("duplicate property 'id'");
    }

    private static string Digest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return PlanCanonicalizer.Canonicalize(doc.RootElement).Digest;
    }
}
