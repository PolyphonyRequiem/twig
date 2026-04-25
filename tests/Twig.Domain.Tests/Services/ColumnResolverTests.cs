using Shouldly;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class ColumnResolverTests
{
    private static readonly IReadOnlyList<FieldDefinition> SampleDefinitions = new List<FieldDefinition>
    {
        new("Microsoft.VSTS.Common.Priority", "Priority", "integer", true),
        new("Microsoft.VSTS.Scheduling.StoryPoints", "Story Points", "double", false),
        new("System.Tags", "Tags", "string", false),
        new("System.Description", "Description", "html", false),
        new("Microsoft.VSTS.Common.ValueArea", "Value Area", "string", false),
    };

    [Fact]
    public void Resolve_ConfiguredColumns_UsesExactList()
    {
        var configured = new List<string>
        {
            "System.Tags",
            "Microsoft.VSTS.Common.Priority",
        };

        var result = ColumnResolver.Resolve(
            profiles: Array.Empty<FieldProfile>(),
            definitions: SampleDefinitions,
            configuredColumns: configured);

        result.Count.ShouldBe(2);
        result[0].ReferenceName.ShouldBe("System.Tags");
        result[0].DisplayName.ShouldBe("Tags");
        result[1].ReferenceName.ShouldBe("Microsoft.VSTS.Common.Priority");
        result[1].DisplayName.ShouldBe("Priority");
        result[1].DataType.ShouldBe("integer");
    }

    [Fact]
    public void Resolve_ConfiguredColumns_DerivesDisplayNameWhenDefNotFound()
    {
        var configured = new List<string> { "Custom.MyField" };

        var result = ColumnResolver.Resolve(
            profiles: Array.Empty<FieldProfile>(),
            definitions: SampleDefinitions,
            configuredColumns: configured);

        result.Count.ShouldBe(1);
        result[0].DisplayName.ShouldBe("My Field");
        result[0].DataType.ShouldBe("string");
    }

    [Fact]
    public void Resolve_AutoDiscovery_RespectsThreshold()
    {
        var profiles = new List<FieldProfile>
        {
            new("Microsoft.VSTS.Common.Priority", 0.8, new[] { "1", "2" }),
            new("System.Tags", 0.5, new[] { "tag1" }),
            new("System.Description", 0.3, new[] { "desc" }),
        };

        var result = ColumnResolver.Resolve(
            profiles: profiles,
            definitions: SampleDefinitions,
            configuredColumns: null,
            fillRateThreshold: 0.4);

        result.Count.ShouldBe(2);
        result[0].ReferenceName.ShouldBe("Microsoft.VSTS.Common.Priority");
        result[1].ReferenceName.ShouldBe("System.Tags");
        // Description (0.3) is below threshold
    }

    [Fact]
    public void Resolve_AutoDiscovery_RespectsMaxExtraColumns()
    {
        var profiles = new List<FieldProfile>
        {
            new("Microsoft.VSTS.Common.Priority", 0.9, new[] { "1" }),
            new("System.Tags", 0.8, new[] { "tag" }),
            new("Microsoft.VSTS.Scheduling.StoryPoints", 0.7, new[] { "5" }),
            new("Microsoft.VSTS.Common.ValueArea", 0.6, new[] { "Business" }),
        };

        var result = ColumnResolver.Resolve(
            profiles: profiles,
            definitions: SampleDefinitions,
            configuredColumns: null,
            fillRateThreshold: 0.4,
            maxExtraColumns: 2);

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void Resolve_JsonOutput_IncludesAllAboveThreshold()
    {
        var profiles = new List<FieldProfile>
        {
            new("Microsoft.VSTS.Common.Priority", 0.9, new[] { "1" }),
            new("System.Tags", 0.8, new[] { "tag" }),
            new("Microsoft.VSTS.Scheduling.StoryPoints", 0.7, new[] { "5" }),
            new("Microsoft.VSTS.Common.ValueArea", 0.6, new[] { "Business" }),
        };

        var result = ColumnResolver.Resolve(
            profiles: profiles,
            definitions: SampleDefinitions,
            configuredColumns: null,
            fillRateThreshold: 0.4,
            maxExtraColumns: 2,
            isJsonOutput: true);

        // JSON ignores maxExtraColumns cap
        result.Count.ShouldBe(4);
    }

    [Fact]
    public void Resolve_EmptyProfiles_ReturnsEmpty()
    {
        var result = ColumnResolver.Resolve(
            profiles: Array.Empty<FieldProfile>(),
            definitions: SampleDefinitions,
            configuredColumns: null);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void DeriveDisplayName_StandardRefNames()
    {
        ColumnResolver.DeriveDisplayName("Microsoft.VSTS.Scheduling.StoryPoints").ShouldBe("Story Points");
        ColumnResolver.DeriveDisplayName("Microsoft.VSTS.Common.Priority").ShouldBe("Priority");
        ColumnResolver.DeriveDisplayName("System.Tags").ShouldBe("Tags");
        ColumnResolver.DeriveDisplayName("Custom.MyCustomField").ShouldBe("My Custom Field");
    }

    [Fact]
    public void DeriveDisplayName_NoDotSegments_ReturnsAsIs()
    {
        ColumnResolver.DeriveDisplayName("SimpleField").ShouldBe("Simple Field");
    }

    [Fact]
    public void DeriveDisplayName_AllCaps_NoExtraSpaces()
    {
        // "ID" → "ID" (no space insertion between consecutive uppercase)
        ColumnResolver.DeriveDisplayName("System.ID").ShouldBe("ID");
    }

    [Fact]
    public void Resolve_EmptyDefinitions_UsesDerivedNames()
    {
        var profiles = new List<FieldProfile>
        {
            new("Microsoft.VSTS.Scheduling.StoryPoints", 0.8, new[] { "5" }),
        };

        var result = ColumnResolver.Resolve(
            profiles: profiles,
            definitions: Array.Empty<FieldDefinition>(),
            configuredColumns: null);

        result.Count.ShouldBe(1);
        result[0].DisplayName.ShouldBe("Story Points");
        result[0].DataType.ShouldBe("string"); // default when no definition
    }
}
