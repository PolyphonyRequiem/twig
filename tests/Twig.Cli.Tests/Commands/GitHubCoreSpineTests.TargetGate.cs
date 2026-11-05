using Shouldly;
using Twig.Core;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class GitHubCoreSpineTests
{
    [Theory]
    [InlineData(422, true)]
    [InlineData(503, false)]
    public async Task NativeGitHubHttpRejectionPermitsOnlyFreshReviewedNoEffectEdits(int status, bool definitive)
    {
        using var fixture = new Fixture();
        await fixture.Initialize();
        fixture.Http.RejectNextMutation = (System.Net.HttpStatusCode)status;
        fixture.Run("update", "title", "original rejected request", "--id", "42", "-o", "json").ShouldBe(1);
        var before = await fixture.Snapshot();
        var original = before.Journal.Single();
        original.Status.ShouldBe(definitive ? OpStatus.Failed : OpStatus.Indeterminate);
        fixture.Run("update", "title", "fresh reviewed request", "--id", "42", "-o", "json").ShouldBe(definitive ? 0 : 1);
        fixture.Http.Writes.ShouldBe(definitive ? 2 : 1);
        var after = await fixture.Snapshot();
        var retained = after.Journal.Single(row => row.Digest == original.Digest);
        retained.Status.ShouldBe(original.Status);
        retained.Attempt.ShouldBe(original.Attempt);
        if (definitive) after.Journal.ShouldContain(row => row.Digest != original.Digest && row.Status == OpStatus.Verified);
    }

    [Fact]
    public async Task NativeUnknownGitHubWriteFencesAnotherRootNormalSaveThroughRealComposition()
    {
        using var first = new Fixture();
        await first.Initialize();
        first.Http.LoseAcknowledgement = true;
        first.Run("update", "title", "original unknown effect", "--id", "42", "-o", "json").ShouldBe(1);
        (await first.Snapshot()).Journal.Single().Status.ShouldBe(OpStatus.Indeterminate);
        first.Http.Writes.ShouldBe(1);
        using var second = new Fixture(Path.Combine(first.Root, "home"));
        await second.Initialize();
        second.Http.Issue["title"] = first.Http.Issue["title"]!.DeepClone();
        second.Http.Issue["updatedAt"] = first.Http.Issue["updatedAt"]!.DeepClone();
        second.Run("update", "title", "must remain unpublished", "--id", "42", "-o", "json").ShouldBe(1);
        second.Http.Writes.ShouldBe(0, "a different root/acquisition fingerprint cannot bypass the native target fence");
        (await first.Snapshot()).Journal.Single().Status.ShouldBe(OpStatus.Indeterminate);
    }
}
