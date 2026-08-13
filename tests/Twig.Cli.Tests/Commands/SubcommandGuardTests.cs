using System.Reflection;
using Shouldly;
using Twig.Commands;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// AB#79 — an unrecognized SUBCOMMAND must exit non-zero with a message naming what was wrong.
///
/// <para>
/// Before the fix, <see cref="GroupedHelp.IsKnownCommand"/> inspected <c>args[0]</c> only, so a
/// known GROUP prefix waved every compound verb through. All three downstream outcomes exited
/// <b>0</b>: usage-and-nothing (<c>link</c>, <c>bench</c>), the bare command running instead
/// (<c>nav bogus</c> launched the navigator), or the stray word being eaten as a positional —
/// <c>twig seed bogus</c> CREATED a seed titled "bogus". #77 lost 13 link edges to the first of
/// those and only a read-back caught it.
/// </para>
/// </summary>
/// <remarks>
/// Asserts on each guard's DISTINCT wording. "Unknown subcommand" and "Missing subcommand" are
/// separate branches of <see cref="SubcommandGuard.Validate"/>, and a test asserting only
/// non-null would pass against either one dead — the mutual-masking failure mode AGENTS.md
/// records for ConflictFixture's paired guards, and which cost two survived mutants on AB#352.
/// </remarks>
public sealed class SubcommandGuardTests
{
    // ---- Unknown subcommand: the reported defect, across EVERY group, not just the three
    // verbs the ticket named. Bare-handler groups (nav, workspace, seed, area) are included
    // deliberately: they never printed usage at all, so an audit that only looked for the
    // usage-and-exit-0 signature would have missed them.
    [Theory]
    [InlineData("link", "bogus")]
    [InlineData("seed", "bogus")]
    [InlineData("nav", "bogus")]
    [InlineData("bench", "bogus")]
    [InlineData("auth", "bogus")]
    [InlineData("ohmyposh", "bogus")]
    [InlineData("workspace", "bogus")]
    [InlineData("area", "bogus")]
    public void UnknownSubcommand_IsRejected(string group, string verb)
    {
        var error = SubcommandGuard.Validate([group, verb]);

        error.ShouldNotBeNull($"'twig {group} {verb}' must not be routed — it exited 0 before AB#79.");
        error.ShouldContain($"Unknown subcommand: '{verb}' is not a '{group}' command.",
            customMessage: "the unknown-subcommand guard's own wording, distinct from the "
                + "missing-subcommand guard so neither can mask the other");
    }

    [Theory]
    [InlineData("workspace", "sprint", "bogus")]
    [InlineData("workspace", "area", "bogus")]
    public void UnknownSubcommand_IsRejectedAtTheThirdLevel(string a, string b, string verb)
    {
        var error = SubcommandGuard.Validate([a, b, verb]);

        error.ShouldNotBeNull();
        error.ShouldContain($"Unknown subcommand: '{verb}' is not a '{a} {b}' command.");
    }

    /// <summary>
    /// The message must name the valid verbs. A bare "unknown subcommand" would satisfy the
    /// ticket's exit-code acceptance while telling the caller nothing about what to type.
    /// </summary>
    [Fact]
    public void UnknownSubcommand_ListsTheValidVerbsForThatGroup()
    {
        var error = SubcommandGuard.Validate(["link", "bogus"]);

        error.ShouldNotBeNull();
        error.ShouldContain("Valid 'link' subcommands:");
        foreach (var verb in new[] { "parent", "unparent", "reparent", "predecessor", "successor", "unlink", "artifact" })
            error.ShouldContain(verb, customMessage: $"'link {verb}' is a real verb and must be offered");
    }

    /// <summary>
    /// Only the IMMEDIATE next word is offered. 'workspace area add' is reached via
    /// 'workspace area', so listing "area add" under 'workspace' would suggest a string the
    /// user cannot type as one word.
    /// </summary>
    [Fact]
    public void UnknownSubcommand_OffersOnlyImmediateVerbs()
    {
        var error = SubcommandGuard.Validate(["workspace", "bogus"]);

        error.ShouldNotBeNull();
        error.ShouldContain("area");
        error.ShouldNotContain("area add",
            customMessage: "'workspace area add' is not a valid completion of 'workspace <verb>'");
    }

    // ---- Missing subcommand: a group with NO bare handler, invoked bare, printed top-level
    // usage and exited 0. Same false green, reached by a different route.
    [Theory]
    [InlineData("link")]
    [InlineData("bench")]
    [InlineData("auth")]
    [InlineData("ohmyposh")]
    public void GroupWithNoBareHandler_InvokedBare_IsRejected(string group)
    {
        var error = SubcommandGuard.Validate([group]);

        error.ShouldNotBeNull($"'twig {group}' has no bare handler — it printed usage and exited 0.");
        error.ShouldContain($"Missing subcommand: '{group}' requires one.",
            customMessage: "the missing-subcommand guard's own wording, distinct from the "
                + "unknown-subcommand guard");
        error.ShouldContain($"Valid '{group}' subcommands:");
    }

    [Fact]
    public void GroupWithNoBareHandler_InvokedBare_AtTheSecondLevel_IsRejected()
    {
        var error = SubcommandGuard.Validate(["workspace", "sprint"]);

        error.ShouldNotBeNull();
        error.ShouldContain("Missing subcommand: 'workspace sprint' requires one.");
    }

    // ---- The trap on this card. A fix that breaks these is worse than the bug.
    [Theory]
    [InlineData("set", "123")]          // pinned by GroupedHelpTests.IsKnownCommand_FallsBackToTopLevelWhenCompoundUnknown
    [InlineData("show", "77")]
    [InlineData("state", "Active")]
    [InlineData("update", "Title")]
    [InlineData("delete", "77")]
    [InlineData("query", "search text")]
    [InlineData("discard", "77")]
    [InlineData("web", "77")]
    [InlineData("history", "77")]
    [InlineData("patch", "77")]
    public void PositionalArgumentCommand_IsRoutedNormally(string command, string argument)
    {
        SubcommandGuard.Validate([command, argument]).ShouldBeNull(
            $"'twig {command} {argument}' takes a positional value — rejecting it would be worse than the bug.");
    }

    /// <summary>
    /// <c>process</c> and <c>config</c> are the hard cases: they ARE group prefixes
    /// (<c>process layout</c>, <c>config status-fields</c>) AND their bare form takes a value.
    /// Structure alone cannot separate the two, which is why
    /// <see cref="SubcommandGuard.PrefixesTakingPositional"/> exists.
    /// </summary>
    [Theory]
    [InlineData("process", "Bug")]
    [InlineData("process", "User Story")]
    [InlineData("config", "org")]
    public void GroupPrefixWhoseBareFormTakesAValue_IsRoutedNormally(string command, string argument)
    {
        SubcommandGuard.Validate([command, argument]).ShouldBeNull();
    }

    [Theory]
    [InlineData("nav")]        // bare handler: launches the navigator
    [InlineData("workspace")]  // bare handler: the workspace view
    [InlineData("area")]       // bare handler: deprecated alias, still valid
    [InlineData("seed")]       // bare handler: hidden alias of 'seed new'
    [InlineData("process")]
    [InlineData("config")]
    [InlineData("show")]
    [InlineData("sync")]
    public void GroupWithABareHandler_InvokedBare_IsRoutedNormally(string command)
    {
        SubcommandGuard.Validate([command]).ShouldBeNull(
            $"'twig {command}' is a real command on its own.");
    }

    [Theory]
    [InlineData("link", "parent", "77")]
    [InlineData("link", "predecessor", "65")]
    [InlineData("seed", "new")]
    [InlineData("seed", "publish", "5")]
    [InlineData("nav", "up")]
    [InlineData("bench", "list")]
    [InlineData("auth", "status")]
    [InlineData("workspace", "track", "77")]
    [InlineData("workspace", "area", "add", "Twig")]
    [InlineData("ohmyposh", "init")]
    [InlineData("process", "layout", "Bug")]
    public void ValidCompoundCommand_IsRoutedNormally(params string[] args)
    {
        SubcommandGuard.Validate(args).ShouldBeNull(
            $"'twig {string.Join(' ', args)}' is a real command.");
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("frobnicate", "wildly")]
    public void UnknownTopLevelCommand_IsLeftToTheExistingGuard(params string[] args)
    {
        // GroupedHelp.ShowUnknown already handles this and already exits 1. Claiming it here
        // would produce two different messages for one condition.
        SubcommandGuard.Validate(args).ShouldBeNull();
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-h")]
    public void OptionLikeFirstArgument_IsIgnored(string arg)
    {
        SubcommandGuard.Validate([arg]).ShouldBeNull();
    }

    [Fact]
    public void EmptyArgs_AreIgnored()
    {
        SubcommandGuard.Validate([]).ShouldBeNull();
    }

    /// <summary>
    /// An option terminates the chain walk, so <c>--help</c> on a group reaches
    /// ConsoleAppFramework rather than being reported as a missing subcommand.
    /// </summary>
    [Theory]
    [InlineData("nav", "--help")]
    [InlineData("seed", "new", "--title", "x")]
    [InlineData("link", "--help")]
    public void OptionAfterAGroup_IsRoutedNormally(params string[] args)
    {
        SubcommandGuard.Validate(args).ShouldBeNull();
    }

    // ---- Drift guards. Both registries are hand-maintained, and a hand-maintained list is
    // exactly how the bench group shipped unreachable (ADO #148-150). These fail the build
    // when the code and the registry disagree, rather than waiting for a user to notice.

    [Fact]
    public void EveryPrefixWithoutABareHandler_ReallyHasNoBareHandler()
    {
        var bare = BareCommandNames();

        foreach (var prefix in SubcommandGuard.PrefixesWithoutBareHandler)
        {
            bare.ShouldNotContain(prefix,
                $"'{prefix}' is listed as having no bare handler, but a handler for it exists. "
                + "Invoking it bare now reports a missing subcommand instead of running.");
            SubcommandGuard.IsGroupPrefix(prefix).ShouldBeTrue(
                $"'{prefix}' is registered as a group prefix but no known command extends it.");
        }
    }

    [Fact]
    public void EveryGroupPrefixWithoutABareHandler_IsRegistered()
    {
        var bare = BareCommandNames();
        var compound = CommandMethods().Select(CommandName).Where(n => n.Contains(' ')).ToHashSet(StringComparer.Ordinal);

        var unregistered = GroupPrefixes()
            // A prefix that is ITSELF a registered compound command has a handler
            // ('workspace area' is [Command("workspace area")]), so invoking it bare works.
            .Where(prefix => !bare.Contains(prefix) && !compound.Contains(prefix))
            .Where(prefix => !SubcommandGuard.PrefixesWithoutBareHandler.Contains(prefix))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        unregistered.ShouldBeEmpty(
            "Group prefixes with no bare handler and no registry entry — invoking one bare "
            + "prints top-level usage and exits 0, which is AB#79 reopened: "
            + string.Join(", ", unregistered));
    }

    [Fact]
    public void EveryPrefixTakingAPositional_ReallyHasABareHandlerWithAnArgument()
    {
        foreach (var prefix in SubcommandGuard.PrefixesTakingPositional)
        {
            var method = BareCommandMethod(prefix);
            method.ShouldNotBeNull(
                $"'{prefix}' is registered as taking a positional but has no bare handler.");

            method.GetParameters()
                .Any(p => p.GetCustomAttributes().Any(a => a.GetType().Name == "ArgumentAttribute"))
                .ShouldBeTrue(
                    $"'{prefix}' is registered as taking a positional, but its handler declares no "
                    + "[Argument]. If that is now true, remove it from PrefixesTakingPositional — "
                    + "otherwise 'twig " + prefix + " typo' silently exits 0 again.");
        }
    }

    /// <summary>
    /// The inverse: any group prefix whose bare handler DOES take an [Argument] must be
    /// registered as taking a positional, or deliberately registered as one where the
    /// subcommand reading wins. Neither list may absorb a prefix silently.
    /// </summary>
    [Fact]
    public void EveryGroupPrefixWhoseBareHandlerTakesAPositional_IsClassified()
    {
        var missing = GroupPrefixes()
            .Where(prefix => !SubcommandGuard.PrefixesTakingPositional.Contains(prefix))
            .Where(prefix => !SubcommandGuard.PrefixesWhereSubcommandWins.Contains(prefix))
            .Where(prefix =>
            {
                var method = BareCommandMethod(prefix);
                return method is not null
                    && method.GetParameters().Any(p =>
                        p.GetCustomAttributes().Any(a => a.GetType().Name == "ArgumentAttribute"));
            })
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        missing.ShouldBeEmpty(
            "Group prefixes whose bare handler takes a positional but appear in neither registry "
            + "— their positional values are now rejected as unknown subcommands: "
            + string.Join(", ", missing));
    }

    /// <summary>
    /// <c>seed</c> is the one prefix where the subcommand reading deliberately beats a real
    /// positional. Pinned by name so removing it is a decision rather than an accident —
    /// dropping it silently restores <c>twig seed bogus</c> creating a seed titled "bogus".
    /// </summary>
    [Fact]
    public void SeedTypo_IsRejectedRatherThanCreatingASeed()
    {
        var error = SubcommandGuard.Validate(["seed", "bogus"]);

        error.ShouldNotBeNull(
            "'twig seed bogus' CREATED a seed titled 'bogus' and exited 0 before AB#79.");
        error.ShouldContain("Unknown subcommand: 'bogus' is not a 'seed' command.");

        // And the canonical route to a title still works.
        SubcommandGuard.Validate(["seed", "new", "bogus"]).ShouldBeNull();
    }

    /// <summary>
    /// Every string that some known command extends by a word — including multi-word prefixes
    /// like "workspace area", which "workspace area add" makes a prefix. Derived from
    /// KnownCommands rather than hardcoded, so a new group is audited the day it is added.
    /// </summary>
    private static IEnumerable<string> GroupPrefixes()
        => GroupedHelp.KnownCommands
            .Where(c => c.Contains(' '))
            .SelectMany(c =>
            {
                var words = c.Split(' ');
                return Enumerable.Range(1, words.Length - 1)
                    .Select(take => string.Join(' ', words.Take(take)));
            })
            .Distinct(StringComparer.Ordinal);

    private static HashSet<string> BareCommandNames()
        => CommandMethods()
            .Select(CommandName)
            .Where(name => !name.Contains(' '))
            .ToHashSet(StringComparer.Ordinal);

    private static MethodInfo? BareCommandMethod(string name)
        => CommandMethods().FirstOrDefault(m => CommandName(m) == name);

    private static IEnumerable<MethodInfo> CommandMethods()
        => typeof(TwigCommands).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

    private static string CommandName(MethodInfo method)
    {
        var attribute = method.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.Name == "CommandAttribute");
        return attribute is null
            ? method.Name.ToLowerInvariant()
            : (string)attribute.ConstructorArguments[0].Value!;
    }
}
