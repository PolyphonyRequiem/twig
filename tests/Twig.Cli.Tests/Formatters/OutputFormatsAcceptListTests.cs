using Shouldly;
using Twig.Formatters;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

/// <summary>
/// Wayfinder 0019 — one accept-list for output formats.
/// </summary>
/// <remarks>
/// <para>
/// Before this fix both <see cref="OutputFormatterFactory"/> and
/// <see cref="RendererFactory"/> ended their format switch in a catch-all arm
/// that silently meant <c>human</c>, and nothing validated <c>-o</c>. So
/// <c>twig show -o jsno</c> emitted ANSI prose on stdout and exited 0 — the
/// worst failure mode for a tool whose output is piped into <c>jq</c>.
/// </para>
/// <para>
/// These tests pin the three halves of the fix: the accept-list is a single
/// literal, the entrypoint validator rejects anything off it with a non-zero
/// exit and a message naming the valid values, and the list cannot diverge
/// from the renderer switch.
/// </para>
/// <para>
/// Scope note: 0010 deliberately left payload/field shape UNPINNED. There are
/// intentionally no schema, <c>schemaVersion</c>, or golden-payload assertions
/// here. The accept-list is the entire contract.
/// </para>
/// </remarks>
public class OutputFormatsAcceptListTests
{
    // ---------------------------------------------------------------- the list

    [Fact]
    public void Accepted_IsExactlyTheHistoricalMembership()
    {
        OutputFormats.Accepted.ShouldBe(
            ["human", "json", "json-full", "json-compact", "minimal", "ids"],
            ignoreOrder: true);
    }

    [Fact]
    public void Default_IsOnTheAcceptList()
    {
        OutputFormats.Accepted.ShouldContain(OutputFormats.Default);
    }

    [Fact]
    public void FactoryDefaults_ForwardToTheSingleList()
    {
        // Both factories must read the one list rather than restate it.
        OutputFormatterFactory.DefaultFormat.ShouldBe(OutputFormats.Default);
        RendererFactory.DefaultFormat.ShouldBe(OutputFormats.Default);
    }

    // ------------------------------------------------- validation at the door

    [Theory]
    [InlineData("jsno")]      // the transposition typo from the ticket
    [InlineData("json5")]
    [InlineData("JSON5")]
    [InlineData("jsonc")]     // deleted by #281; must NOT silently mean human
    [InlineData("yaml")]
    [InlineData("")]
    [InlineData("json ")]     // stray whitespace is not silently forgiven
    public void Validate_UnknownFormat_IsRejected(string format)
    {
        var error = OutputFormatArgumentValidator.Validate(["show", "--output", format]);

        error.ShouldNotBeNull();
        OutputFormats.IsAccepted(format).ShouldBeFalse();
    }

    [Fact]
    public void Validate_UnknownFormat_MessageNamesEveryValidFormat()
    {
        var error = OutputFormatArgumentValidator.Validate(["show", "-o", "jsno"]);

        error.ShouldNotBeNull();
        error.ShouldContain("jsno");
        foreach (var accepted in OutputFormats.Accepted)
            error.ShouldContain(accepted);
    }

    [Fact]
    public void UsageExitCode_IsNonZero()
    {
        // The whole point of the ticket: a typo must not exit 0.
        OutputFormatArgumentValidator.UsageExitCode.ShouldNotBe(0);
    }

    [Theory]
    [InlineData("--output")]
    [InlineData("-o")]
    public void Validate_RejectsUnknownFormat_InBothSpellings(string flag)
    {
        OutputFormatArgumentValidator.Validate(["show", flag, "jsno"]).ShouldNotBeNull();
        OutputFormatArgumentValidator.Validate(["show", $"{flag}=jsno"]).ShouldNotBeNull();
    }

    // ------------------------------------------------ every valid format works

    [Fact]
    public void Validate_EveryAcceptedFormat_Passes()
    {
        foreach (var accepted in OutputFormats.Accepted)
        {
            OutputFormatArgumentValidator.Validate(["show", "--output", accepted])
                .ShouldBeNull($"'{accepted}' is on the accept-list and must pass validation");
            OutputFormatArgumentValidator.Validate(["show", "-o", accepted])
                .ShouldBeNull($"'{accepted}' is on the accept-list and must pass validation");
            OutputFormatArgumentValidator.Validate(["show", $"--output={accepted}"])
                .ShouldBeNull($"'{accepted}' is on the accept-list and must pass validation");
        }
    }

    [Fact]
    public void Validate_NoOutputFlag_Passes()
    {
        OutputFormatArgumentValidator.Validate(["show", "1234"]).ShouldBeNull();
        OutputFormatArgumentValidator.Validate([]).ShouldBeNull();
    }

    [Fact]
    public void Validate_BareTrailingFlag_IsLeftToTheArgumentParser()
    {
        // No value to judge — do not invent an error the parser reports better.
        OutputFormatArgumentValidator.Validate(["show", "--output"]).ShouldBeNull();
        OutputFormatArgumentValidator.Validate(["show", "-o"]).ShouldBeNull();
    }

    [Fact]
    public void Validate_StopsAtDoubleDash()
    {
        // Operands after `--` are pass-through, not format values.
        OutputFormatArgumentValidator.Validate(["show", "--", "--output", "jsno"]).ShouldBeNull();
    }

    // ------------------------------------------------------- case consistency

    [Theory]
    [InlineData("JSON", "json")]
    [InlineData("Json", "json")]
    [InlineData("HUMAN", "human")]
    [InlineData("Json-Full", "json-full")]
    [InlineData("IDS", "ids")]
    [InlineData("MINIMAL", "minimal")]
    public void Normalize_IsCaseInsensitive_MatchingHistoricalToLowerInvariant(string input, string expected)
    {
        // Both factories previously did (format ?? default).ToLowerInvariant().
        // Case handling must stay exactly that permissive — no more, no less.
        OutputFormats.Normalize(input).ShouldBe(expected);
        OutputFormatArgumentValidator.Validate(["show", "-o", input]).ShouldBeNull();
    }

    [Fact]
    public void Normalize_Null_YieldsDefault()
    {
        OutputFormats.Normalize(null).ShouldBe(OutputFormats.Default);
    }

    [Fact]
    public void Normalize_UnknownValue_YieldsNull()
    {
        OutputFormats.Normalize("jsno").ShouldBeNull();
    }

    // -------------------------------- the list and the switches cannot diverge

    [Fact]
    public void EveryAcceptedFormat_ResolvesToARenderer()
    {
        var factory = new RendererFactory();

        foreach (var accepted in OutputFormats.Accepted)
        {
            using var writer = new StringWriter();
            factory.GetRenderer(accepted, writer)
                .ShouldNotBeNull($"'{accepted}' is accepted so it must resolve to a renderer");
        }
    }

    [Fact]
    public void EveryAcceptedFormat_ResolvesToTheExpectedRendererFamily()
    {
        var factory = new RendererFactory();

        // Breaking-change rule 2 from 0010: an accepted value must not change
        // which family (human vs machine) it resolves to.
        //
        // The family is derived from the accept-list itself, NOT restated as a literal.
        // A restated list fails OPEN: adding "yaml" to Accepted without a RendererFactory
        // arm would fall to the catch-all SpectreNodeRenderer, land in the "human" branch
        // and pass — green while reproducing the very bug 0019 closed. Only the default
        // is human; everything else on the list must resolve to a machine renderer.
        foreach (var accepted in OutputFormats.Accepted)
        {
            using var writer = new StringWriter();
            var renderer = factory.GetRenderer(accepted, writer);
            var isHuman = renderer is SpectreNodeRenderer;

            isHuman.ShouldBe(
                accepted == OutputFormats.Default,
                $"'{accepted}' resolved to the {(isHuman ? "human" : "machine")} family. "
                + $"Only '{OutputFormats.Default}' may render as human; every other accepted "
                + "value needs its own RendererFactory arm. A value on the accept-list with no "
                + "arm silently falls through to the human catch-all — the 0019 bug.");
        }
    }

    [Fact]
    public void EveryAcceptedFormat_ResolvesToAFormatter()
    {
        var factory = new OutputFormatterFactory(new HumanOutputFormatter());

        foreach (var accepted in OutputFormats.Accepted)
            factory.GetFormatter(accepted).ShouldNotBeNull();
    }

    [Fact]
    public void MachineFormats_ResolveToAnAnsiStrippingFormatter()
    {
        var factory = new OutputFormatterFactory(new HumanOutputFormatter());

        foreach (var accepted in OutputFormats.Accepted.Where(f => f != "human"))
        {
            var fmt = factory.GetFormatter(accepted);
            fmt.FormatInfo("hello").ShouldNotContain("\x1b[", customMessage: accepted);
        }
    }

    [Fact]
    public void Describe_ListsEveryAcceptedValue_AndNothingElse()
    {
        var described = OutputFormats.Describe()
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        described.ShouldBe(OutputFormats.Accepted.ToArray(), ignoreOrder: true);
    }
}
