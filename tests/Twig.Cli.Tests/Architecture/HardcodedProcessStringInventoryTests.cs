using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Architecture;

public sealed class HardcodedProcessStringInventoryTests
{
    private static readonly Regex ForbiddenLiteral = new(
        "\"(?:Initiative|Investigation|Feature|Bug|Spec|WayfinderTask|Done|Doing|To Do)\"",
        RegexOptions.Compiled);

    [Fact]
    public void No_current_process_owner_hardcodes_a_profile_owned_literal()
    {
        var root = BuildFixture.FindRepoRoot();
        var offenders = SweepRoots(root).SelectMany(relative => SourceFiles(root, relative))
            .SelectMany(file => File.ReadLines(file).Select((line, index) => (file, line, index)))
            .Where(row => IsForbidden(row.line))
            .Select(row => $"{Path.GetRelativePath(root, row.file)}:{row.index + 1}: {row.line.Trim()}")
            .ToArray();

        offenders.ShouldBeEmpty("process-owned names must come from the connected provider's schema");
    }

    [Fact]
    public void The_sweep_covers_current_solution_roots_and_is_non_vacuous()
    {
        var root = BuildFixture.FindRepoRoot();
        var roots = SweepRoots(root);
        roots.ShouldContain("src/Twig");
        roots.ShouldContain("src/Twig.Core");
        roots.ShouldContain("src/Twig.RenderTree");
        foreach (var relative in roots)
            SourceFiles(root, relative).ShouldNotBeEmpty($"no source scanned under {relative}");
        File.Exists(Path.Combine(root, "src", "Twig.Core", "Schema.cs")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("var state = \"Doing\";", true)]
    [InlineData("var type = \"Feature\";", true)]
    [InlineData("var type = schema.Types[0].Name;", false)]
    [InlineData("// example: \"Feature\"", false)]
    public void Literal_scanner_has_positive_and_negative_controls(string line, bool forbidden) =>
        IsForbidden(line).ShouldBe(forbidden);

    private static bool IsForbidden(string line)
    {
        var trimmed = line.TrimStart();
        return !trimmed.StartsWith("//", StringComparison.Ordinal)
            && !trimmed.StartsWith("/*", StringComparison.Ordinal)
            && !trimmed.StartsWith('*')
            && ForbiddenLiteral.IsMatch(line);
    }

    private static string[] SourceFiles(string root, string relative)
    {
        var directory = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.Exists(directory).ShouldBeTrue($"current source root missing: {relative}");
        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"))
            .ToArray();
    }

    private static string[] SweepRoots(string root)
    {
        var roots = XDocument.Load(Path.Combine(root, "Twig.slnx")).Descendants("Project")
            .Select(project => (string?)project.Attribute("Path"))
            .Where(path => path is not null && path.StartsWith("src/", StringComparison.Ordinal))
            .Select(path => path![..path.LastIndexOf('/')])
            .ToArray();
        roots.ShouldNotBeEmpty("the current supported solution must have production projects");
        roots.Distinct(StringComparer.Ordinal).Count().ShouldBe(roots.Length);
        return roots;
    }
}
