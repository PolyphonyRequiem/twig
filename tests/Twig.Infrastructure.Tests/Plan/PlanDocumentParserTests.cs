using System.Text.Json;
using Shouldly;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.Plan;

/// <summary>
/// Behavioural tests for <see cref="PlanDocumentParser"/>. Each test asserts one thing —
/// either that a specific vocabulary parses cleanly, or that a specific violation raises
/// exactly the issue code the shared contract requires.
/// </summary>
public sealed class PlanDocumentParserTests
{
    private static readonly PlanDocumentParser Parser = new();

    private const string ValidBatch = /*lang=json,strict*/ """
    {
      "version": 1,
      "workspace": { "organization": "acme", "project": "widgets" },
      "operations": [
        {
          "id": "op-1",
          "kind": "batch",
          "workItemId": 42,
          "expectedRevision": 3,
          "fields": { "System.Title": "New title", "System.AssignedTo": null }
        }
      ]
    }
    """;

    [Fact]
    public void Parses_a_valid_batch_operation()
    {
        var result = Parser.Parse(ValidBatch);

        result.IsValid.ShouldBeTrue(FormatIssues(result));
        result.Plan.ShouldNotBeNull();
        result.Plan!.Version.ShouldBe(1);
        result.Plan.Workspace.ShouldBe(new PlanWorkspace { Organization = "acme", Project = "widgets" });
        result.Plan.Operations.Count.ShouldBe(1);
        var op = result.Plan.Operations[0].ShouldBeOfType<BatchOperation>();
        op.Id.ShouldBe("op-1");
        op.Kind.ShouldBe(PlanOperationKind.Batch);
        op.WorkItemId.ShouldBe(42);
        op.ExpectedRevision.ShouldBe(3);
        op.Fields["System.Title"].ShouldBe("New title");
        op.Fields["System.AssignedTo"].ShouldBeNull();
        result.Digest.ShouldNotBeNullOrWhiteSpace();
        result.CanonicalJson.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("predecessor")]
    [InlineData("successor")]
    [InlineData("related")]
    public void Parses_an_add_link_for_every_valid_relation(string relation)
    {
        var json = $$"""
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            { "id": "L", "kind": "add-link", "workItemId": 1, "expectedRevision": 2, "relation": "{{relation}}", "otherId": 9 }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.IsValid.ShouldBeTrue(FormatIssues(result));
        var op = result.Plan!.Operations[0].ShouldBeOfType<AddLinkOperation>();
        op.Relation.ShouldBe(relation);
        op.OtherId.ShouldBe(9);
    }

    [Fact]
    public void Parses_a_remove_link_operation()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            { "id": "R", "kind": "remove-link", "workItemId": 1, "expectedRevision": 2, "relation": "related", "otherId": 9 }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.IsValid.ShouldBeTrue(FormatIssues(result));
        result.Plan!.Operations[0].ShouldBeOfType<RemoveLinkOperation>().Kind.ShouldBe(PlanOperationKind.RemoveLink);
    }

    [Fact]
    public void Parses_a_publish_seed_operation()
    {
        var identity = StagedIdentity.New();
        var json = $$"""
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            { "id": "P", "kind": "publish-seed", "stagedIdentity": "{{identity}}", "expectedFingerprint": "abc123" }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.IsValid.ShouldBeTrue(FormatIssues(result));
        var op = result.Plan!.Operations[0].ShouldBeOfType<PublishSeedOperation>();
        op.StagedIdentity.ShouldBe(identity);
        op.ExpectedFingerprint.ShouldBe("abc123");
    }

    [Fact]
    public void Parses_a_delete_operation()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            { "id": "D", "kind": "delete", "workItemId": 7, "expectedRevision": 12 }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.IsValid.ShouldBeTrue(FormatIssues(result));
        var op = result.Plan!.Operations[0].ShouldBeOfType<DeleteOperation>();
        op.WorkItemId.ShouldBe(7);
        op.ExpectedRevision.ShouldBe(12);
    }

    [Fact]
    public void Rejects_invalid_json()
    {
        var result = Parser.Parse("{ not json");

        result.IsValid.ShouldBeFalse();
        result.Issues.ShouldContain(i => i.Code == PlanValidationCodes.JsonInvalid && i.Path == "");
        result.Plan.ShouldBeNull();
        result.Digest.ShouldBeNull();
    }

    [Fact]
    public void Rejects_non_object_root()
    {
        var result = Parser.Parse("[1,2,3]");

        result.Issues.ShouldContain(i => i.Code == PlanValidationCodes.NotAnObject);
    }

    [Fact]
    public void Rejects_unsupported_version()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 2,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [ { "id": "x", "kind": "delete", "workItemId": 1, "expectedRevision": 1 } ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i => i.Code == PlanValidationCodes.UnsupportedVersion && i.Path == "/version");
    }

    [Fact]
    public void Rejects_unknown_top_level_property()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [ { "id": "x", "kind": "delete", "workItemId": 1, "expectedRevision": 1 } ],
          "note": "not allowed here"
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i => i.Code == PlanValidationCodes.UnknownProperty && i.Path == "/note");
    }

    [Fact]
    public void Rejects_unknown_operation_property_note_on_batch()
    {
        // Note is one of the four forbidden concerns the plan surface specifically excludes.
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            {
              "id": "x", "kind": "batch",
              "workItemId": 1, "expectedRevision": 1,
              "fields": { "System.Title": "t" },
              "note": "forbidden"
            }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i =>
            i.Code == PlanValidationCodes.UnknownProperty && i.Path == "/operations/0/note");
    }

    [Fact]
    public void Rejects_branch_property_on_publish_seed()
    {
        // Branch is another explicitly-forbidden concern.
        var identity = StagedIdentity.New();
        var json = $$"""
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            {
              "id": "P", "kind": "publish-seed",
              "stagedIdentity": "{{identity}}",
              "expectedFingerprint": "fp",
              "branch": "feature/x"
            }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i =>
            i.Code == PlanValidationCodes.UnknownProperty && i.Path == "/operations/0/branch");
    }

    [Fact]
    public void Rejects_artifact_property()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            {
              "id": "x", "kind": "batch",
              "workItemId": 1, "expectedRevision": 1,
              "fields": { "System.Title": "t" },
              "artifact": "arn:something"
            }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i =>
            i.Code == PlanValidationCodes.UnknownProperty && i.Path == "/operations/0/artifact");
    }

    [Fact]
    public void Rejects_unknown_kind()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            { "id": "x", "kind": "reparent", "workItemId": 1, "expectedRevision": 1 }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i =>
            i.Code == PlanValidationCodes.UnknownKind && i.Path == "/operations/0/kind");
    }

    [Fact]
    public void Rejects_duplicate_operation_id()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            { "id": "same", "kind": "delete", "workItemId": 1, "expectedRevision": 1 },
            { "id": "same", "kind": "delete", "workItemId": 2, "expectedRevision": 1 }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i =>
            i.Code == PlanValidationCodes.DuplicateOperationId && i.Path == "/operations/1/id");
    }

    [Fact]
    public void Rejects_empty_operations_array()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": []
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i => i.Code == PlanValidationCodes.EmptyOperations);
    }

    [Fact]
    public void Rejects_empty_batch_fields()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            { "id": "b", "kind": "batch", "workItemId": 1, "expectedRevision": 1, "fields": {} }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i => i.Code == PlanValidationCodes.EmptyFields);
    }

    [Fact]
    public void Rejects_invalid_relation()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            { "id": "L", "kind": "add-link", "workItemId": 1, "expectedRevision": 1, "relation": "child", "otherId": 2 }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i => i.Code == PlanValidationCodes.InvalidRelation);
    }

    [Fact]
    public void Rejects_invalid_staged_identity()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            { "id": "P", "kind": "publish-seed", "stagedIdentity": "not-a-guid", "expectedFingerprint": "fp" }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i => i.Code == PlanValidationCodes.InvalidStagedIdentity);
    }

    [Fact]
    public void Rejects_missing_workspace()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "operations": [ { "id": "x", "kind": "delete", "workItemId": 1, "expectedRevision": 1 } ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i =>
            i.Code == PlanValidationCodes.MissingProperty && i.Path == "/workspace");
    }

    [Fact]
    public void Rejects_wrong_type_for_workItemId()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            { "id": "x", "kind": "delete", "workItemId": "42", "expectedRevision": 1 }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i =>
            i.Code == PlanValidationCodes.WrongType && i.Path == "/operations/0/workItemId");
    }

    [Fact]
    public void Rejects_empty_string_for_organization()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "   ", "project": "p" },
          "operations": [ { "id": "x", "kind": "delete", "workItemId": 1, "expectedRevision": 1 } ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i =>
            i.Code == PlanValidationCodes.EmptyString && i.Path == "/workspace/organization");
    }

    [Fact]
    public void Rejects_duplicate_top_level_property()
    {
        // Two "version" keys on the root object — JsonDocument silently keeps the last
        // value, which would let two authorings collapse to the same digest.
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [ { "id": "x", "kind": "delete", "workItemId": 1, "expectedRevision": 1 } ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i =>
            i.Code == PlanValidationCodes.DuplicateProperty && i.Path == "/version");
        // Semantic reads must not run once duplicates are detected.
        result.Plan.ShouldBeNull();
        result.Digest.ShouldBeNull();
    }

    [Fact]
    public void Rejects_duplicate_nested_property_inside_workspace()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "organization": "other", "project": "p" },
          "operations": [ { "id": "x", "kind": "delete", "workItemId": 1, "expectedRevision": 1 } ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i =>
            i.Code == PlanValidationCodes.DuplicateProperty && i.Path == "/workspace/organization");
    }

    [Fact]
    public void Rejects_duplicate_property_inside_batch_fields()
    {
        // Fields is a nested object underneath an array element — verifies the scanner
        // walks recursively into every object, not just the top level.
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            {
              "id": "b", "kind": "batch",
              "workItemId": 1, "expectedRevision": 1,
              "fields": { "System.Title": "a", "System.Title": "b" }
            }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i =>
            i.Code == PlanValidationCodes.DuplicateProperty
            && i.Path == "/operations/0/fields/System.Title");
    }

    [Fact]
    public void Rejects_duplicate_publish_seed_stagedIdentity_even_with_different_op_ids()
    {
        // Different op ids, same stagedIdentity target — two publishes racing on one
        // staged seed is a plan the reconciler cannot honour. Distinct from
        // DuplicateOperationId, which fires on the op id itself.
        var identity = StagedIdentity.New();
        var json = $$"""
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            { "id": "P1", "kind": "publish-seed", "stagedIdentity": "{{identity}}", "expectedFingerprint": "fp1" },
            { "id": "P2", "kind": "publish-seed", "stagedIdentity": "{{identity}}", "expectedFingerprint": "fp2" }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i =>
            i.Code == PlanValidationCodes.DuplicateStagedIdentityTarget
            && i.Path == "/operations/1/stagedIdentity");
        result.Issues.ShouldNotContain(i => i.Code == PlanValidationCodes.DuplicateOperationId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_non_positive_workItemId_as_range_issue(int badId)
    {
        var json = $$"""
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            { "id": "x", "kind": "delete", "workItemId": {{badId}}, "expectedRevision": 1 }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i =>
            i.Code == PlanValidationCodes.IntegerOutOfRange
            && i.Path == "/operations/0/workItemId");
        // Zero/negative must not be misreported as a type error.
        result.Issues.ShouldNotContain(i =>
            i.Code == PlanValidationCodes.WrongType && i.Path == "/operations/0/workItemId");
    }

    [Fact]
    public void Rejects_non_positive_expectedRevision_as_range_issue()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            { "id": "x", "kind": "delete", "workItemId": 1, "expectedRevision": 0 }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i =>
            i.Code == PlanValidationCodes.IntegerOutOfRange
            && i.Path == "/operations/0/expectedRevision");
    }

    [Fact]
    public void Rejects_non_positive_otherId_on_add_link_as_range_issue()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            { "id": "L", "kind": "add-link", "workItemId": 1, "expectedRevision": 1, "relation": "related", "otherId": 0 }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.Issues.ShouldContain(i =>
            i.Code == PlanValidationCodes.IntegerOutOfRange
            && i.Path == "/operations/0/otherId");
    }

    [Fact]
    public void Accepts_positive_boundary_ids_and_revisions()
    {
        // Exact 1 is the smallest positive integer — must round-trip as a valid plan.
        var json = /*lang=json,strict*/ """
        {
          "version": 1,
          "workspace": { "organization": "o", "project": "p" },
          "operations": [
            { "id": "b", "kind": "batch", "workItemId": 1, "expectedRevision": 1, "fields": { "System.Title": "t" } },
            { "id": "L", "kind": "add-link", "workItemId": 2, "expectedRevision": 3, "relation": "related", "otherId": 4 }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.IsValid.ShouldBeTrue(FormatIssues(result));
        result.Plan.ShouldNotBeNull();
        result.Plan!.Operations.Count.ShouldBe(2);
    }

    [Fact]
    public void Rejects_version_zero_and_negative_with_unsupported_version_code()
    {
        // Version must be exactly 1; a well-formed integer that isn't 1 stays a
        // vocabulary problem (UnsupportedVersion), not a range problem.
        foreach (var bad in new[] { 0, -1, 2 })
        {
            var json = $$"""
            {
              "version": {{bad}},
              "workspace": { "organization": "o", "project": "p" },
              "operations": [ { "id": "x", "kind": "delete", "workItemId": 1, "expectedRevision": 1 } ]
            }
            """;

            var result = Parser.Parse(json);

            result.Issues.ShouldContain(i =>
                i.Code == PlanValidationCodes.UnsupportedVersion && i.Path == "/version",
                $"for version={bad}");
        }
    }

    private static string FormatIssues(PlanValidationResult r)
        => "expected valid; got: " + string.Join(", ", r.Issues.Select(i => $"[{i.Code} @ {i.Path}] {i.Message}"));
}
