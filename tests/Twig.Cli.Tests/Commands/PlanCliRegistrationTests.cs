using System.Reflection;
using Shouldly;
using Twig.Commands;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Wiring guards specific to the plan and pending surfaces. The bulk of registration and
/// help-text completeness is enforced by <see cref="GroupedHelpTests"/> and
/// <see cref="Twig.Cli.Tests.DependencyInjection.CommandRegistrationCompletenessTests"/>;
/// these lock the promises native to the plan lifecycle CLI that the general guards would
/// happily let drift.
/// </summary>
public sealed class PlanCliRegistrationTests
{
    /// <summary>The plan and pending verbs must resolve as top-level compound commands.</summary>
    [Theory]
    [InlineData("plan")]
    [InlineData("plan validate")]
    [InlineData("plan preview")]
    [InlineData("plan apply")]
    [InlineData("plan status")]
    [InlineData("plan seed")]
    [InlineData("pending")]
    public void KnownCommands_IncludesPlanSurface(string command)
    {
        GroupedHelp.KnownCommands.ShouldContain(command);
    }

    /// <summary>
    /// The <c>plan apply</c> handler MUST accept a <c>confirm</c> option — the shared
    /// contract routes the digest through <c>--confirm</c>, and the MCP tool mirrors that
    /// spelling. If this parameter is renamed the CLI still compiles but the two surfaces
    /// silently disagree.
    /// </summary>
    [Fact]
    public void PlanApply_HasConfirmParameter()
    {
        var method = typeof(TwigCommands).GetMethod(
            nameof(TwigCommands.PlanApply),
            BindingFlags.Public | BindingFlags.Instance);

        method.ShouldNotBeNull();
        method.GetParameters().ShouldContain(p => p.Name == "confirm");
        method.GetParameters().ShouldContain(p => p.Name == "file");
    }

    [Theory]
    [InlineData(nameof(TwigCommands.PlanValidate), "plan validate")]
    [InlineData(nameof(TwigCommands.PlanPreview), "plan preview")]
    [InlineData(nameof(TwigCommands.PlanApply), "plan apply")]
    [InlineData(nameof(TwigCommands.PlanStatus), "plan status")]
    [InlineData(nameof(TwigCommands.PlanSeed), "plan seed")]
    [InlineData(nameof(TwigCommands.Pending), "pending")]
    public void PlanHandler_HasExpectedCommandAttribute(string methodName, string expected)
    {
        var method = typeof(TwigCommands).GetMethod(
            methodName, BindingFlags.Public | BindingFlags.Instance);

        method.ShouldNotBeNull();
        var cmdAttr = method.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.Name == "CommandAttribute");
        cmdAttr.ShouldNotBeNull($"{methodName} must carry [Command] so ConsoleAppFramework routes it as '{expected}'.");
        var name = (string)cmdAttr.ConstructorArguments[0].Value!;
        name.ShouldBe(expected);
    }

    [Theory]
    [InlineData(nameof(TwigCommands.PlanValidate))]
    [InlineData(nameof(TwigCommands.PlanPreview))]
    [InlineData(nameof(TwigCommands.PlanApply))]
    [InlineData(nameof(TwigCommands.PlanStatus))]
    [InlineData(nameof(TwigCommands.PlanSeed))]
    [InlineData(nameof(TwigCommands.Pending))]
    public void PlanHandler_IsNotHidden(string methodName)
    {
        var method = typeof(TwigCommands).GetMethod(
            methodName, BindingFlags.Public | BindingFlags.Instance);
        method.ShouldNotBeNull();
        method.GetCustomAttributes()
            .Any(a => a.GetType().Name == "HiddenAttribute")
            .ShouldBeFalse($"{methodName} must be visible in grouped help — the plan surface is not deprecated.");
    }
}
