using System.Reflection;
using Shouldly;
using Twig.Commands;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Regression tests for PolyphonyRequiem/twig#253 — <c>twig patch</c> rejected a positional
/// work-item ID ("Argument '63186915' is not recognized.") while <c>twig show</c>,
/// <c>twig set</c>, and <c>twig state</c> all accept one. The fix adds a positional
/// <c>workItemId</c> parameter to <c>Patch</c> while keeping <c>--id</c> working.
/// </summary>
public sealed class PatchPositionalIdTests
{
    private static MethodInfo GetCommand(string name)
    {
        var method = typeof(TwigCommands).GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
        method.ShouldNotBeNull($"TwigCommands.{name} not found");
        return method!;
    }

    private static bool HasArgumentAttribute(ParameterInfo parameter) =>
        // ConsoleAppFramework source-generates a local ArgumentAttribute per project, so we
        // match by name rather than type to avoid CS0436 ambiguity (same trick as GroupedHelpTests).
        parameter.GetCustomAttributes().Any(a => a.GetType().Name == "ArgumentAttribute");

    [Fact]
    public void Patch_HasPositionalWorkItemIdParameter()
    {
        var parameter = GetCommand(nameof(TwigCommands.Patch))
            .GetParameters()
            .SingleOrDefault(p => p.Name == "workItemId");

        parameter.ShouldNotBeNull("twig patch should accept a positional work-item ID (#253)");
        HasArgumentAttribute(parameter!).ShouldBeTrue(
            "the positional ID must carry [Argument] so 'twig patch 1234 --json ...' binds");
    }

    [Fact]
    public void Patch_PositionalIdIsOptional()
    {
        // `twig patch --json '...'` against the active item must keep working, so the
        // positional must be optional and nullable.
        var parameter = GetCommand(nameof(TwigCommands.Patch))
            .GetParameters()
            .Single(p => p.Name == "workItemId");

        parameter.IsOptional.ShouldBeTrue();
        parameter.ParameterType.ShouldBe(typeof(int?));
    }

    [Fact]
    public void Patch_PositionalIdIsTheFirstParameter()
    {
        // ConsoleAppFramework binds positionals in declaration order; the ID must come first
        // to match `twig show <id>` / `twig set <id>` / `twig state <name>`.
        GetCommand(nameof(TwigCommands.Patch))
            .GetParameters()[0].Name.ShouldBe("workItemId");
    }

    [Fact]
    public void Patch_StillAcceptsIdFlag()
    {
        // The documented form `twig patch --id 1234` must not regress.
        var parameter = GetCommand(nameof(TwigCommands.Patch))
            .GetParameters()
            .SingleOrDefault(p => p.Name == "id");

        parameter.ShouldNotBeNull("--id must remain supported for backward compatibility");
        parameter!.ParameterType.ShouldBe(typeof(int?));
        HasArgumentAttribute(parameter).ShouldBeFalse("--id stays an option, not a positional");
    }

    [Theory]
    [InlineData(nameof(TwigCommands.Show))]
    [InlineData(nameof(TwigCommands.Set))]
    [InlineData(nameof(TwigCommands.State))]
    [InlineData(nameof(TwigCommands.Patch))]
    public void IdAcceptingCommands_AllExposeAPositionalFirstParameter(string commandName)
    {
        // The consistency claim in #253: patch should look like its siblings.
        var first = GetCommand(commandName).GetParameters()[0];
        HasArgumentAttribute(first).ShouldBeTrue(
            $"twig {commandName.ToLowerInvariant()} should take its target positionally");
    }
}
