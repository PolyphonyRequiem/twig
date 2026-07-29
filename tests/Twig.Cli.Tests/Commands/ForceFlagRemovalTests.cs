using System.Reflection;
using Shouldly;
using Twig.Commands;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Wayfinder 0004 slice 5 — the <c>--force</c> flag surface on <c>refresh</c> and <c>sync</c>.
/// </summary>
/// <remarks>
/// <para>
/// The owner ruled the flag is <b>deleted outright, with no migration note</b>: a scripted caller
/// gets a hard <c>Argument '--force' is not recognized</c>. Both alternatives were closed rather
/// than deferred — a warning no-op would have left a flag claiming to force something it doesn't
/// (0003 §4's silent coercion), and re-pointing it at "take remote" would have preserved a fast
/// path around the interactive flow this slice exists to make the single road.
/// </para>
/// <para>
/// <b>Scope trap.</b> <c>--force</c> exists on six unrelated commands with entirely different
/// meanings. <see cref="ForceOnUnrelatedCommands_IsUntouched"/> is the scope control: it fails if
/// this slice over-reached and deleted a flag it had no business touching. A reflection test over
/// the declaration is the only guard that runs in-suite — proving ConsoleAppFramework actually
/// stopped <i>binding</i> the flag needs the built exe, which the PR records separately.
/// </para>
/// </remarks>
public sealed class ForceFlagRemovalTests
{
    private static MethodInfo Command(string name) =>
        typeof(TwigCommands).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"command '{name}' not found on TwigCommands");

    private static bool HasForceParameter(MethodInfo command) =>
        command.GetParameters().Any(p =>
            string.Equals(p.Name, "force", StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData(nameof(TwigCommands.Refresh))]
    [InlineData(nameof(TwigCommands.Sync))]
    public void ForceIsGoneFromTheRefreshAndSyncSurface(string commandName)
    {
        HasForceParameter(Command(commandName)).ShouldBeFalse(
            $"'twig {commandName.ToLowerInvariant()} --force' was deleted outright by 0004 " +
            "slice 5; scripted callers get a hard parse error, not a quiet no-op");
    }

    /// <summary>
    /// The scope control. These are DIFFERENT flags: overwrite an existing workspace config, skip
    /// seed validation, skip an interactive confirmation, terminate a process holding a binary
    /// open. None of them is a write bypass and none was in scope.
    /// </summary>
    [Theory]
    [InlineData(nameof(TwigCommands.Init))]
    [InlineData(nameof(TwigCommands.SeedPublish))]
    [InlineData(nameof(TwigCommands.Delete))]
    [InlineData(nameof(TwigCommands.Upgrade))]
    public void ForceOnUnrelatedCommands_IsUntouched(string commandName)
    {
        HasForceParameter(Command(commandName)).ShouldBeTrue(
            $"'twig {commandName.ToLowerInvariant()} --force' is an unrelated flag with its own " +
            "meaning and was explicitly out of scope for 0004 slice 5");
    }

    /// <summary>
    /// The user-facing strings that advertised the flag must not survive it. Leaving one would
    /// tell the user to run a command that now errors, so it is asserted on the emitted metadata
    /// rather than left to code review.
    /// </summary>
    /// <remarks>
    /// <b>Both byte alignments must be scanned.</b> Strings live in the assembly's <c>#US</c> heap
    /// at arbitrary byte offsets, so decoding the file as UTF-16LE from offset 0 alone sees only
    /// the strings that happen to land on an even boundary and is silently blind to the rest. The
    /// first version of this test did exactly that: its positive control sat on an even offset and
    /// passed, while a live <c>"twig sync --force"</c> in <see cref="CommandExamples"/> sat on an
    /// odd one and went undetected through a full green suite. Scanning offset 0 and offset 1
    /// covers both cases.
    /// </remarks>
    [Fact]
    public void NoUserFacingStringStillAdvertisesTheDeletedFlag()
    {
        var assemblyPath = typeof(RefreshCommand).Assembly.Location;
        assemblyPath.ShouldNotBeNullOrEmpty();

        var bytes = File.ReadAllBytes(assemblyPath);
        var atEvenOffsets = System.Text.Encoding.Unicode.GetString(bytes);
        var atOddOffsets = System.Text.Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1);

        bool Present(string needle) =>
            atEvenOffsets.Contains(needle, StringComparison.Ordinal)
            || atOddOffsets.Contains(needle, StringComparison.Ordinal);

        foreach (var advertisement in new[] { "twig sync --force", "twig refresh --force" })
        {
            Present(advertisement).ShouldBeFalse(
                $"'{advertisement}' is advertised in a user-facing string but no longer parses; " +
                "guidance must point at 'twig sync' and 'twig edit <id>' instead");
        }

        // Positive control: the replacement text IS present, so a scan that silently found
        // nothing (wrong encoding, wrong file, wrong alignment) cannot pass this test.
        Present("twig edit <id>").ShouldBeTrue(
            "the replacement guidance must actually be in the shipped assembly");
    }
}
