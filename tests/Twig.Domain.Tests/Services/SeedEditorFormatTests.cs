using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class SeedEditorFormatTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Helper field definitions
    // ═══════════════════════════════════════════════════════════════

    private static readonly List<FieldDefinition> StandardFields = new()
    {
        new FieldDefinition("System.Title", "Title", "String", false),
        new FieldDefinition("System.Description", "Description", "Html", false),
        new FieldDefinition("Microsoft.VSTS.Common.Priority", "Priority", "Integer", false),
        new FieldDefinition("Microsoft.VSTS.Scheduling.Effort", "Effort", "Double", false),
        new FieldDefinition("System.Tags", "Tags", "String", false),
    };

    private static readonly List<FieldDefinition> FieldsWithReadOnly = new()
    {
        new FieldDefinition("System.Title", "Title", "String", false),
        new FieldDefinition("System.Description", "Description", "Html", false),
        new FieldDefinition("System.Id", "ID", "Integer", true),
        new FieldDefinition("System.Rev", "Rev", "Integer", true),
        new FieldDefinition("System.CreatedDate", "Created Date", "DateTime", true),
        new FieldDefinition("Microsoft.VSTS.Common.Priority", "Priority", "Integer", false),
    };

    private static readonly List<FieldDefinition> FieldsWithExcludedSystem = new()
    {
        new FieldDefinition("System.Title", "Title", "String", false),
        new FieldDefinition("System.Description", "Description", "Html", false),
        new FieldDefinition("System.CreatedDate", "Created Date", "DateTime", false), // writable but excluded
        new FieldDefinition("System.ChangedDate", "Changed Date", "DateTime", false), // writable but excluded
        new FieldDefinition("System.Watermark", "Watermark", "Integer", false),       // writable but excluded
        new FieldDefinition("System.BoardColumn", "Board Column", "String", false),    // writable but excluded
        new FieldDefinition("Microsoft.VSTS.Common.Priority", "Priority", "Integer", false),
    };

    // ═══════════════════════════════════════════════════════════════
    //  Generate tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Generate_IncludesCommentHeader()
    {
        var seed = new WorkItemBuilder(-1, "My Task").AsSeed().Build();
        var result = SeedEditorFormat.Generate(seed, StandardFields);

        result.ShouldContain("## Seed editor");
        result.ShouldContain("## Run 'twig refresh'");
    }

    [Fact]
    public void Generate_TitleFirst_DescriptionSecond_ThenAlphabetical()
    {
        var seed = new WorkItemBuilder(-1, "Test Seed").AsSeed().Build();
        var result = SeedEditorFormat.Generate(seed, StandardFields);

        var titleIdx = result.IndexOf("# Title");
        var descIdx = result.IndexOf("# Description");
        var effortIdx = result.IndexOf("# Effort");
        var priorityIdx = result.IndexOf("# Priority");
        var tagsIdx = result.IndexOf("# Tags");

        titleIdx.ShouldBeGreaterThan(-1);
        descIdx.ShouldBeGreaterThan(titleIdx);
        effortIdx.ShouldBeGreaterThan(descIdx);
        priorityIdx.ShouldBeGreaterThan(effortIdx);
        tagsIdx.ShouldBeGreaterThan(priorityIdx);
    }

    [Fact]
    public void Generate_PopulatesValuesFromSeedFields()
    {
        var seed = new WorkItemBuilder(-1, "My Title")
            .AsSeed()
            .WithField("System.Description", "A description")
            .WithField("Microsoft.VSTS.Common.Priority", "2")
            .Build();

        var result = SeedEditorFormat.Generate(seed, StandardFields);

        result.ShouldContain("My Title");
        result.ShouldContain("A description");
        result.ShouldContain("2");
    }

    [Fact]
    public void Generate_ExcludesReadOnlyFields()
    {
        var seed = new WorkItemBuilder(-1, "Test").AsSeed().Build();
        var result = SeedEditorFormat.Generate(seed, FieldsWithReadOnly);

        result.ShouldNotContain("# ID");
        result.ShouldNotContain("# Rev");
        result.ShouldNotContain("# Created Date");
        result.ShouldContain("# Title");
        result.ShouldContain("# Priority");
    }

    [Fact]
    public void Generate_ExcludesSystemInternalFields()
    {
        var seed = new WorkItemBuilder(-1, "Test").AsSeed().Build();
        var result = SeedEditorFormat.Generate(seed, FieldsWithExcludedSystem);

        result.ShouldNotContain("# Created Date");
        result.ShouldNotContain("# Changed Date");
        result.ShouldNotContain("# Watermark");
        result.ShouldNotContain("# Board Column");
        result.ShouldContain("# Title");
        result.ShouldContain("# Priority");
    }

    [Fact]
    public void Generate_EmptyFieldDefinitions_ShowsTitleAndDescriptionWithHint()
    {
        var seed = new WorkItemBuilder(-1, "Fallback Seed")
            .AsSeed()
            .WithField("System.Description", "Some desc")
            .Build();

        var result = SeedEditorFormat.Generate(seed, Array.Empty<FieldDefinition>());

        result.ShouldContain("## No field definitions found");
        result.ShouldContain("# Title");
        result.ShouldContain("Fallback Seed");
        result.ShouldContain("# Description");
        result.ShouldContain("Some desc");
    }

    [Fact]
    public void Generate_EmptyFieldDefinitions_NoDescription_StillShowsSection()
    {
        var seed = new WorkItemBuilder(-1, "Just Title").AsSeed().Build();
        var result = SeedEditorFormat.Generate(seed, Array.Empty<FieldDefinition>());

        result.ShouldContain("# Title");
        result.ShouldContain("Just Title");
        result.ShouldContain("# Description");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parse tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_SingleLineValues()
    {
        var content = """
            ## Comment line
            # Title
            My work item

            # Priority
            2
            """;

        var result = SeedEditorFormat.Parse(content, StandardFields);

        result["System.Title"].ShouldBe("My work item");
        result["Microsoft.VSTS.Common.Priority"].ShouldBe("2");
    }

    [Fact]
    public void Parse_MultiLineValues()
    {
        var content = """
            # Title
            My Title

            # Description
            Line one
            Line two
            Line three

            # Priority
            3
            """;

        var result = SeedEditorFormat.Parse(content, StandardFields);

        result["System.Title"].ShouldBe("My Title");
        result["System.Description"].ShouldNotBeNull();
        result["System.Description"]!.ShouldContain("Line one");
        result["System.Description"]!.ShouldContain("Line two");
        result["System.Description"]!.ShouldContain("Line three");
        result["Microsoft.VSTS.Common.Priority"].ShouldBe("3");
    }

    [Fact]
    public void Parse_IgnoresCommentLines()
    {
        var content = """
            ## Seed editor — edit fields below. Lines starting with ## are ignored.
            ## Run 'twig refresh' to sync field definitions from ADO.

            # Title
            Test
            ## This is a mid-section comment
            
            # Priority
            1
            """;

        var result = SeedEditorFormat.Parse(content, StandardFields);

        result["System.Title"].ShouldBe("Test");
        result["Microsoft.VSTS.Common.Priority"].ShouldBe("1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Parse_NullOrEmptyContent_ReturnsEmptyDictionary(string? content)
    {
        var result = SeedEditorFormat.Parse(content!, StandardFields);
        result.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_MissingFields_ReturnsOnlyPresentFields()
    {
        var content = """
            # Title
            Only title provided
            """;

        var result = SeedEditorFormat.Parse(content, StandardFields);

        result.ShouldContainKey("System.Title");
        result["System.Title"].ShouldBe("Only title provided");
        result.ShouldNotContainKey("System.Description");
        result.ShouldNotContainKey("Microsoft.VSTS.Common.Priority");
    }

    [Fact]
    public void Parse_EmptyFieldValue_ReturnsNull()
    {
        var content = """
            # Title
            My Title

            # Description

            # Priority
            2
            """;

        var result = SeedEditorFormat.Parse(content, StandardFields);

        result["System.Title"].ShouldBe("My Title");
        result["System.Description"].ShouldBeNull();
        result["Microsoft.VSTS.Common.Priority"].ShouldBe("2");
    }

    [Fact]
    public void Parse_UnknownDisplayName_IsIgnored()
    {
        var content = """
            # Title
            Test

            # Unknown Field
            Some value

            # Priority
            1
            """;

        var result = SeedEditorFormat.Parse(content, StandardFields);

        result.ShouldContainKey("System.Title");
        result.ShouldContainKey("Microsoft.VSTS.Common.Priority");
        result.Count.ShouldBe(2);
    }

    [Fact]
    public void Parse_GracefulDegradation_EmptyFieldDefs_StillParsesTitleAndDescription()
    {
        var content = """
            # Title
            Fallback Title

            # Description
            Fallback desc
            """;

        var result = SeedEditorFormat.Parse(content, Array.Empty<FieldDefinition>());

        result["System.Title"].ShouldBe("Fallback Title");
        result["System.Description"].ShouldBe("Fallback desc");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Roundtrip tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Generate_Then_Parse_Roundtrips()
    {
        var seed = new WorkItemBuilder(-1, "Roundtrip Title")
            .AsSeed()
            .WithField("System.Description", "Multi\nline\ndesc")
            .WithField("Microsoft.VSTS.Common.Priority", "2")
            .WithField("Microsoft.VSTS.Scheduling.Effort", "5")
            .WithField("System.Tags", "backend; api")
            .Build();

        var generated = SeedEditorFormat.Generate(seed, StandardFields);
        var parsed = SeedEditorFormat.Parse(generated, StandardFields);

        parsed["System.Title"].ShouldBe("Roundtrip Title");
        parsed["System.Description"]!.ReplaceLineEndings("\n").ShouldBe("Multi\nline\ndesc");
        parsed["Microsoft.VSTS.Common.Priority"].ShouldBe("2");
        parsed["Microsoft.VSTS.Scheduling.Effort"].ShouldBe("5");
        parsed["System.Tags"].ShouldBe("backend; api");
    }

    [Fact]
    public void Generate_Then_Parse_EmptyFields_Roundtrips()
    {
        var seed = new WorkItemBuilder(-1, "Just Title").AsSeed().Build();

        var generated = SeedEditorFormat.Generate(seed, StandardFields);
        var parsed = SeedEditorFormat.Parse(generated, StandardFields);

        parsed["System.Title"].ShouldBe("Just Title");
        // Fields with no value should not be present or null
        if (parsed.ContainsKey("System.Description"))
            parsed["System.Description"].ShouldBeNull();
    }

    [Fact]
    public void Generate_Then_Parse_GracefulDegradation_Roundtrips()
    {
        var seed = new WorkItemBuilder(-1, "Degraded Title")
            .AsSeed()
            .WithField("System.Description", "Degraded desc")
            .Build();

        var generated = SeedEditorFormat.Generate(seed, Array.Empty<FieldDefinition>());
        var parsed = SeedEditorFormat.Parse(generated, Array.Empty<FieldDefinition>());

        parsed["System.Title"].ShouldBe("Degraded Title");
        parsed["System.Description"].ShouldBe("Degraded desc");
    }
}
