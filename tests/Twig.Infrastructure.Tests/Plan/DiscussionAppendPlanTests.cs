using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.Plan;

public sealed class DiscussionAppendPlanTests
{
    [Theory]
    [InlineData("plain", "Hello\nworld", "<p>Hello<br />\nworld</p>\n")]
    [InlineData("plain", "Hello\r\nworld", "<p>Hello<br />\nworld</p>\n")]
    [InlineData("markdown", "**Hello**", "<p><strong>Hello</strong></p>\n")]
    public void V1_append_round_trips_exact_reviewed_body(string format, string text, string body)
    {
        var json = $$"""
        {
          "version": 1,
          "workspace": { "organization": "acme", "project": "widgets" },
          "operations": [{
            "id": "comment-1", "kind": "append-comment", "workItemId": 42,
            "savedBenchId": "7", "expectedBenchContentsDigest": "{{new string('a', 64)}}",
            "expectedIdentityId": "identity-1", "format": "{{format}}",
            "text": {{System.Text.Json.JsonSerializer.Serialize(text)}},
            "effectiveBody": {{System.Text.Json.JsonSerializer.Serialize(body)}}
          }]
        }
        """;

        var parsed = new PlanDocumentParser().Parse(json);

        parsed.IsValid.ShouldBeTrue(string.Join(Environment.NewLine, parsed.Issues.Select(issue => issue.Message)));
        var operation = parsed.Plan!.Operations.Single().ShouldBeOfType<AppendCommentOperation>();
        operation.Text.ShouldBe(text);
        operation.EffectiveBody.ShouldBe(body);
        var written = PlanDocumentWriter.Write(parsed.Plan);
        new PlanDocumentParser().Parse(written).Digest.ShouldBe(parsed.Digest);
    }

    [Fact]
    public void Formatter_escapes_plain_text_and_disables_raw_html()
    {
        DiscussionPostBodyFormatter.Format("plain", "<script>alert(1)</script>")
            .ShouldBe("<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>\n");
        DiscussionPostBodyFormatter.Format("plain", "A\r\n<&\nB")
            .ShouldBe("<p>A<br />\n&lt;&amp;<br />\nB</p>\n");
        DiscussionPostBodyFormatter.Format("markdown", "<script>alert(1)</script>")
            .ShouldNotContain("<script>");
    }

    [Fact]
    public void Formatter_rejects_unsafe_markdown_urls_and_both_oversize_boundaries()
    {
        Should.Throw<ArgumentException>(() => DiscussionPostBodyFormatter.Format("markdown", "[click](javascript:alert(1))"));
        Should.Throw<ArgumentException>(() => DiscussionPostBodyFormatter.Format("markdown", "[relative](../work-items/42)"));
        Should.Throw<ArgumentException>(() => DiscussionPostBodyFormatter.Format("plain",
            new string('x', DiscussionPostBodyFormatter.MaximumSubmittedUtf8Bytes + 1)));
        Should.Throw<ArgumentException>(() => DiscussionPostBodyFormatter.Format("plain",
            new string('&', DiscussionPostBodyFormatter.MaximumSubmittedUtf8Bytes)));
    }

    [Fact]
    public async Task Review_model_preserves_all_bound_append_details()
    {
        const string text = "Review this **exact** note.";
        const string effectiveBody = "<p>Review this <strong>exact</strong> note.</p>\n";
        var json = $$"""
        {
          "version": 1,
          "workspace": { "organization": "acme", "project": "widgets" },
          "operations": [{
            "id": "comment-1", "kind": "append-comment", "workItemId": 42,
            "savedBenchId": "7", "expectedBenchContentsDigest": "{{new string('a', 64)}}",
            "expectedIdentityId": "identity-1", "format": "markdown",
            "text": {{JsonSerializer.Serialize(text)}},
            "effectiveBody": {{JsonSerializer.Serialize(effectiveBody)}}
          }]
        }
        """;
        var parsed = new PlanDocumentParser().Parse(json);
        parsed.IsValid.ShouldBeTrue();
        var workItems = Substitute.For<IWorkItemRepository>();
        workItems.GetByIdsAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkItem>>([]));

        var model = await new ChangeProposalReviewModelBuilder(workItems)
            .BuildAsync(parsed.Plan!, parsed.Digest!, [], []);

        model.ModelVersion.ShouldBe(2);
        var operation = model.Operations.Single();
        operation.Preconditions.Select(precondition => (precondition.Kind, precondition.Value))
            .ShouldBe([
                ("savedBenchId", "7"),
                ("expectedBenchContentsDigest", new string('a', 64)),
                ("expectedIdentityId", "identity-1"),
            ]);
        var effect = operation.Consequences.Single();
        effect.Kind.ShouldBe("discussion-append");
        effect.BodyFormat.ShouldBe("markdown");
        effect.SubmittedText.ShouldBe(text);
        effect.EffectiveBody.ShouldBe(effectiveBody);

        using var serialized = JsonDocument.Parse(ChangeProposalReviewModelJson.Serialize(model));
        var wireOperation = serialized.RootElement.GetProperty("operations")[0];
        wireOperation.GetProperty("preconditions").GetArrayLength().ShouldBe(3);
        var wireEffect = wireOperation.GetProperty("consequences")[0];
        wireEffect.GetProperty("bodyFormat").GetString().ShouldBe("markdown");
        wireEffect.GetProperty("submittedText").GetString().ShouldBe(text);
        wireEffect.GetProperty("effectiveBody").GetString().ShouldBe(effectiveBody);
    }

    [Fact]
    public async Task Legacy_review_model_keeps_v1_version_and_wire_shape()
    {
        var definition = new PlanDefinition
        {
            Version = 1,
            Workspace = new PlanWorkspace { Organization = "acme", Project = "widgets" },
            Operations =
            [
                new BatchOperation
                {
                    Id = "edit",
                    WorkItemId = 42,
                    ExpectedRevision = 3,
                    Fields = new Dictionary<string, string?> { ["System.Title"] = "Updated" },
                },
            ],
        };
        var workItems = Substitute.For<IWorkItemRepository>();
        workItems.GetByIdsAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkItem>>([]));

        var model = await new ChangeProposalReviewModelBuilder(workItems)
            .BuildAsync(definition, new string('a', 64), [], []);

        model.ModelVersion.ShouldBe(1);
        using var serialized = JsonDocument.Parse(ChangeProposalReviewModelJson.Serialize(model));
        serialized.RootElement.GetProperty("modelVersion").GetInt32().ShouldBe(1);
        var consequence = serialized.RootElement.GetProperty("operations")[0].GetProperty("consequences")[0];
        consequence.TryGetProperty("bodyFormat", out _).ShouldBeFalse();
        consequence.TryGetProperty("submittedText", out _).ShouldBeFalse();
        consequence.TryGetProperty("effectiveBody", out _).ShouldBeFalse();
    }

    [Fact]
    public void Version_2_refuses_v1_append_operation()
    {
        const string json = """
        {"version":2,"workspace":{"organization":"acme","project":"widgets"},
         "operations":[{"id":"x","kind":"append-comment","target":{} }]}
        """;

        var parsed = new PlanDocumentParser().Parse(json);

        parsed.IsValid.ShouldBeFalse();
        parsed.Issues.ShouldContain(issue => issue.Code == PlanValidationCodes.UnknownKind);
    }
}
