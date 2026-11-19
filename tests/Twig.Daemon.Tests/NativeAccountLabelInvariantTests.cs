using Xunit;

namespace Twig.Daemon.Tests.Native;

public sealed class NativeAccountLabelInvariantTests
{
    [Fact]
    public async Task LabelsOnlyTheExactLocallyBoundAccount()
    {
        var root = Path.Combine(Path.GetTempPath(), "twig-connection-label-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await NativeConnectionLabelTests.Run(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
