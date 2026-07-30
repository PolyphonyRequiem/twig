using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Twig.Domain.Tests.Architecture;

/// <summary>
/// Guards the published packages' target-framework list (issue #315).
/// </summary>
/// <remarks>
/// <para>
/// <c>Directory.Build.props</c> sets <c>&lt;TargetFramework&gt;net11.0&lt;/TargetFramework&gt;</c>
/// repo-wide. Before #315 that meant every published package shipped <c>lib/net11.0</c> only, and
/// since .NET 11 has no GA release, consuming ANY twig package forced the consumer onto a preview
/// SDK. The fix multi-targets the three packable contract libraries to
/// <c>net10.0;net11.0</c> — the executables deliberately stay on the newest runtime.
/// </para>
/// <para>
/// This is the enforcement point. The failure mode it guards is invisible from inside the repo:
/// twig builds and tests fine against its own pinned SDK either way, and the cost only shows up
/// on a consumer's machine. Two ways it can silently regress — a packable library losing its
/// <c>TargetFrameworks</c> override, or a NEW packable library being added that never got one —
/// so the packable set is DISCOVERED from the csprojs rather than hardcoded.
/// </para>
/// <para>
/// Note each packable csproj must also blank the inherited singular <c>TargetFramework</c>;
/// MSBuild prefers it over <c>TargetFrameworks</c>, so leaving it set makes the multi-target
/// silently a no-op that still builds green. That trap is asserted explicitly below.
/// </para>
/// </remarks>
public sealed class PackableTargetFrameworkTests
{
    private const string RequiredTargetFrameworks = "net10.0;net11.0";

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Twig.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        dir.ShouldNotBeNull("Could not find repository root (looked for Twig.slnx)");
        return dir;
    }

    /// <summary>
    /// A project is packable if it declares a PackageId and is not explicitly IsPackable=false.
    /// This mirrors what <c>.github/workflows/release.yml</c> actually runs `dotnet pack` on.
    /// </summary>
    private static IReadOnlyList<(string Name, XDocument Doc)> DiscoverPackableProjects()
    {
        var srcDir = Path.Combine(FindRepoRoot(), "src");
        var result = new List<(string, XDocument)>();

        foreach (var path in Directory.EnumerateFiles(srcDir, "*.csproj", SearchOption.AllDirectories))
        {
            var doc = XDocument.Load(path);
            var hasPackageId = doc.Descendants("PackageId").Any();
            var optedOut = doc.Descendants("IsPackable")
                .Any(e => string.Equals(e.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase));

            if (hasPackageId && !optedOut)
            {
                result.Add((Path.GetFileNameWithoutExtension(path), doc));
            }
        }

        return result;
    }

    [Fact]
    public void PackableProjectsAreDiscovered()
    {
        // A fixture guard: if discovery silently found nothing, every assertion below would
        // vacuously pass over an empty collection.
        DiscoverPackableProjects()
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ShouldBe(["Twig.Domain", "Twig.Infrastructure", "Twig.RenderTree"]);
    }

    [Fact]
    public void EveryPackableProjectMultiTargetsTheGaFramework()
    {
        foreach (var (name, doc) in DiscoverPackableProjects())
        {
            var frameworks = doc.Descendants("TargetFrameworks").SingleOrDefault()?.Value.Trim();

            frameworks.ShouldBe(
                RequiredTargetFrameworks,
                $"{name} is published to nuget.org. Without a GA target framework in its " +
                "TargetFrameworks list, consumers are forced onto a preview SDK (issue #315).");
        }
    }

    [Fact]
    public void EveryPackableProjectBlanksTheInheritedSingularTargetFramework()
    {
        foreach (var (name, doc) in DiscoverPackableProjects())
        {
            var singular = doc.Descendants("TargetFramework").SingleOrDefault();

            singular.ShouldNotBeNull(
                $"{name} must blank the TargetFramework inherited from Directory.Build.props, " +
                "or MSBuild prefers it and the multi-target is a silent no-op.");

            singular.Value.Trim().ShouldBeEmpty(
                $"{name}'s TargetFramework must be empty so TargetFrameworks takes effect.");
        }
    }
}
