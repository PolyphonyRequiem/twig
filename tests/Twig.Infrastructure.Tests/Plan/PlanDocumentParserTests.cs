using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const string LocalId = "018f3f5a-6b7c-7d8e-9f01-234567890abc";

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

    private const string ValidProviderBatch = /*lang=json,strict*/ """
    {
      "version": 2,
      "workspace": { "organization": "acme", "project": "widgets" },
      "operations": [
        {
          "id": "op-v2",
          "kind": "batch",
          "target": {
            "item": {
              "localId": "018f3f5a-6b7c-7d8e-9f01-234567890abc",
              "externalIdentity": {
                "providerKind": "github",
                "resourceScope": "https://github.com/acme/widgets",
                "nativeId": "I_kwDOopaque"
              }
            },
            "expectedProviderRevision": "opaque:rev/42"
          },
          "fields": { "System.Title": "New title" }
        }
      ]
    }
    """;

    private const string ValidProviderLink = /*lang=json,strict*/ """
    {
      "version": 2,
      "workspace": { "organization": "acme", "project": "widgets" },
      "operations": [
        {
          "id": "link",
          "kind": "add-link",
          "target": {
            "item": {
              "localId": "018f3f5a-6b7c-7d8e-9f01-234567890abc",
              "externalIdentity": {
                "providerKind": "github",
                "resourceScope": "https://github.com/acme/widgets",
                "nativeId": "issue:7"
              }
            },
            "expectedProviderRevision": "opaque:rev/42"
          },
          "relation": "related",
          "other": {
            "localId": "0190f1f0-7b12-7abc-8abc-1234567890ab",
            "externalIdentity": {
              "providerKind": "github",
              "resourceScope": "https://github.com/acme/widgets",
              "nativeId": "issue:8"
            }
          }
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

    [Fact]
    public void Parses_v2_provider_batch_and_preserves_opaque_binding()
    {
        var result = Parser.Parse(ValidProviderBatch);

        result.IsValid.ShouldBeTrue(FormatIssues(result));
        result.Plan!.Version.ShouldBe(2);
        var op = result.Plan.Operations.Single().ShouldBeOfType<ProviderMutationOperation>();
        op.Kind.ShouldBe(PlanOperationKind.Batch);
        op.Target.Item.LocalId.ToString().ShouldBe(LocalId);
        op.Target.Item.ExternalIdentity.ShouldBe(new ExternalWorkItemIdentity(
            "github", "https://github.com/acme/widgets", "I_kwDOopaque"));
        op.Target.ExpectedProviderRevision.Value.ShouldBe("opaque:rev/42");
        op.Fields!["System.Title"].ShouldBe("New title");

        var written = PlanDocumentWriter.Write(result.Plan);
        Parser.Parse(written).Digest.ShouldBe(result.Digest);
    }

    [Fact]
    public void Parses_v2_link_with_provider_bound_source_and_peer()
    {
        var json = $$"""
        {
          "version": 2,
          "workspace": { "organization": "acme", "project": "widgets" },
          "operations": [
            {
              "id": "link",
              "kind": "add-link",
              "target": {
                "item": {
                  "localId": "{{LocalId}}",
                  "externalIdentity": { "providerKind": "github", "resourceScope": "repo:1", "nativeId": "issue:7" }
                },
                "expectedProviderRevision": "etag:\"abc\""
              },
              "relation": "related",
              "other": {
                "localId": "0190f1f0-7b12-7abc-8abc-1234567890ab",
                "externalIdentity": { "providerKind": "github", "resourceScope": "repo:1", "nativeId": "issue:8" }
              }
            }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.IsValid.ShouldBeTrue(FormatIssues(result));
        var op = result.Plan!.Operations.Single().ShouldBeOfType<ProviderMutationOperation>();
        op.OperationKind.ShouldBe(PlanOperationKind.AddLink);
        op.Target.Item.ExternalIdentity.NativeId.ShouldBe("issue:7");
        op.Other!.LocalId.ToString().ShouldBe("0190f1f0-7b12-7abc-8abc-1234567890ab");
        op.Target.ExpectedProviderRevision.Value.ShouldBe("etag:\"abc\"");
        Parser.Parse(PlanDocumentWriter.Write(result.Plan!)).Digest.ShouldBe(result.Digest);
    }

    [Fact]
    public void Parses_and_round_trips_v2_remove_link_with_opaque_binding()
    {
        var json = ValidProviderLink.Replace("\"add-link\"", "\"remove-link\"", StringComparison.Ordinal);

        var result = Parser.Parse(json);

        result.IsValid.ShouldBeTrue(FormatIssues(result));
        var operation = result.Plan!.Operations.Single().ShouldBeOfType<ProviderMutationOperation>();
        operation.Kind.ShouldBe(PlanOperationKind.RemoveLink);
        operation.Target.Item.LocalId.ToString().ShouldBe(LocalId);
        operation.Target.Item.ExternalIdentity.ShouldBe(new ExternalWorkItemIdentity(
            "github", "https://github.com/acme/widgets", "issue:7"));
        operation.Target.ExpectedProviderRevision.Value.ShouldBe("opaque:rev/42");
        operation.Relation.ShouldBe("related");
        operation.Other!.LocalId.ToString().ShouldBe("0190f1f0-7b12-7abc-8abc-1234567890ab");
        operation.Other.ExternalIdentity.ShouldBe(new ExternalWorkItemIdentity(
            "github", "https://github.com/acme/widgets", "issue:8"));
        operation.Fields.ShouldBeNull();

        var written = PlanDocumentWriter.Write(result.Plan);
        var roundTripped = Parser.Parse(written);
        roundTripped.IsValid.ShouldBeTrue(FormatIssues(roundTripped));
        roundTripped.Digest.ShouldBe(result.Digest);
        var writtenOperation = roundTripped.Plan!.Operations.Single().ShouldBeOfType<ProviderMutationOperation>();
        writtenOperation.Kind.ShouldBe(PlanOperationKind.RemoveLink);
        writtenOperation.Target.Item.ExternalIdentity.NativeId.ShouldBe("issue:7");
        writtenOperation.Target.ExpectedProviderRevision.Value.ShouldBe("opaque:rev/42");
        writtenOperation.Other!.ExternalIdentity.NativeId.ShouldBe("issue:8");
    }

    [Fact]
    public void Parses_and_round_trips_v2_delete_with_opaque_binding()
    {
        var json = $$"""
        {
          "version": 2,
          "workspace": { "organization": "acme", "project": "widgets" },
          "operations": [
            {
              "id": "delete",
              "kind": "delete",
              "target": {
                "item": {
                  "localId": "{{LocalId}}",
                  "externalIdentity": {
                    "providerKind": "github",
                    "resourceScope": "https://github.com/acme/widgets",
                    "nativeId": "issue:7"
                  }
                },
                "expectedProviderRevision": "etag:\"delete-revision\""
              }
            }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.IsValid.ShouldBeTrue(FormatIssues(result));
        var operation = result.Plan!.Operations.Single().ShouldBeOfType<ProviderMutationOperation>();
        operation.Kind.ShouldBe(PlanOperationKind.Delete);
        operation.Target.Item.LocalId.ToString().ShouldBe(LocalId);
        operation.Target.Item.ExternalIdentity.ShouldBe(new ExternalWorkItemIdentity(
            "github", "https://github.com/acme/widgets", "issue:7"));
        operation.Target.ExpectedProviderRevision.Value.ShouldBe("etag:\"delete-revision\"");
        operation.Other.ShouldBeNull();
        operation.Relation.ShouldBeNull();
        operation.Fields.ShouldBeNull();

        var written = PlanDocumentWriter.Write(result.Plan);
        var roundTripped = Parser.Parse(written);
        roundTripped.IsValid.ShouldBeTrue(FormatIssues(roundTripped));
        roundTripped.Digest.ShouldBe(result.Digest);
        var writtenOperation = roundTripped.Plan!.Operations.Single().ShouldBeOfType<ProviderMutationOperation>();
        writtenOperation.Kind.ShouldBe(PlanOperationKind.Delete);
        writtenOperation.Target.Item.ExternalIdentity.NativeId.ShouldBe("issue:7");
        writtenOperation.Target.ExpectedProviderRevision.Value.ShouldBe("etag:\"delete-revision\"");
        writtenOperation.Other.ShouldBeNull();
        writtenOperation.Relation.ShouldBeNull();
        writtenOperation.Fields.ShouldBeNull();
    }

    [Fact]
    public void V2_digest_changes_when_any_provider_binding_component_changes()
    {
        var original = Parser.Parse(ValidProviderLink);
        original.IsValid.ShouldBeTrue(FormatIssues(original));
        var originalDigest = original.Digest;

        var mutations = new (string Name, Action<JsonObject> Apply)[]
        {
            ("target localId", root =>
                root["operations"]![0]!["target"]!["item"]!["localId"] = "0190f1f0-7b12-7abc-8abc-1234567890ac"),
            ("target providerKind", root =>
                root["operations"]![0]!["target"]!["item"]!["externalIdentity"]!["providerKind"] = "github-enterprise"),
            ("target resourceScope", root =>
                root["operations"]![0]!["target"]!["item"]!["externalIdentity"]!["resourceScope"] = "https://github.com/acme/other"),
            ("target nativeId", root =>
                root["operations"]![0]!["target"]!["item"]!["externalIdentity"]!["nativeId"] = "issue:70"),
            ("expected provider revision", root =>
                root["operations"]![0]!["target"]!["expectedProviderRevision"] = "opaque:rev/43"),
            ("peer localId", root =>
                root["operations"]![0]!["other"]!["localId"] = "0190f1f0-7b12-7abc-8abc-1234567890ac"),
            ("peer providerKind", root =>
                root["operations"]![0]!["other"]!["externalIdentity"]!["providerKind"] = "github-enterprise"),
            ("peer resourceScope", root =>
                root["operations"]![0]!["other"]!["externalIdentity"]!["resourceScope"] = "https://github.com/acme/other"),
            ("peer nativeId", root =>
                root["operations"]![0]!["other"]!["externalIdentity"]!["nativeId"] = "issue:80"),
        };

        foreach (var (name, apply) in mutations)
        {
            var changedDocument = JsonNode.Parse(ValidProviderLink)!.AsObject();
            apply(changedDocument);

            var changed = Parser.Parse(changedDocument.ToJsonString());

            changed.IsValid.ShouldBeTrue($"{name}: {FormatIssues(changed)}");
            changed.Digest.ShouldNotBe(originalDigest, $"{name} must participate in the v2 digest");
        }
    }

    [Fact]
    public void V2_digest_is_stable_across_nested_property_order_and_whitespace()
    {
        const string reordered = """
            { "operations" : [ {
              "other" : { "externalIdentity" : { "nativeId" : "issue:8", "resourceScope" : "https://github.com/acme/widgets", "providerKind" : "github" }, "localId" : "0190f1f0-7b12-7abc-8abc-1234567890ab" },
              "relation" : "related",
              "target" : { "expectedProviderRevision" : "opaque:rev/42", "item" : { "externalIdentity" : { "nativeId" : "issue:7", "resourceScope" : "https://github.com/acme/widgets", "providerKind" : "github" }, "localId" : "018f3f5a-6b7c-7d8e-9f01-234567890abc" } },
              "kind" : "add-link", "id" : "link"
            } ], "workspace" : { "project" : "widgets", "organization" : "acme" }, "version" : 2 }
            """;

        var original = Parser.Parse(ValidProviderLink);
        var equivalent = Parser.Parse(reordered);

        equivalent.IsValid.ShouldBeTrue(FormatIssues(equivalent));
        equivalent.Digest.ShouldBe(original.Digest);
    }

    [Fact]
    public void V2_rejects_integer_aliases_instead_of_treating_them_as_authority()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 2,
          "workspace": { "organization": "acme", "project": "widgets" },
          "operations": [
            { "id": "legacy", "kind": "delete", "workItemId": 42, "expectedRevision": 7 }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.IsValid.ShouldBeFalse();
        result.Plan.ShouldBeNull();
        result.Issues.ShouldContain(i =>
            i.Code == PlanValidationCodes.UnknownProperty && i.Path == "/operations/0/workItemId");
        result.Issues.ShouldContain(i =>
            i.Code == PlanValidationCodes.MissingProperty && i.Path == "/operations/0/target");
    }

    [Theory]
    [InlineData("018F3F5A-6B7C-7D8E-9F01-234567890ABC", "repo:1", "issue:7")]
    [InlineData("00000000-0000-0000-0000-000000000000", "repo:1", "issue:7")]
    [InlineData("018f3f5a-6b7c-7d8e-9f01-234567890abc", " repo:1", "issue:7")]
    public void Rejects_noncanonical_or_invalid_v2_identity_components(string localId, string scope, string nativeId)
    {
        var json = $$"""
        {
          "version": 2,
          "workspace": { "organization": "acme", "project": "widgets" },
          "operations": [
            {
              "id": "delete",
              "kind": "delete",
              "target": {
                "item": {
                  "localId": "{{localId}}",
                  "externalIdentity": { "providerKind": "github", "resourceScope": "{{scope}}", "nativeId": "{{nativeId}}" }
                },
                "expectedProviderRevision": "opaque"
              }
            }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.IsValid.ShouldBeFalse();
        result.Issues.ShouldContain(i => i.Code == PlanValidationCodes.InvalidProviderIdentity);
    }

    [Fact]
    public void V2_does_not_accept_v1_seed_publish_operations()
    {
        var json = /*lang=json,strict*/ """
        {
          "version": 2,
          "workspace": { "organization": "acme", "project": "widgets" },
          "operations": [
            { "id": "seed", "kind": "publish-seed", "stagedIdentity": "018f3f5a-6b7c-7d8e-9f01-234567890abc", "expectedFingerprint": "fp" }
          ]
        }
        """;

        var result = Parser.Parse(json);

        result.IsValid.ShouldBeFalse();
        result.Issues.ShouldContain(i =>
            i.Code == PlanValidationCodes.UnsupportedOperationVersion && i.Path == "/operations/0/kind");
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
          "version": 3,
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
        // Versions 1 and 2 are recognized; other well-formed integers stay a
        // vocabulary problem (UnsupportedVersion), not a range problem.
        foreach (var bad in new[] { 0, -1, 3 })
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

    [Fact]
    public void V1_canonical_bytes_and_digest_remain_pinned()
    {
        var result = Parser.Parse(ValidBatch);

        result.CanonicalJson.ShouldBe(
            "{\"operations\":[{\"expectedRevision\":3,\"fields\":{\"System.AssignedTo\":null,\"System.Title\":\"New title\"},\"id\":\"op-1\",\"kind\":\"batch\",\"workItemId\":42}],\"version\":1,\"workspace\":{\"organization\":\"acme\",\"project\":\"widgets\"}}");
        result.Digest.ShouldBe("ccb91d0ca6add7f9fd5808f4921f7958f0a9b8c3bd7ca14586d03137acb56d0b");
        PlanDocumentWriter.Write(result.Plan!).ShouldBe(
            "{\"version\":1,\"workspace\":{\"organization\":\"acme\",\"project\":\"widgets\"},\"operations\":[{\"id\":\"op-1\",\"kind\":\"batch\",\"workItemId\":42,\"expectedRevision\":3,\"fields\":{\"System.Title\":\"New title\",\"System.AssignedTo\":null}}]}");
    }

    [Theory]
    [InlineData("observed-non-atomic")]
    [InlineData("exact-revision")]
    public void V2_link_concurrency_is_explicit_reviewable_and_digest_bound(string concurrency)
    {
        var json = ValidProviderLink.Replace("\"relation\": \"related\"",
            $"\"relation\": \"parent\", \"concurrency\": \"{concurrency}\"", StringComparison.Ordinal);
        var parsed = Parser.Parse(json);
        parsed.IsValid.ShouldBeTrue(FormatIssues(parsed));
        parsed.Plan!.Operations.Single().ShouldBeOfType<ProviderMutationOperation>().Concurrency.ShouldBe(concurrency);
        var written = Parser.Parse(PlanDocumentWriter.Write(parsed.Plan));
        written.Plan!.Operations.Single().ShouldBeOfType<ProviderMutationOperation>().Concurrency.ShouldBe(concurrency);
        if (concurrency == "observed-non-atomic")
            parsed.Digest.ShouldNotBe(Parser.Parse(json.Replace("observed-non-atomic", "exact-revision", StringComparison.Ordinal)).Digest);
    }

    [Theory]
    [InlineData("best-effort")]
    [InlineData("Observed-Non-Atomic")]
    [InlineData("")]
    public void V2_link_refuses_unknown_concurrency(string concurrency)
    {
        var json = ValidProviderLink.Replace("\"relation\": \"related\"",
            $"\"relation\": \"parent\", \"concurrency\": \"{concurrency}\"", StringComparison.Ordinal);
        Parser.Parse(json).IsValid.ShouldBeFalse();
    }

    private static string FormatIssues(PlanValidationResult r)
        => "expected valid; got: " + string.Join(", ", r.Issues.Select(i => $"[{i.Code} @ {i.Path}] {i.Message}"));
}
