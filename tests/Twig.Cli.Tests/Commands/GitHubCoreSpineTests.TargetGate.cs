using Shouldly;
using Twig.Core;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class GitHubCoreSpineTests
{
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
