using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Architecture;

/// <summary>
/// <c>tools/run-ci-tests.py</c> isolates every <c>Category=HostInventory</c> class in its own
/// session and refuses to run the Cli suite when its <c>HOST_CLASSES</c> registry disagrees
/// with the traited classes. That guard only runs in the release-candidate workflow, so a new
/// traited class could merge unregistered and fail every platform later. This test pins the
/// same equality in the ordinary Cli suite.
/// </summary>
public sealed class HostInventoryRegistryTests
{
    [Fact]
    public void HostClassesRegistry_MatchesClassLevelHostInventoryTraits()
    {
        var traited = typeof(HostInventoryRegistryTests).Assembly.GetTypes()
            .Where(type => type.CustomAttributes.Any(IsHostInventoryTrait))
            .Select(type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        var registry = ReadHostClassesRegistry();

        traited.ShouldNotBeEmpty();
        registry.Except(traited).OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBeEmpty("HOST_CLASSES names classes without a class-level HostInventory trait");
        traited.Except(registry).OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBeEmpty("HostInventory classes are missing from HOST_CLASSES in tools/run-ci-tests.py");
    }

    [Fact]
    public void HostInventoryTraits_AreDeclaredOnClassesOnly()
    {
        var methodLevel = typeof(HostInventoryRegistryTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                               | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.CustomAttributes.Any(IsHostInventoryTrait))
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}")
            .ToList();

        methodLevel.ShouldBeEmpty("tools/run-ci-tests.py partitions HostInventory by class");
    }

    private static bool IsHostInventoryTrait(CustomAttributeData attribute) =>
        attribute.AttributeType.FullName == "Xunit.TraitAttribute"
        && attribute.ConstructorArguments.Count == 2
        && Equals(attribute.ConstructorArguments[0].Value, "Category")
        && Equals(attribute.ConstructorArguments[1].Value, "HostInventory");

    private static HashSet<string> ReadHostClassesRegistry()
    {
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "run-ci-tests.py"));
        var block = Regex.Match(script, @"^HOST_CLASSES = \{(?<body>.*?)^\}", RegexOptions.Multiline | RegexOptions.Singleline);
        block.Success.ShouldBeTrue("HOST_CLASSES registry block not found");
        var lines = block.Groups["body"].Value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var registry = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var entry = Regex.Match(line, @"^""(?<name>Twig\.Cli\.Tests\.[\w.+]+)"",$");
            entry.Success.ShouldBeTrue($"unrecognised HOST_CLASSES entry: {line}");
            registry.Add(entry.Groups["name"].Value).ShouldBeTrue($"duplicate HOST_CLASSES entry: {line}");
        }
        registry.ShouldNotBeEmpty();
        return registry;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Twig.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not find the repo root above " + AppContext.BaseDirectory);
    }
}
