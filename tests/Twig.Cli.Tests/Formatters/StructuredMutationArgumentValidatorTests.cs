using Shouldly;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

public sealed class StructuredMutationArgumentValidatorTests
{
    [Fact]
    public void OnlyTheExplicitLocalFamilyAdmitsCanonicalUuidSources()
    {
        const string uuid = "019a0000-0000-7000-8000-000000000001";
        StructuredMutationArgumentValidator.Validate(["link", "local", "related", "42", "--id", uuid, "-o", "json"]).ShouldBeNull();
        StructuredMutationArgumentValidator.Validate(["link", "local", "unrelate", "-12", "--id=-13", "-o", "json"]).ShouldBeNull();
        AssertError(["link", "related", "42", "--id", uuid, "-o", "json"], "requires an explicit --id");
        AssertError(["update", "title", "text", "--id", uuid, "-o", "json"], "requires an explicit --id");
        AssertError(["link", "local", "related", uuid, uuid, "-o", "json"], "two-GUID positional");
        AssertError(["link", "local", "related", uuid, "--id", "42", "--id", "43", "-o", "json"], "Specify --id once");
        AssertError(["link", "local", "related", uuid, "-o", "json"], "--id");
    }

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
    public void NavigationIsRetiredBeforeScopeValidation(string command, params string[] arguments)
    {
        CoreMutationAdmissionGuard.ValidateCatalog([command, .. arguments, "-o", "json"])
            .ShouldNotBeNull().ShouldContain("retired");
    }

    [Fact]
    public void ExplicitListsRemainUsableAndHistoryIsRetired()
    {
        StructuredMutationArgumentValidator.Validate(["query", "--wiql", "select [System.Id] from WorkItems", "-o", "json"]).ShouldBeNull();
        StructuredMutationArgumentValidator.Validate(["show-batch", "42,43", "-o", "json"]).ShouldBeNull();
        StructuredMutationArgumentValidator.Validate(["tree", "--all", "-o", "json"]).ShouldBeNull();
        foreach (var format in new[] { "json", "minimal", "json-full" })
            CoreMutationAdmissionGuard.ValidateCatalog(["nav", "history", "-o", format]).ShouldNotBeNull().ShouldContain("retired");
        AssertError(["seed", "publish", "-6", "-o", "json"], "reviewed proposal");
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
        CoreMutationAdmissionGuard.ValidateCatalog(["states", "-o", format]).ShouldNotBeNull().ShouldContain("twig process <type>");
    }

    [Theory]
    [InlineData("show")]
    [InlineData("tree")]
    public void ReadTargetTwinsAreUnambiguousWithoutHumanFallback(string command)
    {
        foreach (var target in new[]
        {
            new[] { "42" }, new[] { "--id", "42" }, new[] { "--id=42" },
            new[] { "42", "--id", "42" }, new[] { "-42" }, new[] { "--id", "-42" },
        })
            StructuredMutationArgumentValidator.Validate([command, .. target, "-o", "json"]).ShouldBeNull();
        AssertError([command], "requires an explicit work-item ID");
        AssertError([command, "-o", "human"], "requires an explicit work-item ID");
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
        CoreMutationAdmissionGuard.ValidateCatalog(["states"]).ShouldNotBeNull().ShouldContain("twig process <type>");
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
        CoreMutationAdmissionGuard.ValidateCatalog(["set", "42", "-o", format]).ShouldNotBeNull().ShouldContain("retired");
    }

    [Fact]
    public void HumanMutationsRequireExplicitScopeToo()
    {
        AssertError(["state", "Done", "-o", "human"], "requires an explicit --id");
        StructuredMutationArgumentValidator.Validate(["state", "Done", "--id", "42", "-o", "human"]).ShouldBeNull();
        CoreMutationAdmissionGuard.ValidateCatalog(["set", "42", "-o", "human"]).ShouldNotBeNull().ShouldContain("retired");
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
        CoreMutationAdmissionGuard.ValidateCatalog(["new", "--title", "example", "--type", "Task", "--set", "-o", "json"])
            .ShouldNotBeNull().ShouldContain("retired");
    }

    private static void AssertError(IReadOnlyList<string> args, string expectedMessage)
    {
        var error = StructuredMutationArgumentValidator.Validate(args);
        error.ShouldNotBeNull();
        error.ShouldContain(expectedMessage);
    }
}
