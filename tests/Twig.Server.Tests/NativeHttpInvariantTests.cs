using Xunit;

namespace Twig.Server.Tests.Native;

public sealed class NativeHttpInvariantTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ProtectsNativePanelRoutesAndProfiles(bool profilesOnly) => NativePanelTests.Run(profilesOnly);

    [Fact]
    public Task AggregatesOnlyAdmittedNativeSources() => NativeAggregationTests.Run();

    [Fact]
    public void BoundsAndRedactsSourceDiagnostics() => NativeSourceDiagnosticsTests.Run();

    [Fact]
    public Task ScopesMcpBridgeAuthorityAndReleasesItsView() => McpBridgeTests.Run();

    [Theory]
    [InlineData("gates")]
    [InlineData("cancellation")]
    [InlineData("failures")]
    [InlineData("readback")]
    public Task ProtectsConnectionSetup(string scenario) => ConnectionSetupTests.Run(scenario);
}
