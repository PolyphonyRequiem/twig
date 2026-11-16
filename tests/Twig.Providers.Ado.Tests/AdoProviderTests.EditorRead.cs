using Shouldly;
using Twig.Core;
using Xunit;

namespace Twig.Providers.Ado.Tests;

public sealed partial class AdoProviderTests
{
    [Fact]
    public async Task EditorReadReusesItsNativeDiscoveryButRevalidatesActorAndProject()
    {
        using var fixture = await Fixture.Create();
        var metadata = (await fixture.Provider.CaptureEditorMetadataAsync(fixture.Connection, default)).Value;
        var before = fixture.Http.ReadAccessPaths.Count;
        Task<Result<QueryPage>> Read() => fixture.Provider.ReadEditorAsync(metadata, [fixture.Key(1)], default);
        (await Read()).IsSuccess.ShouldBeTrue();
        fixture.Http.ReadAccessPaths.Count.ShouldBeGreaterThan(before);
        var afterFirst = fixture.Http.ReadAccessPaths.Count;
        (await Read()).IsSuccess.ShouldBeTrue();
        fixture.Http.ReadAccessPaths.Count.ShouldBeGreaterThan(afterFirst);
        fixture.Http.ReadAccessPaths.Skip(before).ShouldAllBe(path =>
            path.EndsWith("/connectionData") || path.Contains("/_apis/projects/") || path.Contains("/workitems/", StringComparison.OrdinalIgnoreCase));
        fixture.Access.Principal = fixture.Http.NativeActor = Fixture.Process;
        (await Read()).IsSuccess.ShouldBeFalse();
        fixture.Http.Writes.ShouldBe(0);
        (await fixture.Stored()).Version.ShouldBe(0);
    }
}
