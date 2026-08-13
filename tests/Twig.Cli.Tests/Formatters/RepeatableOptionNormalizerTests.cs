using System.Reflection;
using System.Text.Json;
using Shouldly;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

/// <summary>
/// AB#350 — repeated <c>--field</c> / <c>--set</c> must all be written, not just the last.
/// </summary>
/// <remarks>
/// <para>
/// ConsoleAppFramework v5's generated parser emits one <c>case "--field":</c> arm that
/// ASSIGNS the bound array, so a second occurrence overwrote the first. <c>twig new</c>
/// then exited 0 having written one field of N — the same failure class as the
/// pre-0.86.1 <c>link predecessor</c> gap, where an unrecognised subcommand printed help
/// and exited 0 while wiring nothing. A caller checking the exit code cannot detect
/// either, which is why these assertions are on the rewritten ARGUMENT VECTOR rather
/// than on a return code.
/// </para>
/// <para>
/// <see cref="RepeatableOptionNormalizer"/> collapses the repeats into the single
/// JSON-array form the binder does support. JSON rather than comma-joining is
/// load-bearing and is pinned by <see cref="ValuesContainingCommas_AreNotSplit"/>:
/// field values legitimately contain commas, and comma-joining would trade a silent
/// drop for a silent split.
/// </para>
/// </remarks>
public class RepeatableOptionNormalizerTests
{
    // ------------------------------------------------------------ the defect itself

    [Fact]
    public void TwoFieldArguments_BothSurvive()
    {
        var result = RepeatableOptionNormalizer.Normalize(
        [
            "new", "--type", "Feature", "--title", "probe",
            "--field", "Custom.Maturity=Planned",
            "--field", "Custom.PriorityBand=P3 later",
        ]);

        // Exactly one --field remains, carrying BOTH values in order.
        result.Count(a => a == "--field").ShouldBe(1);
        Decode(result, "--field").ShouldBe(["Custom.Maturity=Planned", "Custom.PriorityBand=P3 later"]);
    }

    [Fact]
    public void ThreeFieldArguments_AllSurviveInOrder()
    {
        var result = RepeatableOptionNormalizer.Normalize(
        [
            "new", "--field", "a=1", "--field", "b=2", "--field", "c=3",
        ]);

        Decode(result, "--field").ShouldBe(["a=1", "b=2", "c=3"]);
    }

    [Fact]
    public void ReversedOrder_ReversesTheEncodedOrder()
    {
        // The reported behaviour was positional (last wins). Pinning both orders
        // proves the fix is order-preserving rather than order-dependent.
        Decode(RepeatableOptionNormalizer.Normalize(["new", "--field", "a=1", "--field", "b=2"]), "--field")
            .ShouldBe(["a=1", "b=2"]);
        Decode(RepeatableOptionNormalizer.Normalize(["new", "--field", "b=2", "--field", "a=1"]), "--field")
            .ShouldBe(["b=2", "a=1"]);
    }

    [Fact]
    public void RepeatedSet_IsNormalizedToo()
    {
        // `twig batch --set` is the sibling call path: same string[] binding,
        // same documented "Repeatable", so it had the identical defect.
        Decode(RepeatableOptionNormalizer.Normalize(["batch", "--set", "a=1", "--set", "b=2"]), "--set")
            .ShouldBe(["a=1", "b=2"]);
    }

    [Fact]
    public void EqualsForm_IsNormalizedAndMixesWithSpaceForm()
    {
        Decode(RepeatableOptionNormalizer.Normalize(["new", "--field=a=1", "--field", "b=2"]), "--field")
            .ShouldBe(["a=1", "b=2"]);
    }

    // ------------------------------------------------------------------ not-a-split

    [Fact]
    public void ValuesContainingCommas_AreNotSplit()
    {
        // Comma-joining would turn these two assignments into three malformed ones.
        var result = RepeatableOptionNormalizer.Normalize(
        [
            "new", "--field", "Custom.Notes=alpha, beta", "--field", "Custom.Owner=Green, Daniel",
        ]);

        Decode(result, "--field").ShouldBe(["Custom.Notes=alpha, beta", "Custom.Owner=Green, Daniel"]);
    }

    [Fact]
    public void EncodedValue_IsParseableAsJsonArray()
    {
        // The binder's array branch is JSON. If the encoding is not valid JSON the
        // rewrite would turn a silent drop into a hard parse failure.
        var result = RepeatableOptionNormalizer.Normalize(["new", "--field", "a=1", "--field", "b=2"]);
        var encoded = result[Array.IndexOf(result, "--field") + 1];

        encoded.ShouldStartWith("[");
        JsonSerializer.Deserialize<string[]>(encoded).ShouldNotBeNull().Length.ShouldBe(2);
    }

    // -------------------------------------------------------------- pass-through

    [Fact]
    public void SingleOccurrence_IsUnchanged()
    {
        // A lone --field must stay byte-identical so the existing comma and JSON
        // forms keep working exactly as before.
        string[] args = ["new", "--type", "Task", "--field", "a=1,b=2"];
        RepeatableOptionNormalizer.Normalize(args).ShouldBe(args);
    }

    [Fact]
    public void NoRepeatableOption_IsUnchanged()
    {
        string[] args = ["show", "350", "--refresh", "-o", "json"];
        RepeatableOptionNormalizer.Normalize(args).ShouldBe(args);
    }

    [Fact]
    public void TokensAfterDoubleDash_AreNotRewritten()
    {
        string[] args = ["new", "--field", "a=1", "--", "--field", "b=2"];
        RepeatableOptionNormalizer.Normalize(args).ShouldBe(args);
    }

    [Fact]
    public void TrailingOptionWithNoValue_IsLeftToTheParser()
    {
        // Swallowing it here would hide a usage error behind a rewrite.
        string[] args = ["new", "--field"];
        RepeatableOptionNormalizer.Normalize(args).ShouldBe(args);
    }

    [Fact]
    public void OptionPosition_IsPreserved()
    {
        var result = RepeatableOptionNormalizer.Normalize(
        [
            "new", "--field", "a=1", "--type", "Task", "--field", "b=2", "-o", "json",
        ]);

        // The collapsed option stays where the FIRST occurrence was, so anything
        // order-sensitive downstream (trailing params operands) is undisturbed.
        result[0].ShouldBe("new");
        result[1].ShouldBe("--field");
        result[3].ShouldBe("--type");
        result[4].ShouldBe("Task");
        result[5].ShouldBe("-o");
        result[6].ShouldBe("json");
        result.Length.ShouldBe(7);
    }

    // ------------------------------------------------------- the class, not the site

    [Fact]
    public void EveryStringArrayOptionOnACommand_IsCoveredByTheNormalizer()
    {
        // This is the guard that makes the fix a class fix rather than a site fix.
        // Any NEW string[] option added to a command has the same binder defect by
        // construction, so it must either be registered here or be a `params`
        // operand (which the generated parser DOES accumulate correctly).
        var commands = typeof(Twig.Formatters.OutputFormats).Assembly
            .GetType("TwigCommands")!;

        var uncovered = commands
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetParameters())
            .Where(p => p.ParameterType == typeof(string[]) || p.ParameterType == typeof(string[]))
            .Where(p => !p.IsDefined(typeof(ParamArrayAttribute), inherit: false))
            .Select(p => "--" + ToKebab(p.Name!))
            .Distinct(StringComparer.Ordinal)
            .Where(o => !RepeatableOptionNormalizer.RepeatableOptions.Contains(o, StringComparer.Ordinal))
            .ToList();

        uncovered.ShouldBeEmpty(
            $"string[] options bind last-wins (AB#350). Add to RepeatableOptionNormalizer.RepeatableOptions: {string.Join(", ", uncovered)}");
    }

    private static string ToKebab(string name)
        => string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i == 0 ? char.ToLowerInvariant(c).ToString() : "-" + char.ToLowerInvariant(c)) : c.ToString()));

    private static string[] Decode(string[] args, string option)
    {
        var i = Array.IndexOf(args, option);
        i.ShouldBeGreaterThanOrEqualTo(0, $"{option} missing from rewritten args");
        return JsonSerializer.Deserialize<string[]>(args[i + 1])!;
    }
}
