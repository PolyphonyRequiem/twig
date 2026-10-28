using Shouldly;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

public sealed class StructuredMutationArgumentValidatorTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("json-full")]
    [InlineData("json-compact")]
    [InlineData("minimal")]
    [InlineData("ids")]
    public void ActiveItemMutation_RequiresExplicitIdForStructuredOutput(string format)
    {
        AssertError(["state", "Done", "-o", format], "requires an explicit --id");
    }

    [Theory]
    [InlineData("--id", "42")]
    [InlineData("--id=42", null)]
    public void ActiveItemMutation_AllowsExplicitId(string idOption, string? idValue)
    {
        var args = idValue is null
            ? new[] { "state", "Done", idOption, "-o", "json" }
            : new[] { "state", "Done", idOption, idValue, "-o", "json" };

        StructuredMutationArgumentValidator.Validate(args).ShouldBeNull();
    }

    [Theory]
    [InlineData("update", "System.Description", "--file", "d.html")]
    [InlineData("state", "Done")]
    [InlineData("note", "--text", "finished")]
    [InlineData("link", "parent", "42")]
    public void ActiveItemMutation_AllowsExplicitNegativeSeedAlias(string command, params string[] arguments)
    {
        StructuredMutationArgumentValidator.Validate([command, .. arguments, "--id", "-6", "-o", "json"])
            .ShouldBeNull();
    }

    [Theory]
    [InlineData("--id")]
    [InlineData("--id=")]
    [InlineData("--id=invalid")]
    [InlineData("--id=0")]
    public void MissingOrInvalidIdValue_DoesNotCountAsExplicitScope(string idOption)
    {
        AssertError(["state", "Done", idOption, "-o", "json"], "requires an explicit --id");
    }

    [Fact]
    public void BatchWithExplicitIds_DoesNotUseActiveItem()
    {
        StructuredMutationArgumentValidator.Validate(["batch", "--state", "Done", "--ids", "1,2", "-o", "json"])
            .ShouldBeNull();
        AssertError(
            ["batch", "--state", "Done", "--ids", "-o", "json"],
            "requires an explicit --id");
    }

    [Fact]
    public void SaveRequiresExplicitScope()
    {
        AssertError(["save", "-o", "json"], "requires an explicit work-item ID or --all");
        AssertError(["save", "--all=false", "-o", "json"], "requires an explicit work-item ID or --all");
        StructuredMutationArgumentValidator.Validate(["save", "123", "-o", "json"]).ShouldBeNull();
        StructuredMutationArgumentValidator.Validate(["save", "--all", "-o", "json"]).ShouldBeNull();
    }

    [Theory]
    [InlineData("state", "Done")]
    [InlineData("note", "--text", "finished")]
    [InlineData("update", "System.Title", "new title")]
    [InlineData("patch", "--json", "{}")]
    [InlineData("link", "artifact", "https://example.test")]
    public void ActiveTargetingMutationsRequireId(string command, params string[] arguments)
    {
        AssertError([command, .. arguments, "-o", "json"], "requires an explicit --id");
    }

    [Fact]
    public void OptionValuesAreNotMistakenForExplicitScope()
    {
        AssertError(["note", "--text", "--id", "-o", "json"], "requires an explicit --id");
        AssertError(
            ["seed", "new", "--title", "--no-parent", "-o", "json"],
            "--parent <id> or --no-parent");
        StructuredMutationArgumentValidator.Validate(["new", "--title", "--set", "-o", "json"])
            .ShouldBeNull();
    }

    [Theory]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("next")]
    [InlineData("prev")]
    [InlineData("nav", "back")]
    [InlineData("nav", "fore")]
    [InlineData("seed", "publish", "123")]
    public void ActiveContextChangesAreRejectedForStructuredOutput(string command, params string[] arguments)
    {
        AssertError([command, .. arguments, "-o", "json"], "cannot be used with structured output");
    }

    [Fact]
    public void ExplicitListAndHistoryScopesRemainUsableWithoutId()
    {
        StructuredMutationArgumentValidator.Validate(["query", "--wiql", "select [System.Id] from WorkItems", "-o", "json"]).ShouldBeNull();
        StructuredMutationArgumentValidator.Validate(["show-batch", "42,43", "-o", "json"]).ShouldBeNull();
        StructuredMutationArgumentValidator.Validate(["tree", "--all", "-o", "json"]).ShouldBeNull();
        StructuredMutationArgumentValidator.Validate(["nav", "history", "-o", "json"]).ShouldBeNull();
        StructuredMutationArgumentValidator.Validate(["nav", "history", "-o", "minimal"]).ShouldBeNull();
        AssertError(
            ["nav", "history", "-o", "json-full"],
            "supports only -o json or -o minimal");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("json-full")]
    [InlineData("json-compact")]
    [InlineData("minimal")]
    [InlineData("ids")]
    public void StructuredSingleReadsRequireAnExplicitTarget(string format)
    {
        AssertError(["show", "-o", format], "requires an explicit work-item ID");
        AssertError(["tree", "-o", format], "requires an explicit work-item ID");
        AssertError(["states", "-o", format], "twig process <type>");
    }

    [Theory]
    [InlineData("show")]
    [InlineData("tree")]
    public void ReadTargetTwinsAndHumanFallbackAreUnambiguous(string command)
    {
        foreach (var target in new[]
        {
            new[] { "42" }, new[] { "--id", "42" }, new[] { "--id=42" },
            new[] { "42", "--id", "42" }, new[] { "-42" }, new[] { "--id", "-42" },
        })
            StructuredMutationArgumentValidator.Validate([command, .. target, "-o", "json"]).ShouldBeNull();
        StructuredMutationArgumentValidator.Validate([command]).ShouldBeNull();
        StructuredMutationArgumentValidator.Validate([command, "-o", "human"]).ShouldBeNull();
        AssertError([command, "42", "--id", "43", "-o", "json"], "Conflicting work-item targets");
        AssertError([command, "42", "--id", "43"], "Conflicting work-item targets");
        AssertError([command, "--id", "42", "--id=43", "-o", "json"], "Conflicting work-item targets");
        AssertError([command, "--id=0", "-o", "json"], "nonzero integer");
        AssertError([command, "--id=invalid", "-o", "json"], "nonzero integer");
        AssertError([command, "--id", "-o", "json"], "nonzero integer");
    }

    [Fact]
    public void ReadOptionValuesAndTerminatorsCannotSmuggleAnActiveTarget()
    {
        AssertError(["tree", "--depth", "42", "-o", "json"], "requires an explicit work-item ID");
        AssertError(["show", "--fields", "42", "-o", "json"], "requires an explicit work-item ID");
        AssertError(["show", "--fields", "--id", "-o", "json"], "requires an explicit work-item ID");
        AssertError(["show", "--sections", "42", "-o", "json"], "requires an explicit work-item ID");
        AssertError(["show", "-o", "json", "--", "--id", "42"], "requires an explicit work-item ID");
        StructuredMutationArgumentValidator.Validate(["show", "-o", "json", "--", "42"]).ShouldBeNull();
        StructuredMutationArgumentValidator.Validate(["show", "--local-id", "01900000-0000-7000-8000-000000000001", "-o", "json"]).ShouldBeNull();
        AssertError(["show", "42", "--local-id", "01900000-0000-7000-8000-000000000001", "-o", "json"],
            "Conflicting work-item targets");
        StructuredMutationArgumentValidator.Validate(["states"]).ShouldBeNull();
    }

    [Fact]
    public void FileScopedProposalApplyDoesNotDependOnActiveItem()
    {
        StructuredMutationArgumentValidator.Validate(["proposal", "apply", "--file", "proposal.json", "--confirm", "digest", "-o", "json"])
            .ShouldBeNull();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("minimal")]
    public void SetIsRejectedForStructuredOutput(string format)
    {
        AssertError(["set", "42", "-o", format], "cannot be used with structured output");
    }

    [Fact]
    public void HumanMutationsRemainUnchanged()
    {
        StructuredMutationArgumentValidator.Validate(["state", "Done", "-o", "human"]).ShouldBeNull();
        StructuredMutationArgumentValidator.Validate(["set", "42", "-o", "human"]).ShouldBeNull();
    }

    [Fact]
    public void StructuredSeedCreationRequiresExplicitParentChoice()
    {
        AssertError(
            ["seed", "new", "--title", "example", "--type", "Task", "-o", "json"],
            "--parent <id> or --no-parent");
        StructuredMutationArgumentValidator.Validate(["seed", "new", "--title", "example", "--type", "Task", "--no-parent", "-o", "json"])
            .ShouldBeNull();
        AssertError(
            ["seed", "new", "--title", "example", "--parent", "-o", "json"],
            "--parent <id> or --no-parent");
        AssertError(["seed", "chain", "--title", "example", "-o", "json"], "--parent <id>");
    }

    [Fact]
    public void NewSetIsRejectedForStructuredOutput()
    {
        AssertError(
            ["new", "--title", "example", "--type", "Task", "--set", "-o", "json"],
            "changes the active work item");
    }

    private static void AssertError(IReadOnlyList<string> args, string expectedMessage)
    {
        var error = StructuredMutationArgumentValidator.Validate(args);
        error.ShouldNotBeNull();
        error.ShouldContain(expectedMessage);
    }
}
