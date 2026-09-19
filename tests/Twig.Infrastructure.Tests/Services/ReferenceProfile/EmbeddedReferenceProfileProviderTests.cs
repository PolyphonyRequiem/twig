using System.Text.Json.Nodes;
using Shouldly;
using Twig.Domain.Services.ReferenceProfile;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services.ReferenceProfile;
using Xunit;

namespace Twig.Infrastructure.Tests.Services.ReferenceProfile;

public sealed class EmbeddedReferenceProfileProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "twig_pin_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Absent_profile_block_leaves_sprint_entry_out_of_scope()
    {
        var provider = LoadProvider("{}");

        provider.ValidatePin().Error.ShouldBe(ReferenceProfileErrors.TwigJsonProfileBlockMissing);
        Evaluate(provider, "Feature").IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("identity", null)]
    [InlineData("identity", "null")]
    [InlineData("identity", "\"\"")]
    [InlineData("identity", "\" \\t \"")]
    [InlineData("profileVersion", null)]
    [InlineData("profileVersion", "null")]
    [InlineData("profileVersion", "\"\"")]
    [InlineData("profileVersion", "\" \\t \"")]
    [InlineData("baseProcessVersion", null)]
    [InlineData("baseProcessVersion", "null")]
    [InlineData("baseProcessVersion", "\"\"")]
    [InlineData("baseProcessVersion", "\" \\t \"")]
    public void Incomplete_pin_refuses_sprint_entry(string field, string? valueJson)
    {
        var document = MatchingDocument();
        var pin = document["profile"]!.AsObject();
        if (valueJson is null)
            pin.Remove(field);
        else
            pin[field] = JsonNode.Parse(valueJson);
        var provider = LoadProvider(document.ToJsonString());

        provider.ValidatePin().Error.ShouldBe(ReferenceProfileErrors.ProfileSchemaInvalid);
        var result = Evaluate(provider, "Feature");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.ProfileSchemaInvalid);
        Evaluate(provider, "Task").IsSuccess.ShouldBeFalse("an incomplete declaration cannot establish even the sprint-tier binding");
    }

    [Fact]
    public void Empty_profile_object_is_not_an_absent_declaration()
    {
        var result = Evaluate(LoadProvider("{\"profile\":{}}"), "Feature");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.ProfileSchemaInvalid);
    }

    [Fact]
    public void Complete_matching_pin_enforces_sprint_tier_and_preserves_backlog_entry()
    {
        var provider = LoadProvider(MatchingDocument().ToJsonString());

        provider.ValidatePin().IsSuccess.ShouldBeTrue();
        var result = Evaluate(provider, "Feature");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(SprintEntryFailure.NotSprintTier);
        Evaluate(provider, "Task").IsSuccess.ShouldBeTrue();
        new SprintEntryPolicy(provider).Evaluate(
            WorkItemType.Parse("Feature").Value, IterationPath.Parse("Project").Value)
            .IsSuccess.ShouldBeTrue();
    }

    private EmbeddedReferenceProfileProvider LoadProvider(string json)
    {
        Directory.CreateDirectory(_root);
        var paths = new TwigPaths(Path.Combine(_root, ".twig"),
            Path.Combine(_root, ".twig", "config"), Path.Combine(_root, ".twig", "cache", "twig.db"));
        File.WriteAllText(paths.RepoConfigPath, json);
        var config = TwigConfiguration.LoadSplit(paths);
        return new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(config));
    }

    private static Twig.Domain.Common.Result Evaluate(EmbeddedReferenceProfileProvider provider, string type) =>
        new SprintEntryPolicy(provider).Evaluate(
            WorkItemType.Parse(type).Value, IterationPath.Parse(@"Project\Sprint 1").Value);

    private static JsonObject MatchingDocument() => new()
    {
        ["profile"] = new JsonObject
        {
            ["identity"] = ProfilePinSources.ShippedIdentity,
            ["profileVersion"] = ProfilePinSources.ShippedProfileVersion,
            ["baseProcessVersion"] = ProfilePinSources.ShippedBaseProcessVersion,
        },
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
