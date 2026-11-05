using System.Reflection;
using System.Collections.Immutable;
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
        var method = typeof(AdoProvider).GetMethod("ReadEditorAsync", BindingFlags.Instance | BindingFlags.Public);
        async Task<Result<QueryPage>> Read() => method is null
            ? await fixture.Provider.ReadAsync(fixture.Connection, [fixture.Key(1)], default, captureOnly: true)
            : await (Task<Result<QueryPage>>)method.Invoke(fixture.Provider, [metadata,
                ImmutableArray.Create(fixture.Key(1)), CancellationToken.None])!;
        (await Read()).IsSuccess.ShouldBeTrue();
        (await Read()).IsSuccess.ShouldBeTrue();
        fixture.Http.ReadAccessPaths.Skip(before).ShouldAllBe(path =>
            path.EndsWith("/connectionData") || path.Contains("/_apis/projects/") || path.Contains("/workitems/", StringComparison.OrdinalIgnoreCase));
        fixture.Access.Principal = fixture.Http.NativeActor = Fixture.Process;
        (await Read()).IsSuccess.ShouldBeFalse();
        fixture.Http.Writes.ShouldBe(0);
        (await fixture.Stored()).Version.ShouldBe(0);
    }
}
