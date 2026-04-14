using System.Reflection;
using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class GroupedHelpTests
{
    [Fact]
    public void GroupedHelp_ShowsCorrectCommands()
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try { GroupedHelp.Show(); }
        finally { Console.SetOut(original); }
        var output = writer.ToString();

        output.ShouldNotContain("  save ");     // [Hidden] — must not appear
        output.ShouldContain("  sync ");
        output.ShouldContain("  seed new ");    // canonical seed command
    }

    [Fact]
    public void BareSeed_HasHiddenAttribute()
    {
        var method = typeof(TwigCommands).GetMethod(
            nameof(TwigCommands.Seed),
            BindingFlags.Public | BindingFlags.Instance);

        method.ShouldNotBeNull("TwigCommands.Seed method not found");
        // ConsoleAppFramework source-generates a local HiddenAttribute in each project,
        // so we match by name rather than by type to avoid CS0436 ambiguity.
        method.GetCustomAttributes()
            .Any(a => a.GetType().Name == "HiddenAttribute")
            .ShouldBeTrue("Bare Seed() should have [Hidden] since 'seed new' is the canonical command");
    }

    [Fact]
    public void SeedNew_DoesNotHaveHiddenAttribute()
    {
        var method = typeof(TwigCommands).GetMethod(
            nameof(TwigCommands.SeedNew),
            BindingFlags.Public | BindingFlags.Instance);

        method.ShouldNotBeNull("TwigCommands.SeedNew method not found");
        method.GetCustomAttributes()
            .Any(a => a.GetType().Name == "HiddenAttribute")
            .ShouldBeFalse("SeedNew should NOT be hidden — it is the canonical seed command");
    }
}
