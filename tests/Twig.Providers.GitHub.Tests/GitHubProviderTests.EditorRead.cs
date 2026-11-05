using System.Reflection;
using System.Collections.Immutable;
using Shouldly;
using Twig.Core;
using Xunit;

namespace Twig.Providers.GitHub.Tests;

public sealed partial class GitHubProviderTests
{
    [Fact]
    public async Task EditorReadReusesItsNativeDiscoveryButRevalidatesActorAndRepository()
    {
        using var fixture = await Fixture.Create();
        var metadata = (await fixture.Provider.CaptureEditorMetadataAsync(fixture.Connection, default)).Value;
        var before = fixture.Http.ReadQueries.Count;
        var method = typeof(GitHubProvider).GetMethod("ReadEditorAsync", BindingFlags.Instance | BindingFlags.Public);
        async Task<Result<QueryPage>> Read() => method is null
            ? await fixture.Provider.ReadAsync(fixture.Connection, [fixture.Key("I1")], default, captureOnly: true)
            : await (Task<Result<QueryPage>>)method.Invoke(fixture.Provider, [metadata,
                ImmutableArray.Create(fixture.Key("I1")), CancellationToken.None])!;
        (await Read()).IsSuccess.ShouldBeTrue();
        (await Read()).IsSuccess.ShouldBeTrue();
        fixture.Http.ReadQueries.Skip(before).ShouldAllBe(query => !query.Contains("__type(") && !query.Contains("issueTypes"));
        fixture.Access.Actor = fixture.Http.Actor = "A_other";
        (await Read()).IsSuccess.ShouldBeFalse();
        fixture.Http.Writes.ShouldBe(0);
        (await fixture.Stored()).Version.ShouldBe(0);
    }
}
