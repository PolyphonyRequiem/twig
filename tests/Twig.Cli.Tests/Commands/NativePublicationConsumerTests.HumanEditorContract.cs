using Shouldly;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public void NativeHumanEditorBridgeIsAnActualPublicNativeSurface()
        => typeof(UserBenchStore).Assembly.GetType("Twig.Infrastructure.Services.NativeItemEditor").ShouldNotBeNull();
}
