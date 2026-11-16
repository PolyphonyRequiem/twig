using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Architecture;

public sealed class TestDiagnosticsActivationTests
{
    [Fact]
    public void Every_current_suite_compiles_and_activates_shared_trace_exactly_once()
    {
        var root = BuildFixture.FindRepoRoot();
        var frameworkDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = frameworkDirectory.Parent!.Name;
        var projects = XDocument.Load(Path.Combine(root, "Twig.slnx")).Descendants("Project")
            .Select(project => (string?)project.Attribute("Path"))
            .Where(path => path is not null && path.StartsWith("tests/", StringComparison.Ordinal))
            .Select(path => path!).ToArray();
        projects.Length.ShouldBe(9, "the current solution must include all nine supported suites");
        projects.Distinct(StringComparer.Ordinal).Count().ShouldBe(projects.Length);

        foreach (var project in projects)
        {
            var name = Path.GetFileNameWithoutExtension(project);
            var assembly = Path.Combine(root, Path.GetDirectoryName(project)!, "bin",
                configuration, frameworkDirectory.Name, name + ".dll");
            File.Exists(assembly).ShouldBeTrue($"build the supported solution first: {name}");
            using var stream = File.OpenRead(assembly);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var traces = metadata.TypeDefinitions.Where(handle =>
            {
                var type = metadata.GetTypeDefinition(handle);
                return metadata.GetString(type.Namespace) == "Twig.TestSupport"
                    && metadata.GetString(type.Name) == "TestProgressTraceAttribute";
            }).ToArray();
            traces.Length.ShouldBe(1, $"{name} must compile the shared source exactly once");
            metadata.GetAssemblyDefinition().GetCustomAttributes().Count(handle =>
            {
                var constructor = metadata.GetCustomAttribute(handle).Constructor;
                return constructor.Kind == HandleKind.MethodDefinition
                    && metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType() == traces[0];
            }).ShouldBe(1, $"{name} must activate the linked attribute exactly once");
        }
    }
}
