using System.Reflection;
using Shouldly;
using Twig.Commands;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// AB#398 — a stray extra positional must name the quoting fix, not the raw parser error.
///
/// <para>
/// The card described the defect as "one bare word is accepted, the SECOND is rejected".
/// Measured against the real binary at the card's own commit, that was false: <c>note</c>,
/// <c>new</c> and <c>seed chain</c> declared their trailing words as <c>params string[]</c>
/// after the <see cref="System.Threading.CancellationToken"/>, for which ConsoleAppFramework
/// 5.7.13 emits NO positional slot, so <c>twig note hello</c> was rejected too. The fix is
/// therefore in two halves — restore the accepted spelling with a single <c>[Argument]</c>
/// slot, THEN make the remaining refusal honest — and this file asserts both, because a hint
/// suggesting a spelling that does not work is a false green in a helpful tone.
/// </para>
/// </summary>
/// <remarks>
/// Asserts each guard's DISTINCT wording rather than mere non-null. The stray-positional and
/// unknown-subcommand guards both fire on <c>twig seed bogus extra</c>, so a non-null
/// assertion would pass against either one dead — the mutual-masking failure AGENTS.md
/// records for ConflictFixture and which cost two survived mutants on AB#352.
/// </remarks>
public sealed class StrayPositionalGuardTests
{
    // ---- The reported defect: surplus bare words get a message naming the remedy.
    [Theory]
    [InlineData("note|hello|world", "world", "twig note \"hello world\"")]
    [InlineData("note|a|b|c", "b", "twig note \"a b c\"")]
    [InlineData("edit|System.Title|extra", "extra", "twig edit \"System.Title extra\"")]
    [InlineData("seed|chain|alpha|beta", "beta", "twig seed chain \"alpha beta\"")]
    [InlineData("new|task|Write|tests", "tests", "twig new task \"Write tests\"")]
    public void SurplusPositionals_AreRejectedWithAQuotingHint(string argv, string stray, string suggestion)
    {
        var error = StrayPositionalGuard.Validate(argv.Split('|'));

        error.ShouldNotBeNull();
        error.ShouldContain($"unexpected extra argument '{stray}'",
            customMessage: "the message must name the token the user must fix");
        error.ShouldContain($"Did you mean: {suggestion}",
            customMessage: "the hint is the whole value of this card — the refusal already existed");
        error.ShouldNotContain("is not recognized",
            customMessage: "the raw generated-parser wording is what this guard replaces");
    }

    /// <summary>
    /// The suggested spelling must be the one that WORKS. A hint pointing at a second failure
    /// is the AB#352/AB#79 defect wearing a helpful tone, and it is the reason this card could
    /// not be shipped as a message change alone.
    /// </summary>
    [Fact]
    public void TheSuggestedSpelling_IsWithinTheCommandsAcceptedArity()
    {
        foreach (var (chain, allowed) in StrayPositionalGuard.Arity)
        {
            var words = Enumerable.Range(0, allowed + 2).Select(i => $"w{i}").ToArray();
            var error = StrayPositionalGuard.Validate([.. chain.Split(' '), .. words]);

            error.ShouldNotBeNull($"'{chain}' with {allowed + 2} positionals exceeds its arity");
            var suggested = error[(error.IndexOf("Did you mean: ", StringComparison.Ordinal) + 14)..];
            // Everything after the chain collapses into ONE quoted value, so the suggestion
            // passes exactly `allowed` positionals — which the parser accepts.
            suggested.Count(c => c == '"').ShouldBe(2,
                customMessage: $"'{chain}' must suggest a single quoted value, got: {suggested}");
        }
    }

    // ---- Exactly-at-arity is not a defect. The off-by-one direction of this guard is the
    // one that produces a false RED on a working command line.
    [Theory]
    [InlineData("note|hello world")]
    [InlineData("note")]
    [InlineData("edit|System.Title")]
    [InlineData("seed|chain|design api,build api")]
    [InlineData("new|task")]
    [InlineData("new|task|Write tests")]
    public void PositionalsWithinArity_AreRoutedNormally(string argv)
    {
        var args = argv.Split('|');

        StrayPositionalGuard.Validate(args).ShouldBeNull(
            $"'twig {string.Join(' ', args)}' is a working spelling — rejecting it would be worse than the bug.");
    }

    // ---- Named spellings were never broken and must stay that way. These are the card's
    // acceptance item 2, and they are the reason the fix ADDS a positional rather than
    // replacing the option.
    [Theory]
    [InlineData("note|--text|hello world")]
    [InlineData("note|--text|hello world|--id|398")]
    [InlineData("edit|--field|System.Title")]
    [InlineData("new|--title|hello world|--type|Task")]
    [InlineData("seed|chain|--parent|383")]
    [InlineData("new|--type|Task|--field|a=1|--field|b=2")]
    public void NamedSpellings_AreRoutedNormally(string argv)
    {
        var args = argv.Split('|');

        StrayPositionalGuard.Validate(args).ShouldBeNull(
            $"'twig {string.Join(' ', args)}' is a documented spelling that was never broken.");
    }

    /// <summary>
    /// AB#352's lesson in reverse: a usage error for a request that SUCCEEDED is a false RED,
    /// and a false red corrodes an exit code exactly as fast as a false green.
    /// </summary>
    [Theory]
    [InlineData("note|hello|world|--help")]
    [InlineData("new|task|a|b|-h")]
    [InlineData("seed|chain|a|b|--help")]
    public void HelpRequests_AreNeverAUsageError(string argv)
    {
        StrayPositionalGuard.Validate(argv.Split('|')).ShouldBeNull(
            "a help request succeeded; failing it would be a false RED.");
    }

    // ---- Commands outside the registry keep the generated parser's message, which is
    // correct for them.
    [Theory]
    [InlineData("init|myorg|myproject|extra")]  // two UNRELATED ids, not a phrase
    [InlineData("show|1|2")]
    [InlineData("update|title|foo|bar")]
    [InlineData("link|parent|383|384")]
    public void CommandsOutsideTheRegistry_AreRoutedNormally(string argv)
    {
        var args = argv.Split('|');

        StrayPositionalGuard.Validate(args).ShouldBeNull(
            $"'twig {string.Join(' ', args)}' has no quoting remedy — suggesting one would be confidently wrong.");
    }

    [Fact]
    public void Init_IsDeliberatelyAbsent_BecauseQuotingIsNotItsRemedy()
        => StrayPositionalGuard.Arity.ShouldNotContainKey("init",
            customMessage: "init takes org + project — two unrelated identifiers, not a phrase that "
                + "lost its quotes. A quoting hint there would be a confidently wrong remedy.");

    // ---- Drift guards. Arity is hand-maintained, and a hand-maintained list is exactly how
    // the whole bench group shipped unreachable (ADO #148-150, 3,072 CLI tests green).
    // These fail the BUILD when a command's [Argument] count and the registry disagree.

    [Fact]
    public void EveryRegisteredChain_IsAKnownCommand()
    {
        foreach (var chain in StrayPositionalGuard.Arity.Keys)
            GroupedHelp.KnownCommands.ShouldContain(chain,
                customMessage: $"'{chain}' is registered in Arity but is not a known command — "
                    + "a stale entry silently guards nothing.");
    }

    [Fact]
    public void EveryRegisteredArity_MatchesTheCommandsArgumentCount()
    {
        var sweep = 0;
        foreach (var (chain, declared) in StrayPositionalGuard.Arity)
        {
            var method = ResolveCommandMethod(chain);
            method.ShouldNotBeNull($"could not resolve a TwigCommands method for '{chain}'");

            var actual = method!.GetParameters()
                .Count(p => p.GetCustomAttributes()
                    .Any(a => a.GetType().Name is "ArgumentAttribute"));

            actual.ShouldBe(declared,
                customMessage: $"'{chain}' declares {actual} [Argument] parameter(s) but Arity says "
                    + $"{declared}. Update StrayPositionalGuard.Arity — a stale count either "
                    + "suppresses the hint or fires it on a working command line.");
            sweep++;
        }

        sweep.ShouldBe(StrayPositionalGuard.Arity.Count,
            customMessage: "the sweep must cover every entry — a vacuous pass proves nothing.");
        sweep.ShouldBeGreaterThan(0, "an empty sweep is not a passing guard.");
    }

    /// <summary>
    /// The inverse direction: a command that GAINS a positional slot and free-text last
    /// argument should be considered for the registry. Pins the three the card named plus the
    /// two the drift sweep found, so removing one is a deliberate act rather than an omission.
    /// </summary>
    [Theory]
    [InlineData("note", 1)]
    [InlineData("edit", 1)]
    [InlineData("seed chain", 1)]
    [InlineData("new", 2)]
    public void CommandsFixedByThisCard_AcceptTheirPositionals(string chain, int expected)
    {
        var method = ResolveCommandMethod(chain);
        method.ShouldNotBeNull();

        method!.GetParameters()
            .Count(p => p.GetCustomAttributes().Any(a => a.GetType().Name is "ArgumentAttribute"))
            .ShouldBe(expected,
                customMessage: $"'{chain}' lost its [Argument] slot — the quoted spelling this "
                    + "card restored would be rejected again, and the hint would point at it.");
    }

    /// <summary>
    /// The trailing <c>params string[]</c> shape emitted no positional slot at all and is what
    /// made every bare word a parse error. Its absence is the fix; assert it cannot return.
    /// </summary>
    [Fact]
    public void NoCommand_DeclaresATrailingParamsArray()
    {
        var offenders = typeof(TwigCommands)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetParameters().Any(p =>
                p.GetCustomAttributes().Any(a => a.GetType().Name is "ParamArrayAttribute")
                || (p.Position == m.GetParameters().Length - 1 && p.IsDefined(typeof(ParamArrayAttribute)))))
            .Select(m => m.Name)
            .ToList();

        offenders.ShouldBeEmpty(
            "a `params string[]` after the CancellationToken emits NO positional slot in "
            + "ConsoleAppFramework 5.7.13, so every bare word becomes a parse error. That shape "
            + "is what AB#398 removed — see TwigCommands.SplitSeedTitles.");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("alpha", "alpha")]
    [InlineData("alpha,beta", "alpha|beta")]
    [InlineData("design api, build api", "design api|build api")]
    [InlineData("alpha,,beta", "alpha|beta")]
    public void SplitSeedTitles_SplitsOnCommasAndTrims(string? input, string expected)
    {
        var want = expected.Length == 0 ? [] : expected.Split('|');

        TwigCommands.SplitSeedTitles(input).ShouldBe(want);
    }

    /// <summary>
    /// Inherited from the deleted <c>JoinTrailingTextTests</c>: the NAMED option wins over the
    /// positional, so <c>--text</c> keeps overriding a bare word. That helper joined trailing
    /// tokens the parser could never deliver — 5 green tests over an unreachable path — but its
    /// precedence rule survived into the <c>text ?? textArg</c> expressions, so it is re-pinned
    /// here at the seam that actually runs rather than dropped with the helper.
    /// </summary>
    [Theory]
    [InlineData("Note", "text", "textArg")]
    [InlineData("New", "title", "titleArg")]
    [InlineData("New", "type", "typeArg")]
    [InlineData("Edit", "field", "fieldArg")]
    [InlineData("Init", "org", "orgArg")]
    [InlineData("Init", "project", "projectArg")]
    public void NamedOption_AndItsPositionalTwin_BothExist(string method, string named, string positional)
    {
        var parameters = typeof(TwigCommands)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .First(m => m.Name == method)
            .GetParameters();

        parameters.ShouldContain(p => p.Name == named,
            customMessage: $"{method} lost its --{named} option; the named spelling was never broken "
                + "and removing it would be a regression, not a fix.");

        var arg = parameters.FirstOrDefault(p => p.Name == positional);
        arg.ShouldNotBeNull($"{method} lost its positional '{positional}'");
        arg!.GetCustomAttributes().ShouldContain(a => a.GetType().Name == "ArgumentAttribute",
            customMessage: $"{positional} must carry [Argument] or the quoted spelling is rejected again.");
    }

    private static MethodInfo? ResolveCommandMethod(string chain)
    {
        var methods = typeof(TwigCommands)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        // A [Command("seed chain")] attribute names the chain explicitly; otherwise the method
        // name is the verb. Matching on the attribute FIRST is what keeps `seed chain` from
        // resolving to `Seed`.
        foreach (var method in methods)
        {
            var attr = method.GetCustomAttributes()
                .FirstOrDefault(a => a.GetType().Name is "CommandAttribute");
            var name = attr?.GetType().GetProperty("Command")?.GetValue(attr) as string;
            if (name == chain)
                return method;
        }

        return methods.FirstOrDefault(m =>
            string.Equals(m.Name, chain.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
    }
}
