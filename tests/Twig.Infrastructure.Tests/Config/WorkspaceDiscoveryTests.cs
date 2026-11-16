using Shouldly;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

public sealed class WorkspaceDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "twig-discovery-" + Guid.NewGuid().ToString("N"));
    public WorkspaceDiscoveryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ManifestAloneIdentifiesNearestWorkspace()
    {
        File.WriteAllText(Path.Combine(_root, "twig.json"), "{}");
        var nested = Path.Combine(_root, "nested");
        var child = Path.Combine(nested, "src");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(nested, "twig.json"), "{}");
        Directory.Exists(Path.Combine(nested, ".twig")).ShouldBeFalse();

        WorkspaceDiscovery.FindWorkspaceRoot(child).ShouldBe(nested);
        WorkspaceDiscovery.FindTwigDir(child).ShouldBe(Path.Combine(nested, ".twig"));
        WorkspaceDiscovery.IsWorkspaceDirectory(Path.Combine(nested, ".twig")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("config")]
    [InlineData("org", "project", "config")]
    [InlineData("org", "project", "twig.db")]
    public void RetiredLocalMarkersCannotShadowManifestWorkspace(params string[] marker)
    {
        File.WriteAllText(Path.Combine(_root, "twig.json"), "{}");
        var child = Path.Combine(_root, "nested");
        var twig = Path.Combine(child, ".twig");
        var path = Path.Combine(new[] { twig }.Concat(marker).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        File.Exists(Path.Combine(child, "twig.json")).ShouldBeFalse();

        WorkspaceDiscovery.IsWorkspaceDirectory(twig).ShouldBeFalse();
        WorkspaceDiscovery.FindWorkspaceRoot(child).ShouldBe(_root);
    }

    [Fact]
    public void GlobalHomeIsNotAWorkspace()
        => WorkspaceDiscovery.IsWorkspaceDirectory(WorkspaceDiscovery.GlobalHomePath).ShouldBeFalse();

    public void Dispose() => Directory.Delete(_root, true);
}
