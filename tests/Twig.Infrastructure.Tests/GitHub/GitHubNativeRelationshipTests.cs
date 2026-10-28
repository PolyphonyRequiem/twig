using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Domain.Enums;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class GitHubNativeRelationshipTests
{
    [Fact]
    public async Task OfflineProviderLifecycle_ReadAuthorApplyReadbackUsesRealGitHubComposition()
    {
        using var fixture = await Fixture.CreateAsync(credentialSource: "github-app");
        var identity = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_item");
        var before = await fixture.ReadAsync(identity);
        var overlay = FieldValues.Empty.Set("System.State", new TextValue("local-only"));
        (await fixture.Local.UpdateAsync(before.Local.Id, before.Local.Revision, overlay))
            .Value.ShouldBeOfType<LocalWriteApplied>();
        var path = await fixture.WriteAsync(fields: new Dictionary<string, string?>
        {
            ["System.Title"] = "Offline qualified GitHub title",
        });
        var policyPath = fixture.Target.Paths.PolicyPath;
        await File.WriteAllTextAsync(policyPath, """{"version":2,"rules":[]}""");
        (await fixture.Lifecycle.PreviewAsync(path)).CanApply.ShouldBeFalse("invalid declared policy must fail closed");
        fixture.Network.Writes.ShouldBe(0);
        File.Delete(policyPath);

        var preview = await fixture.Lifecycle.PreviewAsync(path);
        preview.CanApply.ShouldBeTrue(string.Join("; ", preview.Issues.Select(i => i.Message)));
        preview.ReviewModel.ShouldNotBeNull().Operations.Single().Summary.ShouldContain("non-atomic");
        fixture.Network.Writes.ShouldBe(0, "authoring and preview must not publish");
        (await fixture.ApplyAsync(path, new string('0', 64))).Failed.ShouldBeTrue();
        fixture.Network.Writes.ShouldBe(0, "a different digest cannot authorize the effect");
        var result = await fixture.ApplyAsync(path, preview.Digest!);
        result.Failed.ShouldBeFalse(result.Error);
        result.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        fixture.Network.Writes.ShouldBe(1);
        var readback = await fixture.ReadAsync(identity);
        readback.Local.Id.ShouldBe(before.Local.Id);
        readback.ProviderFields["System.Title"].Value.ShouldBe(new TextValue("Offline qualified GitHub title"));
        readback.Local.Fields.ShouldBe(overlay);
        readback.Fields["System.State"].Value.ShouldBe(new TextValue("local-only"));
        (await fixture.Journal.GetAsync(preview.Digest!))!.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        (await fixture.ApplyAsync(path, preview.Digest!)).Failed.ShouldBeFalse();
        fixture.Network.Writes.ShouldBe(1, "verified replay must not write again");
    }

    [Fact]
    public async Task OfflineProviderLifecycle_DeclaredParentPolicyIsEvaluatedAgainBeforeGitHubWrite()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Network.IssueType = "Synthetic child type";
        var observed = await fixture.ReadAsync(new("github", "https://api.github.com", "I_item"));
        var type = observed.ProviderFields["System.WorkItemType"].Value.ShouldBeOfType<TextValue>().Value;
        var path = await fixture.WriteAsync();
        var policyPath = fixture.Target.Paths.PolicyPath;
        string Policy(string parentType) => JsonSerializer.Serialize(new
        {
            version = 1,
            rules = new[] { new { id = "parent-qualification", kind = "parent-type", childType = type,
                allowedParentTypes = new[] { parentType } } },
        });
        await File.WriteAllTextAsync(policyPath, Policy("disallowed-synthetic-type"));
        var denied = await fixture.Lifecycle.PreviewAsync(path);
        denied.CanApply.ShouldBeFalse();
        denied.ReviewModel.ShouldNotBeNull().PolicyDecisions.Single().IsBlocking.ShouldBeTrue();
        fixture.Network.Writes.ShouldBe(0);

        await File.WriteAllTextAsync(policyPath, Policy(type));
        var permitted = await fixture.Lifecycle.PreviewAsync(path);
        permitted.CanApply.ShouldBeTrue();
        permitted.ReviewModel.ShouldNotBeNull().PolicyDecisions.Single().IsBlocking.ShouldBeFalse();
        await File.WriteAllTextAsync(policyPath, Policy("disallowed-synthetic-type"));
        (await fixture.ApplyAsync(path, permitted.Digest!)).Failed.ShouldBeTrue();
        fixture.Network.Writes.ShouldBe(0, "a prior permitted preview cannot bypass a changed declared policy");
    }

    [Fact]
    public void DeclaredProposalCapabilitiesMatchFixedExecutorShapes()
    {
        var target = new ProviderWorkItemMutationTarget
        {
            Item = new() { LocalId = LocalWorkItemId.New(), ExternalIdentity = new("github", "https://api.github.com", "I_item") },
            ExpectedProviderRevision = new("github-observed:synthetic"),
        };
        var peer = new ProviderWorkItemReference
        {
            LocalId = LocalWorkItemId.New(), ExternalIdentity = new("github", "https://api.github.com", "I_peer"),
        };
        foreach (var capability in ProviderCapabilityCatalog.GitHub.ProposalOperations)
        {
            if (capability == "create") continue;
            var parts = capability.Split(':');
            var batch = parts[0] == "batch";
            var fields = batch ? new Dictionary<string, string?>
            {
                [parts[1]] = parts[1] == "System.State" ? "OPEN" : "native",
            } : null;
            GitHubNativeMutationService.Supports(new ProviderMutationOperation
            {
                Id = "test", Target = target, Concurrency = "observed-non-atomic",
                OperationKind = batch ? PlanOperationKind.Batch : parts[0] == "add-link" ? PlanOperationKind.AddLink : PlanOperationKind.RemoveLink,
                Fields = fields, Relation = batch ? null : parts[1], Other = batch ? null : peer,
            }).ShouldBeTrue(capability);
        }
        GitHubNativeMutationService.Supports(new ProviderMutationOperation
        {
            Id = "mixed", Target = target, Concurrency = "observed-non-atomic", OperationKind = PlanOperationKind.Batch,
            Fields = new Dictionary<string, string?> { ["github.labels.add"] = "L_label", ["System.Title"] = "Mixed" },
        }).ShouldBeFalse();
    }

    [Fact]
    public async Task AppOAuthSource_UsesSelectedCredentialForDigestBoundNativeWrites()
    {
        using var fixture = await Fixture.CreateAsync(credentialSource: "github-app");
        var (path, digest) = await fixture.PreviewAsync();
        var result = await fixture.ApplyAsync(path, digest);
        result.Failed.ShouldBeFalse();
        result.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        fixture.Network.Writes.ShouldBe(1);
        fixture.Network.WriterCredential.ShouldBe(fixture.Network.LastViewerCredentialBeforeWrite);
        (await fixture.Journal.GetAsync(digest))!.Origin!.Method.ShouldBe("github-github-app");
    }

    [Theory]
    [InlineData("System.Title", "New title")]
    [InlineData("System.Description", "Markdown body")]
    [InlineData("System.Description", null)]
    [InlineData("System.State", "CLOSED")]
    [InlineData("github.milestoneId", "M_milestone")]
    [InlineData("github.milestoneId", null)]
    [InlineData("github.labels.add", "L_label")]
    [InlineData("github.labels.remove", "L_label")]
    [InlineData("github.assignees.add", "U_assignee")]
    [InlineData("github.assignees.remove", "U_assignee")]
    public async Task NativeFields_AppCompositionUsesOneIntendedMutationAndVerifies(string field, string? value)
    {
        using var fixture = await Fixture.CreateAsync(credentialSource: "github-app");
        fixture.Network.Labels.Add("L_unrelated");
        fixture.Network.Assignees.Add("U_unrelated");
        if (field.EndsWith(".remove", StringComparison.Ordinal))
        {
            fixture.Network.Labels.Add("L_label");
            fixture.Network.Assignees.Add("U_assignee");
        }
        var path = await fixture.WriteAsync(fields: new Dictionary<string, string?> { [field] = value });
        var preview = await fixture.Lifecycle.PreviewAsync(path);
        preview.CanApply.ShouldBeTrue();
        preview.ReviewModel!.Operations.Single().Summary.ShouldContain("non-atomic");
        var result = await fixture.ApplyAsync(path, preview.Digest!);
        result.Failed.ShouldBeFalse(string.Join("; ", result.Operations.Select(o => o.Error).Append(result.Error)));
        result.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        fixture.Network.Writes.ShouldBe(1);
        fixture.Network.Labels.ShouldContain("L_unrelated");
        fixture.Network.Assignees.ShouldContain("U_unrelated");
        fixture.Network.WrittenInput.ShouldNotContain("If-Match");
        (await fixture.Journal.GetAsync(preview.Digest!))!.Operations.Single().ResultJson.ShouldNotBeNull().ShouldContain("observed-non-atomic");
        await fixture.ApplyAsync(path, preview.Digest!);
        fixture.Network.Writes.ShouldBe(1);
    }

    [Theory]
    [InlineData("System.Title", "New title", "stale")]
    [InlineData("github.labels.add", "L_label", "foreign-resource")]
    [InlineData("github.milestoneId", "M_milestone", "foreign-resource")]
    [InlineData("System.Title", "New title", "actor")]
    [InlineData("System.Title", "New title", "timestamp")]
    public async Task NativeFields_AdmissionRefusesChangedEvidence(string field, string value, string change)
    {
        using var fixture = await Fixture.CreateAsync(credentialSource: "github-app");
        var path = await fixture.WriteAsync(fields: new Dictionary<string, string?> { [field] = value });
        var preview = await fixture.Lifecycle.PreviewAsync(path);
        preview.CanApply.ShouldBeTrue();
        if (change == "stale") fixture.Network.Title = "Web edit";
        if (change == "actor") fixture.Network.Actor = "U_changed";
        if (change == "foreign-resource") fixture.Network.ResourceRepository = "R_other";
        if (change == "timestamp") fixture.Network.UpdatedAt = "2026-10-28T02:00:00Z";
        (await fixture.ApplyAsync(path, preview.Digest!)).Failed.ShouldBeTrue();
        fixture.Network.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("System.Title", "x", "exact-revision")]
    [InlineData("System.State", "Doing", "observed-non-atomic")]
    [InlineData("System.WorkItemType", "x", "observed-non-atomic")]
    [InlineData("github.labels", "x", "observed-non-atomic")]
    [InlineData("url", "https://evil.test", "observed-non-atomic")]
    public async Task NativeFields_UnsupportedShapesNeverImportOrWrite(string field, string value, string concurrency)
    {
        using var fixture = await Fixture.CreateAsync();
        var path = await fixture.WriteAsync(concurrency, new Dictionary<string, string?> { [field] = value });
        var preview = await fixture.Lifecycle.PreviewAsync(path);
        preview.CanApply.ShouldBeFalse();
        (await fixture.Journal.GetAsync(preview.Digest!)).ShouldBeNull();
        (await fixture.ApplyAsync(path, preview.Digest!)).Failed.ShouldBeTrue();
        fixture.Network.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("lost-response", true, PlanOperationState.Verified)]
    [InlineData("lost-response", false, PlanOperationState.Failed)]
    [InlineData("ack", false, PlanOperationState.Failed)]
    public async Task NativeFields_ReadbackClassifiesWithoutRetry(string response, bool effect, PlanOperationState expected)
    {
        using var fixture = await Fixture.CreateAsync();
        var path = await fixture.WriteAsync(fields: new Dictionary<string, string?> { ["System.Title"] = "New title" });
        var preview = await fixture.Lifecycle.PreviewAsync(path);
        fixture.Network.Response = response;
        fixture.Network.ApplyEffect = effect;
        (await fixture.ApplyAsync(path, preview.Digest!)).Operations.Single().State.ShouldBe(expected);
        await fixture.ApplyAsync(path, preview.Digest!);
        fixture.Network.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task Create_AppCompositionJournalsNativeIdentityAndFencesAcrossDigests()
    {
        using var fixture = await Fixture.CreateAsync(credentialSource: "github-app");
        var creation = Guid.NewGuid();
        var path = await fixture.WriteCreateAsync(creation);
        var preview = await fixture.Lifecycle.PreviewAsync(path);
        preview.CanApply.ShouldBeTrue(string.Join("; ", preview.Issues.Select(i => i.Message)));
        preview.ReviewModel!.Operations.Single().Summary.ShouldContain("fenced");
        var result = await fixture.ApplyAsync(path, preview.Digest!);
        result.Failed.ShouldBeFalse(string.Join("; ", result.Operations.Select(o => o.Error).Append(result.Error)));
        result.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        fixture.Network.Writes.ShouldBe(1);
        (await fixture.Journal.GetAsync(preview.Digest!))!.Operations.Single().ResultJson.ShouldNotBeNull().ShouldContain("I_created");
        (await fixture.Local.FindAsync(new("github", "https://api.github.com", "I_created"))).ShouldNotBeNull();
        await fixture.ApplyAsync(path, preview.Digest!);
        fixture.Network.Writes.ShouldBe(1);
        // A new digest retaining this creation identity must not POST a second issue.
        path = await fixture.WriteCreateAsync(creation, title: "Different title", file: "revised.json");
        var changed = await fixture.Lifecycle.PreviewAsync(path);
        (await fixture.ApplyAsync(path, changed.Digest!)).Operations.Single().State.ShouldBe(PlanOperationState.Indeterminate);
        fixture.Network.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task Create_LostAcknowledgmentRemainsIndeterminateAndNeverInfersMatchingTitle()
    {
        using var fixture = await Fixture.CreateAsync(credentialSource: "github-app");
        var creation = Guid.NewGuid();
        var path = await fixture.WriteCreateAsync(creation);
        var preview = await fixture.Lifecycle.PreviewAsync(path);
        fixture.Network.Response = "lost-response";
        var result = await fixture.ApplyAsync(path, preview.Digest!);
        result.Operations.Single().State.ShouldBe(PlanOperationState.Indeterminate);
        fixture.Network.Writes.ShouldBe(1);
        await fixture.ApplyAsync(path, preview.Digest!);
        path = await fixture.WriteCreateAsync(creation, file: "retry.json", opId: "renamed");
        var retry = await fixture.Lifecycle.PreviewAsync(path);
        (await fixture.ApplyAsync(path, retry.Digest!)).Operations.Single().State.ShouldBe(PlanOperationState.Indeterminate);
        fixture.Network.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task Create_ForeignDestinationRefusesBeforeMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        var path = await fixture.WriteCreateAsync(Guid.NewGuid(), foreign: true);
        var preview = await fixture.Lifecycle.PreviewAsync(path);
        (await fixture.ApplyAsync(path, preview.Digest!)).Failed.ShouldBeTrue();
        fixture.Network.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task Create_CrashAfterAcknowledgmentRecoversNativeIdentityWithoutAnotherPost()
    {
        using var fixture = await Fixture.CreateAsync(credentialSource: "github-app");
        var path = await fixture.WriteCreateAsync(Guid.NewGuid());
        var preview = await fixture.Lifecycle.PreviewAsync(path);
        var digest = preview.Digest!;
        var old = DateTimeOffset.UtcNow.AddMinutes(-10);
        await fixture.Journal.ConfirmAsync(digest, old);
        await fixture.Journal.TryTransitionOperationAsync(digest, "create",
            PlanOperationState.Planned, PlanOperationState.Confirmed, old);
        await fixture.Journal.TryTransitionOperationAsync(digest, "create",
            PlanOperationState.Confirmed, PlanOperationState.Applying, old);
        var operation = (ProviderCreateOperation)new PlanDocumentParser().Parse(await File.ReadAllTextAsync(path)).Plan!.Operations.Single();
        var origin = (await fixture.Journal.GetAsync(digest))!.Origin;
        // Simulate the native attempt landing, followed by loss of the operation-row acknowledgment.
        (await fixture.Native.ExecuteCreateAsync(operation, origin, CancellationToken.None)).Outcome.ShouldBe(PlanExecutionOutcome.Applied);
        fixture.Network.Writes.ShouldBe(1);
        var recovered = await fixture.ApplyAsync(path, digest);
        recovered.Failed.ShouldBeFalse();
        recovered.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        recovered.Operations.Single().ResultJson.ShouldNotBeNull().ShouldContain("localId");
        fixture.Network.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task Create_CorruptFenceRemainsUnknownWithoutAnotherPost()
    {
        using var fixture = await Fixture.CreateAsync();
        var creation = Guid.NewGuid();
        var path = await fixture.WriteCreateAsync(creation);
        var preview = await fixture.Lifecycle.PreviewAsync(path);
        var directory = Path.Combine(fixture.Target.Paths.TwigDir, "github-create-fences");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, creation.ToString("D") + ".json"), "{}");
        var result = await fixture.ApplyAsync(path, preview.Digest!);
        result.Operations.Single().State.ShouldBe(PlanOperationState.Indeterminate);
        fixture.Network.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task MultipleOperations_VerifiedPrefixRemainsWhenNextPreflightIsStale()
    {
        using var fixture = await Fixture.CreateAsync();
        var path = await fixture.WriteAsync(fields: new Dictionary<string, string?> { ["System.Title"] = "First effect" });
        var parsed = new PlanDocumentParser().Parse(await File.ReadAllTextAsync(path));
        var first = fixture.Operation!;
        await File.WriteAllTextAsync(path, PlanDocumentWriter.Write(parsed.Plan! with
        {
            Operations =
            [
                first,
                first with { Id = "second", Fields = new Dictionary<string, string?> { ["System.State"] = "CLOSED" } },
                first with { Id = "tail", Fields = new Dictionary<string, string?> { ["System.Description"] = "Untouched" } },
            ],
        }));
        var preview = await fixture.Lifecycle.PreviewAsync(path);
        preview.CanApply.ShouldBeTrue();
        var result = await fixture.ApplyAsync(path, preview.Digest!);
        result.Failed.ShouldBeTrue();
        result.Operations.Select(o => o.State).ShouldBe(
            [PlanOperationState.Verified, PlanOperationState.Failed, PlanOperationState.Confirmed]);
        fixture.Network.Title.ShouldBe("First effect");
        fixture.Network.State.ShouldBe("OPEN");
        fixture.Network.Body.ShouldBe("");
        fixture.Network.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task UnknownCredentialSourceNeverFallsBackToGhForNativeWrites()
    {
        using var fixture = await Fixture.CreateAsync(credentialSource: "unsupported");
        var (path, digest) = await fixture.PreviewAsync();
        (await fixture.ApplyAsync(path, digest)).Failed.ShouldBeTrue();
        fixture.Network.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task NativeFields_ConcurrentUnrelatedEditIsPreservedByIntendedPatch()
    {
        using var fixture = await Fixture.CreateAsync();
        var path = await fixture.WriteAsync(fields: new Dictionary<string, string?> { ["System.Description"] = "Intended body" });
        var preview = await fixture.Lifecycle.PreviewAsync(path);
        fixture.Network.InterleavedTitle = "Concurrent web edit";
        var result = await fixture.ApplyAsync(path, preview.Digest!);
        result.Failed.ShouldBeFalse();
        fixture.Network.Title.ShouldBe("Concurrent web edit");
        fixture.Network.Body.ShouldBe("Intended body");
        fixture.Network.WrittenInput.ShouldNotContain("title");
        var evidence = (await fixture.Journal.GetAsync(preview.Digest!))!.Operations.Single().ResultJson.ShouldNotBeNull();
        evidence.ShouldContain("nativeReadback");
        evidence.ShouldContain("observed-non-atomic");
    }

    [Theory]
    [InlineData("parent", true)]
    [InlineData("parent", false)]
    [InlineData("predecessor", true)]
    [InlineData("predecessor", false)]
    [InlineData("successor", true)]
    [InlineData("successor", false)]
    public async Task ProductionComposition_DigestConfirmsJournalsWritesAndReadsBackBothEndpoints(string relation, bool add)
    {
        using var fixture = await Fixture.CreateAsync(relation, add);
        var (path, digest) = await fixture.PreviewAsync();

        var result = await fixture.ApplyAsync(path, digest);

        result.Error.ShouldBeNull();
        result.Failed.ShouldBeFalse();
        result.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        fixture.Network.Writes.ShouldBe(1);
        fixture.Network.Edge.ShouldBe(add);
        var journal = (await fixture.Journal.GetAsync(digest))!;
        var authority = journal.Origin.ShouldNotBeNull().ProviderAuthority.ShouldNotBeNull();
        authority.ActorId.ShouldBe("U_actor");
        authority.Target.ShouldBe(GitHubTargetFixture.Target.ToIdentity(new("https://api.github.com/graphql")));
        fixture.Network.WriterCredential.ShouldBe(fixture.Network.LastViewerCredentialBeforeWrite);
        fixture.Network.ReadIdsAfterWrite.ShouldContain("I_item");
        fixture.Network.ReadIdsAfterWrite.ShouldContain("I_peer");
        fixture.Network.WrittenQuery.ShouldBe(GitHubRelationshipMutations.Create(fixture.Operation!, "https://api.github.com").Query);
        (await fixture.ApplyAsync(path, digest)).Failed.ShouldBeFalse();
        fixture.Network.Writes.ShouldBe(1);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("actor")]
    [InlineData("attachment")]
    [InlineData("peer-repository")]
    [InlineData("mapping")]
    public async Task ChangedPrecondition_RefusesWithoutNativeWriteOrLocalFallback(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        var (path, digest) = await fixture.PreviewAsync();
        switch (change)
        {
            case "token": fixture.Network.Title = "Changed"; break;
            case "actor": fixture.Network.Actor = "U_changed"; break;
            case "attachment":
                var attachment = (await fixture.Target.Attachment.ReadWithRevisionAsync()).Value;
                (await fixture.Target.Attachment.ClearAsync(attachment.Envelope!.BindingId!.Value, attachment.Revision))
                    .IsSuccess.ShouldBeTrue();
                break;
            case "peer-repository": fixture.Network.PeerRepository = "R_other"; break;
            case "mapping":
                await fixture.Local.DeleteAsync(fixture.Operation!.Other!.LocalId, 1);
                break;
        }

        var result = await fixture.ApplyAsync(path, digest);

        result.Failed.ShouldBeTrue();
        fixture.Network.Writes.ShouldBe(0);
        string.Join("; ", result.Operations.Select(o => o.Error).Append(result.Error))
            .ShouldNotContain("fixture-credential");
    }

    [Fact]
    public async Task ExactRevisionDefault_IsReviewableButNeverImportedOrApplied()
    {
        using var fixture = await Fixture.CreateAsync();
        var path = await fixture.WriteAsync("exact-revision");
        var preview = await fixture.Lifecycle.PreviewAsync(path);
        preview.CanApply.ShouldBeFalse();
        preview.Issues.ShouldContain(i => i.Code == PlanValidationCodes.UnsupportedApplyVersion);
        (await fixture.Journal.GetAsync(preview.Digest!)).ShouldBeNull();
        (await fixture.ApplyAsync(path, preview.Digest!)).Failed.ShouldBeTrue();
        fixture.Network.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task DigestMismatch_RemainsUnappliedWithoutNativeWrite()
    {
        using var fixture = await Fixture.CreateAsync();
        var (path, digest) = await fixture.PreviewAsync();
        var mismatchedDigest = (digest[0] == '0' ? "1" : "0") + digest[1..];
        var result = await fixture.Lifecycle.ApplyAsync(path, mismatchedDigest);
        result.Failed.ShouldBeTrue();
        (result.Error ?? throw new InvalidOperationException("Expected a digest-mismatch refusal."))
            .ShouldContain("does not match confirmed digest");
        (await fixture.Journal.GetAsync(digest))!.Operations.Single().State.ShouldBe(PlanOperationState.Planned);
        fixture.Network.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("lost-response", true, true)]
    [InlineData("lost-response", false, false)]
    [InlineData("contradiction", false, false)]
    [InlineData("graphql-error", true, true)]
    [InlineData("redirect", false, false)]
    public async Task UncertainOrContradictoryResponse_UsesNativeReadbackAndNeverReplays(
        string response, bool effect, bool verified)
    {
        using var fixture = await Fixture.CreateAsync();
        var (path, digest) = await fixture.PreviewAsync();
        fixture.Network.Response = response;
        fixture.Network.ApplyEffect = effect;

        var first = await fixture.ApplyAsync(path, digest);
        first.Failed.ShouldBe(!verified);
        first.Operations.Single().State.ShouldBe(verified ? PlanOperationState.Verified : PlanOperationState.Failed);
        await fixture.ApplyAsync(path, digest);
        fixture.Network.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task DeniedReadback_RemainsIndeterminateWithoutReplay()
    {
        using var fixture = await Fixture.CreateAsync();
        var (path, digest) = await fixture.PreviewAsync();
        fixture.Network.DenyReadback = true;
        var result = await fixture.ApplyAsync(path, digest);
        result.Failed.ShouldBeTrue();
        result.Operations.Single().State.ShouldBe(PlanOperationState.Indeterminate);
        await fixture.ApplyAsync(path, digest);
        fixture.Network.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task CrashRecovery_ObservesNativeEffectWithoutIssuingMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        var (path, digest) = await fixture.PreviewAsync();
        var old = DateTimeOffset.UtcNow.AddMinutes(-10);
        await fixture.Journal.ConfirmAsync(digest, old);
        await fixture.Journal.TryTransitionOperationAsync(digest, "link",
            PlanOperationState.Planned, PlanOperationState.Confirmed, old);
        await fixture.Journal.TryTransitionOperationAsync(digest, "link",
            PlanOperationState.Confirmed, PlanOperationState.Applying, old);
        fixture.Network.Edge = true;

        var result = await fixture.ApplyAsync(path, digest);

        result.Error.ShouldBeNull();
        result.Failed.ShouldBeFalse();
        result.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        fixture.Network.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task JournalRejectsConcurrencyDowngradeAgainstConfirmedArtifact()
    {
        using var fixture = await Fixture.CreateAsync();
        var path = await fixture.WriteAsync();
        var parsed = new PlanDocumentParser().Parse(await File.ReadAllTextAsync(path));
        var altered = parsed.Plan! with
        {
            Operations = [fixture.Operation! with { Concurrency = "exact-revision" }],
        };
        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            fixture.Journal.ImportAsync(altered, parsed.CanonicalJson!, parsed.Digest!, path, DateTimeOffset.UtcNow));
        error.Message.ShouldContain("concurrency");
        (await fixture.Journal.GetAsync(parsed.Digest!)).ShouldBeNull();
    }

    [Fact]
    public void ObservationToken_IsOrderIndependentButChangesWithFieldsAndRelationships()
    {
        var item = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_item");
        var peer = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_peer");
        var third = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_third");
        var fields = FieldValues.Empty.Set("title", new TextValue("Title"));
        ProviderWorkItemRelationship[] edges =
        [
            new(item, peer, LinkKind.ParentChild),
            new(third, item, LinkKind.PredecessorSuccessor),
        ];
        var token = GitHubObservationToken.Create(item, fields, edges);
        GitHubObservationToken.Create(item, fields, edges.Reverse()).ShouldBe(token);
        GitHubObservationToken.Create(item, fields.Set("title", new TextValue("Changed")), edges).ShouldNotBe(token);
        GitHubObservationToken.Create(item, fields, []).ShouldNotBe(token);
    }

    private sealed class Fixture : IDisposable
    {
        internal GitHubTargetFixture Target { get; } = new();
        private readonly SqliteCacheStore _store = new("Data Source=:memory:");
        internal Network Network { get; } = new();
        private ServiceProvider _services = null!;
        internal SqliteLocalWorkItemRepository Local { get; private set; } = null!;
        internal IPlanJournalRepository Journal => _services.GetRequiredService<IPlanJournalRepository>();
        internal IPlanLifecycleService Lifecycle => _services.GetRequiredService<IPlanLifecycleService>();
        internal GitHubNativeMutationService Native => _services.GetRequiredService<GitHubNativeMutationService>();
        internal ProviderMutationOperation? Operation { get; private set; }
        private readonly ExternalWorkItemIdentity _item = new("github", "https://api.github.com", "I_item");
        private readonly ExternalWorkItemIdentity _peer = new("github", "https://api.github.com", "I_peer");

        internal static async Task<Fixture> CreateAsync(
            string relation = "parent", bool add = true, string? credentialSource = null)
        {
            var fixture = new Fixture();
            await fixture.Target.BindAsync();
            fixture.Network.Relation = relation;
            fixture.Network.Add = add;
            fixture.Network.Edge = !add;
            var config = new TwigConfiguration
            {
                Provider = "github", GitHub = new() { Host = "github.com", Repository = "owner/repo" },
                GitHubAccount = "selected-account",
                GitHubCredentialSource = credentialSource,
            };
            var services = new ServiceCollection();
            services.AddTwigInfrastructure(config, fixture.Target.Paths.TwigDir, fixture.Target.Root);
            services.AddConnectionDomainServices();
            fixture.Target.Register(services);
            services.AddSingleton(fixture._store);
            services.AddSingleton<IGitHubTokenSource>(new Tokens());
            services.AddSingleton(new GitHubTransportOverride(() => new Forwarder(fixture.Network)));
            fixture._services = services.BuildServiceProvider();
            fixture.Local = fixture._services.GetRequiredService<SqliteLocalWorkItemRepository>();
            return fixture;
        }

        internal async Task<string> WriteAsync(string concurrency = "observed-non-atomic",
            IReadOnlyDictionary<string, string?>? fields = null)
        {
            var snapshot = await _services.GetRequiredService<IWorkItemSnapshotReader>().FetchSnapshotAsync(_item);
            var item = await Local.ResolveAsync(_item, snapshot.RevisionToken);
            var peer = await Local.ResolveAsync(_peer, null);
            Operation = new ProviderMutationOperation
            {
                Id = "link", OperationKind = fields is not null ? PlanOperationKind.Batch
                    : Network.Add ? PlanOperationKind.AddLink : PlanOperationKind.RemoveLink,
                Concurrency = concurrency, Relation = fields is null ? Network.Relation : null, Fields = fields,
                Target = new() { Item = new() { LocalId = item.Id, ExternalIdentity = _item },
                    ExpectedProviderRevision = snapshot.RevisionToken! },
                Other = fields is null ? new() { LocalId = peer.Id, ExternalIdentity = _peer } : null,
            };
            var path = Path.Combine(Target.Root, "proposal.json");
            await File.WriteAllTextAsync(path, PlanDocumentWriter.Write(new()
            {
                Version = 2, Workspace = new() { Organization = "github.com", Project = "owner/repo" },
                Operations = [Operation],
            }));
            return path;
        }

        internal async Task<string> WriteCreateAsync(Guid creation, string title = "Created title",
            string file = "create.json", string opId = "create", bool foreign = false)
        {
            var path = Path.Combine(Target.Root, file);
            await File.WriteAllTextAsync(path, PlanDocumentWriter.Write(new()
            {
                Version = 2, Workspace = new() { Organization = "github.com", Project = "owner/repo" },
                Operations = [new ProviderCreateOperation
                {
                    Id = opId, CreationId = creation, Concurrency = "observed-non-atomic",
                    Destination = new GitHubRepositoryTarget("O_owner", foreign ? "R_other" : "R_repo")
                        .ToIdentity(new("https://api.github.com/graphql")),
                    Fields = new Dictionary<string, string?>
                    {
                        ["System.Title"] = title, ["System.Description"] = "Created body",
                    },
                }],
            }));
            return path;
        }

        internal async Task<(string Path, string Digest)> PreviewAsync()
        {
            var path = await WriteAsync();
            var preview = await Lifecycle.PreviewAsync(path);
            preview.CanApply.ShouldBeTrue(string.Join("; ", preview.Issues.Select(i => i.Message)));
            preview.ReviewModel.ShouldNotBeNull().Operations.Single().Summary.ShouldContain("non-atomic");
            return (path, preview.Digest!);
        }

        internal Task<PlanApplyResult> ApplyAsync(string path, string digest) =>
            Lifecycle.ApplyAsync(path, digest);

        internal Task<ProviderWorkItemReadResult> ReadAsync(ExternalWorkItemIdentity identity) =>
            _services.GetRequiredService<IProviderWorkItemReadService>().ReadByIdentityAsync(identity);

        public void Dispose()
        {
            _services?.Dispose();
            _store.Dispose();
            Target.Dispose();
        }
    }

    private sealed class Tokens : IGitHubTokenSource
    {
        private int _calls;
        public ValueTask<string> GetTokenAsync(CancellationToken ct) =>
            ValueTask.FromResult("fixture-credential-" + ++_calls);
    }

    private sealed class Forwarder(Network network) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            network.SendAsync(request, ct);
    }

    private sealed class Network
    {
        internal string Relation { get; set; } = "parent";
        internal bool Add { get; set; } = true;
        internal bool Edge { get; set; }
        internal bool ApplyEffect { get; set; } = true;
        internal bool DenyReadback { get; set; }
        internal string Response { get; set; } = "ack";
        internal string Actor { get; set; } = "U_actor";
        internal string Title { get; set; } = "Title";
        internal string State { get; set; } = "OPEN";
        internal string? IssueType { get; set; }
        internal string? UpdatedAt { get; set; } = "2026-10-28T01:00:00Z";
        internal string? InterleavedTitle { get; set; }
        internal string Body { get; set; } = "";
        internal string? Milestone { get; set; }
        internal string ResourceRepository { get; set; } = "R_repo";
        internal HashSet<string> Labels { get; } = [];
        internal HashSet<string> Assignees { get; } = [];
        internal string PeerRepository { get; set; } = "R_repo";
        internal int Writes { get; private set; }
        internal string? WrittenQuery { get; private set; }
        internal string WrittenInput { get; private set; } = "";
        internal string? WriterCredential { get; private set; }
        internal string? LastViewerCredentialBeforeWrite { get; private set; }
        internal HashSet<string> ReadIdsAfterWrite { get; } = [];

        internal async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri.ShouldBe(new("https://api.github.com/graphql"));
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var query = document.RootElement.GetProperty("query").GetString()!;
            var vars = document.RootElement.GetProperty("variables");
            var credential = request.Headers.Authorization!.Parameter;
            string body;
            if (query == GitHubIssueQueries.AuthenticatedViewer)
            {
                if (Writes == 0) LastViewerCredentialBeforeWrite = credential;
                body = $$$$$$"""{"data":{"viewer":{"id":"{{{{{{Actor}}}}}}","login":"locator"}}}""";
            }
            else if (query.StartsWith("mutation", StringComparison.Ordinal))
            {
                Writes++;
                if (InterleavedTitle is not null) Title = InterleavedTitle;
                WrittenQuery = query;
                WriterCredential = credential;
                string name;
                string payload;
                if (vars.TryGetProperty("input", out var input))
                {
                    WrittenInput = input.GetRawText();
                    var create = query.Contains("createIssue(", StringComparison.Ordinal);
                    name = create ? "createIssue" : query.Contains("updateIssue(", StringComparison.Ordinal) ? "updateIssue"
                        : new[] { "addLabelsToLabelable", "removeLabelsFromLabelable", "addAssigneesToAssignable", "removeAssigneesFromAssignable" }
                            .Single(n => query.Contains(n + "(", StringComparison.Ordinal));
                    var member = name.Contains("Label", StringComparison.Ordinal) ? "labelable"
                        : name.Contains("Assignees", StringComparison.Ordinal) ? "assignable" : "issue";
                    var nativeId = create ? "I_created" : "I_item";
                    if (create) input.GetProperty("repositoryId").GetString().ShouldBe("R_repo");
                    if (ApplyEffect)
                    {
                        if (input.TryGetProperty("title", out var title)) Title = title.GetString()!;
                        if (input.TryGetProperty("body", out var description)) Body = description.GetString()!;
                        if (input.TryGetProperty("state", out var state)) State = state.GetString()!;
                        if (input.TryGetProperty("milestoneId", out var milestone)) Milestone = milestone.GetString();
                        foreach (var (key, set) in new[] { ("labelIds", Labels), ("assigneeIds", Assignees) })
                            if (input.TryGetProperty(key, out var ids))
                                foreach (var id in ids.EnumerateArray())
                                    if (name.StartsWith("add", StringComparison.Ordinal)) set.Add(id.GetString()!);
                                    else set.Remove(id.GetString()!);
                    }
                    payload = $$$$$$"""{"{{{{{{member}}}}}}":{"id":"{{{{{{nativeId}}}}}}"}}""";
                }
                else
                {
                    var issueId = vars.GetProperty("issueId").GetString();
                    issueId.ShouldBe(Relation is "parent" or "successor" ? "I_peer" : "I_item");
                    vars.GetProperty(Relation == "parent" ? "subIssueId" : "blockingIssueId").GetString()
                        .ShouldBe(Relation is "parent" or "successor" ? "I_item" : "I_peer");
                    if (ApplyEffect) Edge = Add;
                    name = (Add ? "add" : "remove") + (Relation == "parent" ? "SubIssue" : "BlockedBy");
                    payload = """{"clientMutationId":null}""";
                }
                if (Response == "lost-response") throw new HttpRequestException("private provider response");
                if (Response == "redirect") return Json("{}", HttpStatusCode.TemporaryRedirect);
                if (Response == "graphql-error") return Json("""{"errors":[{"message":"private response"}]}""");
                body = $$$$$$"""{"data":{"{{{{{{name}}}}}}":{{{{{{payload}}}}}}}}""";
            }
            else if (query.Contains("... on Label", StringComparison.Ordinal))
            {
                var id = vars.GetProperty("id").GetString()!;
                var type = id.StartsWith("L_", StringComparison.Ordinal) ? "Label"
                    : id.StartsWith("M_", StringComparison.Ordinal) ? "Milestone" : "User";
                body = $$$$$$"""{"data":{"node":{"__typename":"{{{{{{type}}}}}}","id":"{{{{{{id}}}}}}","repository":{"id":"{{{{{{ResourceRepository}}}}}}","owner":{"id":"O_owner"}}}}}""";
            }
            else
            {
                var id = vars.GetProperty("id").GetString()!;
                if (Writes > 0) ReadIdsAfterWrite.Add(id);
                if (Writes > 0 && DenyReadback) return Json("{}", HttpStatusCode.Forbidden);
                var other = id == "I_item" ? "I_peer" : "I_item";
                if (query == GitHubIssueQueries.Snapshot)
                {
                    var parent = Edge && Relation == "parent" && id == "I_item"
                        ? """{"id":"I_peer"}""" : "null";
                    var repository = id == "I_peer" ? PeerRepository : "R_repo";
                    var milestone = Milestone is null ? "null" : $$"""{"id":"{{Milestone}}","title":"Milestone"}""";
                    var issueType = IssueType is null ? "null" : $$"""{"name":{{Quote(IssueType)}}}""";
                    body = $$$$$$"""
                        {"data":{"node":{"__typename":"Issue","id":"{{{{{{id}}}}}}","number":1,"title":{{{{{{Quote(Title)}}}}}},"state":"{{{{{{State}}}}}}",
                        "body":{{{{{{Quote(Body)}}}}}},"updatedAt":{{{{{{(UpdatedAt is null ? "null" : Quote(UpdatedAt))}}}}}},"url":"https://example.test/issue","issueType":{{{{{{issueType}}}}}},"parent":{{{{{{parent}}}}}},"milestone":{{{{{{milestone}}}}}},
                        "repository":{"id":"{{{{{{repository}}}}}}","nameWithOwner":"owner/repo","owner":{"id":"O_owner"}}}}}
                        """;
                }
                else
                {
                    var field = new[] { "labels", "assignees", "subIssues", "blockedBy", "blocking" }
                        .Single(f => query == GitHubIssueQueries.Connection(f));
                    var hasEdge = Edge && (Relation, id, field) is
                        ("parent", "I_peer", "subIssues") or
                        ("predecessor", "I_item", "blockedBy") or ("predecessor", "I_peer", "blocking") or
                        ("successor", "I_peer", "blockedBy") or ("successor", "I_item", "blocking");
                    var nodes = hasEdge ? $$"""[{"id":"{{other}}"}]""" : "[]";
                    if (field == "labels")
                        nodes = "[" + string.Join(",", Labels.Select(l => $$"""{"id":"{{l}}","name":"{{l}}"}""")) + "]";
                    if (field == "assignees")
                        nodes = "[" + string.Join(",", Assignees.Select(a => $$"""{"id":"{{a}}","login":"{{a}}"}""")) + "]";
                    body = $$$$$$"""
                        {"data":{"node":{"__typename":"Issue","id":"{{{{{{id}}}}}}","{{{{{{field}}}}}}":{
                        "nodes":{{{{{{nodes}}}}}},"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                        """;
                }
            }
            return Json(body);
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static string Quote(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";
    }
}
