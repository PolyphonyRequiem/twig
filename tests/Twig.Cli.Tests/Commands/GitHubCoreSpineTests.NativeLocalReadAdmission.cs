using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;
using Model = Twig.Core.Core;

namespace Twig.Cli.Tests.Commands;

public sealed partial class GitHubCoreSpineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeGitHubLocalOriginalReadersShareEntryAdmission(bool exact)
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        using var editor = (await fixture.OpenAsync()).Value;
        var pair = (await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).Value;
        var created = (await editor.ReviewLocalRelationshipAsync(pair.Fields.Ticket, true)).Value;
        var before = await fixture.SnapshotAsync();
        before.Drafts.ShouldBeEmpty();
        before.Proposals.ShouldHaveSingleItem().Digest.ShouldBe(created.Proposal.Digest);
        before.Journal.ShouldHaveSingleItem().Status.ShouldBe(OpStatus.Planned);
        var proof = await LocalCaptureProof(fixture);
        var requests = fixture.Native.Http.Requests;
        var credentials = fixture.Native.CredentialCalls;
        var handles = fixture.Native.Http.HandleCreations;
        NativeLocalRelationshipReview loaded;
        if (exact)
        {
            var read = await editor.ReadLocalRelationshipReviewAsync(created.CaptureId, created.Proposal.Digest);
            read.IsSuccess.ShouldBeTrue(read.IsSuccess ? "" : read.Error.Message);
            loaded = read.Value;
        }
        else
        {
            var read = await editor.ReadLocalRelationshipReviewsAsync();
            read.IsSuccess.ShouldBeTrue(read.IsSuccess ? "" : read.Error.Message);
            loaded = read.Value.ShouldHaveSingleItem();
        }
        (fixture.Native.Http.HandleCreations - handles).ShouldBe(2,
            "one entry and one post-read source admission; original lookup must not reopen the admitted source");
        loaded.CaptureId.ShouldBe(created.CaptureId);
        Model.ProposalBytes(loaded.Proposal).ShouldBe(Model.ProposalBytes(created.Proposal));
        loaded.Outcome.ShouldNotBeNull().Journal!.Status.ShouldBe(OpStatus.Planned);
        loaded.Outcome.Settled.ShouldBeFalse();
        AssertSameLocalState(before, await fixture.SnapshotAsync());
        (await LocalCaptureProof(fixture)).ShouldBe(proof);
        fixture.Native.Http.Requests.ShouldBe(requests);
        fixture.Native.CredentialCalls.ShouldBe(credentials);
        fixture.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeGitHubLocalOriginalReadersStillRefuseSourceChangedAfterEntryAdmission(bool exact)
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        var path = Path.Combine(fixture.Host.Root, ".twig", "core-workspace.json");
        var changed = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        changed["principalId"]!.GetValue<string>().ShouldBe("U_actor");
        changed["principalId"] = "U_other";
        var changeOnAdmission = false;
        var changedAfterOpen = false;
        using var editor = (await NativeItemEditor.OpenAsync(fixture.Host.Root, fixture.Host.Bench, fixture.Host.Stamp, services =>
        {
            fixture.Configure(services);
            services.AddSingleton(new CoreTransportOverride(() =>
            {
                if (changeOnAdmission)
                {
                    changeOnAdmission = false;
                    File.WriteAllText(path, changed.ToJsonString());
                    changedAfterOpen = true;
                }
                return fixture.Native.Http.NewHandle();
            }));
        }, default)).Value;
        var pair = (await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).Value;
        var created = (await editor.ReviewLocalRelationshipAsync(pair.Fields.Ticket, true)).Value;
        var before = await fixture.SnapshotAsync();
        before.Drafts.ShouldBeEmpty();
        before.Proposals.ShouldHaveSingleItem().Digest.ShouldBe(created.Proposal.Digest);
        before.Journal.ShouldHaveSingleItem().Status.ShouldBe(OpStatus.Planned);
        var proof = await LocalCaptureProof(fixture);
        var requests = fixture.Native.Http.Requests;
        var credentials = fixture.Native.CredentialCalls;
        changeOnAdmission = true;
        Error failure;
        if (exact)
        {
            var refused = await editor.ReadLocalRelationshipReviewAsync(created.CaptureId, created.Proposal.Digest);
            refused.IsSuccess.ShouldBeFalse();
            failure = refused.Error;
        }
        else
        {
            var refused = await editor.ReadLocalRelationshipReviewsAsync();
            refused.IsSuccess.ShouldBeFalse();
            failure = refused.Error;
        }
        changedAfterOpen.ShouldBeTrue("replace the recorded actor after the entry handle read its original document");
        changeOnAdmission.ShouldBeFalse();
        failure.Code.ShouldBe(ErrorCode.Conflict);
        AssertSameLocalState(before, await fixture.SnapshotAsync());
        (await LocalCaptureProof(fixture)).ShouldBe(proof);
        fixture.Native.Http.Requests.ShouldBe(requests);
        fixture.Native.CredentialCalls.ShouldBe(credentials);
        fixture.Writes.ShouldBe(0);
    }
}
