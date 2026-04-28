using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Workspace;

public class WorkItemExportFormatTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Shared field definitions
    // ═══════════════════════════════════════════════════════════════

    private static readonly List<FieldDefinition> StandardFields =
    [
        new("System.Title", "Title", "String", false),
        new("System.Description", "Description", "Html", false),
        new("Microsoft.VSTS.Common.Priority", "Priority", "Integer", false),
        new("Microsoft.VSTS.Scheduling.Effort", "Effort", "Double", false),
        new("System.Tags", "Tags", "String", false),
    ];

    private static readonly List<FieldDefinition> FieldsWithReadOnly =
    [
        new("System.Title", "Title", "String", false),
        new("System.Description", "Description", "Html", false),
        new("System.Id", "ID", "Integer", true),
        new("System.Rev", "Rev", "Integer", true),
        new("System.CreatedDate", "Created Date", "DateTime", true),
        new("Microsoft.VSTS.Common.Priority", "Priority", "Integer", false),
    ];

    private static readonly List<FieldDefinition> FieldsWithState =
    [
        new("System.Title", "Title", "String", false),
        new("System.State", "State", "String", false),
        new("System.Description", "Description", "Html", false),
        new("Microsoft.VSTS.Common.Priority", "Priority", "Integer", false),
    ];

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private static WorkItem BuildItem(int id, string title, int revision = 1, Dictionary<string, string?>? fields = null)
    {
        var builder = new WorkItemBuilder(id, title);
        if (fields is not null)
            builder.WithFields(fields);
        var item = builder.Build();
        item.MarkSynced(revision);
        return item;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Generate: single item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Generate_SingleItem_ProducesFormatHeader()
    {
        var item = BuildItem(42, "Fix login bug");
        var result = WorkItemExportFormat.Generate([item], StandardFields);

        result.ShouldContain("<!-- twig-export: edit field values below, then run 'twig import' -->");
    }

    [Fact]
    public void Generate_SingleItem_ProducesMetadataComment()
    {
        var item = BuildItem(42, "Fix login bug", revision: 5);
        var result = WorkItemExportFormat.Generate([item], StandardFields);

        result.ShouldContain("<!-- item: id=42 rev=5 type=Task -->");
    }

    [Fact]
    public void Generate_SingleItem_ProducesH2TitleHeading()
    {
        var item = BuildItem(42, "Fix login bug");
        var result = WorkItemExportFormat.Generate([item], StandardFields);

        result.ShouldContain("## 42 \u2014 Fix login bug");
    }

    [Fact]
    public void Generate_SingleItem_ProducesFieldSections()
    {
        var fields = new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "1",
            ["Microsoft.VSTS.Scheduling.Effort"] = "8",
        };
        var item = BuildItem(100, "Add feature", fields: fields);
        var result = WorkItemExportFormat.Generate([item], StandardFields);

        result.ShouldContain("## Title");
        result.ShouldContain("Add feature");
        result.ShouldContain("## Priority");
        result.ShouldContain("1");
        result.ShouldContain("## Effort");
        result.ShouldContain("8");
    }

    [Fact]
    public void Generate_SingleItem_TitleFirst_DescriptionSecond_ThenAlphabetical()
    {
        var item = BuildItem(1, "Test");
        var result = WorkItemExportFormat.Generate([item], StandardFields);

        var titleIdx = result.IndexOf("## Title");
        var descIdx = result.IndexOf("## Description");
        var effortIdx = result.IndexOf("## Effort");
        var priorityIdx = result.IndexOf("## Priority");
        var tagsIdx = result.IndexOf("## Tags");

        titleIdx.ShouldBeGreaterThan(-1);
        descIdx.ShouldBeGreaterThan(titleIdx);
        effortIdx.ShouldBeGreaterThan(descIdx);
        priorityIdx.ShouldBeGreaterThan(effortIdx);
        tagsIdx.ShouldBeGreaterThan(priorityIdx);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Generate: multi-item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Generate_MultiItem_ProducesSeparatorBetweenItems()
    {
        var item1 = BuildItem(1, "First");
        var item2 = BuildItem(2, "Second");
        var result = WorkItemExportFormat.Generate([item1, item2], StandardFields);

        var lines = result.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        lines.ShouldContain("---");

        // Separator should appear between the two metadata comments
        var meta1Idx = lines.FindIndex(l => l.Contains("id=1"));
        var meta2Idx = lines.FindIndex(l => l.Contains("id=2"));
        var sepIdx = lines.FindIndex(meta1Idx, l => l == "---");

        sepIdx.ShouldBeGreaterThan(meta1Idx);
        sepIdx.ShouldBeLessThan(meta2Idx);
    }

    [Fact]
    public void Generate_MultiItem_EachItemHasOwnMetadataComment()
    {
        var item1 = BuildItem(10, "Alpha", revision: 2);
        var item2 = BuildItem(20, "Beta", revision: 3);
        var result = WorkItemExportFormat.Generate([item1, item2], StandardFields);

        result.ShouldContain("<!-- item: id=10 rev=2 type=Task -->");
        result.ShouldContain("<!-- item: id=20 rev=3 type=Task -->");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Generate: excludes read-only fields
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Generate_ExcludesReadOnlyFields()
    {
        var item = BuildItem(1, "Item");
        var result = WorkItemExportFormat.Generate([item], FieldsWithReadOnly);

        result.ShouldContain("## Title");
        result.ShouldContain("## Priority");
        result.ShouldNotContain("## ID");
        result.ShouldNotContain("## Rev");
        result.ShouldNotContain("## Created Date");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Generate: excludes System.State
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Generate_ExcludesSystemState()
    {
        var item = BuildItem(1, "Item");
        var result = WorkItemExportFormat.Generate([item], FieldsWithState);

        result.ShouldContain("## Title");
        result.ShouldContain("## Priority");
        result.ShouldNotContain("## State");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Generate: null/empty field values
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Generate_NullFieldValue_ProducesBlankContentAfterHeader()
    {
        var result = WorkItemExportFormat.Generate([BuildItem(1, "Item")], StandardFields);
        AssertFieldSectionIsEmpty(result, "Description");
    }

    [Fact]
    public void Generate_EmptyFieldValue_ProducesBlankContentAfterHeader()
    {
        var fields = new Dictionary<string, string?> { ["System.Description"] = "" };
        var result = WorkItemExportFormat.Generate([BuildItem(1, "Item", fields: fields)], StandardFields);
        AssertFieldSectionIsEmpty(result, "Description");
    }

    private static void AssertFieldSectionIsEmpty(string result, string fieldHeader)
    {
        var header = $"## {fieldHeader}";
        var idx = result.IndexOf(header);
        idx.ShouldBeGreaterThan(-1);
        var after = result[(idx + header.Length)..];
        after[..after.IndexOf("## ")].Trim().ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parse: round-trip (Generate → Parse)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_RoundTrip_ProducesIdenticalFieldValues()
    {
        var fields = new Dictionary<string, string?>
        {
            ["System.Description"] = "A detailed description",
            ["Microsoft.VSTS.Common.Priority"] = "2",
            ["Microsoft.VSTS.Scheduling.Effort"] = "13",
            ["System.Tags"] = "backend; api",
        };
        var item = BuildItem(99, "Round-trip test", revision: 7, fields: fields);

        var markdown = WorkItemExportFormat.Generate([item], StandardFields);
        var parsed = WorkItemExportFormat.Parse(markdown, StandardFields);

        parsed.Count.ShouldBe(1);
        var result = parsed[0];
        result.Id.ShouldBe(99);
        result.Revision.ShouldBe(7);
        result.TypeName.ShouldBe("Task");

        result.Fields["System.Title"].ShouldBe("Round-trip test");
        result.Fields["System.Description"].ShouldBe("A detailed description");
        result.Fields["Microsoft.VSTS.Common.Priority"].ShouldBe("2");
        result.Fields["Microsoft.VSTS.Scheduling.Effort"].ShouldBe("13");
        result.Fields["System.Tags"].ShouldBe("backend; api");
    }

    [Fact]
    public void Parse_RoundTrip_MultiItem_PreservesAllItems()
    {
        var item1 = BuildItem(1, "First", revision: 1, fields: new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "1",
        });
        var item2 = BuildItem(2, "Second", revision: 2, fields: new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "3",
        });

        var markdown = WorkItemExportFormat.Generate([item1, item2], StandardFields);
        var parsed = WorkItemExportFormat.Parse(markdown, StandardFields);

        parsed.Count.ShouldBe(2);
        parsed[0].Id.ShouldBe(1);
        parsed[0].Fields["System.Title"].ShouldBe("First");
        parsed[0].Fields["Microsoft.VSTS.Common.Priority"].ShouldBe("1");
        parsed[1].Id.ShouldBe(2);
        parsed[1].Fields["System.Title"].ShouldBe("Second");
        parsed[1].Fields["Microsoft.VSTS.Common.Priority"].ShouldBe("3");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parse: ignores unrecognized field headers
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_IgnoresUnrecognizedFieldHeaders()
    {
        var markdown = """
            <!-- twig-export: edit field values below, then run 'twig import' -->

            <!-- item: id=1 rev=1 type=Task -->

            ## 1 — Test

            ## Title
            My Title

            ## Unknown Custom Field
            some value that should be ignored

            ## Priority
            2

            """;

        var parsed = WorkItemExportFormat.Parse(markdown, StandardFields);

        parsed.Count.ShouldBe(1);
        parsed[0].Fields["System.Title"].ShouldBe("My Title");
        parsed[0].Fields["Microsoft.VSTS.Common.Priority"].ShouldBe("2");
        parsed[0].Fields.ShouldNotContainKey("Unknown Custom Field");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parse: maps display names to reference names
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_MapsDisplayNamesToReferenceNames()
    {
        var markdown = """
            <!-- item: id=5 rev=1 type=Bug -->

            ## 5 — A bug

            ## Title
            A bug

            ## Priority
            1

            ## Effort
            5

            """;

        var parsed = WorkItemExportFormat.Parse(markdown, StandardFields);

        parsed.Count.ShouldBe(1);
        parsed[0].Fields.ShouldContainKey("System.Title");
        parsed[0].Fields.ShouldContainKey("Microsoft.VSTS.Common.Priority");
        parsed[0].Fields.ShouldContainKey("Microsoft.VSTS.Scheduling.Effort");
        parsed[0].Fields["Microsoft.VSTS.Scheduling.Effort"].ShouldBe("5");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parse: multi-item document
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_MultiItemDocument_ProducesMultipleRecords()
    {
        var markdown = """
            <!-- twig-export: edit field values below, then run 'twig import' -->

            <!-- item: id=10 rev=2 type=Task -->

            ## 10 — Alpha

            ## Title
            Alpha

            ---

            <!-- item: id=20 rev=3 type=Bug -->

            ## 20 — Beta

            ## Title
            Beta

            """;

        var parsed = WorkItemExportFormat.Parse(markdown, StandardFields);

        parsed.Count.ShouldBe(2);
        parsed[0].Id.ShouldBe(10);
        parsed[0].Fields["System.Title"].ShouldBe("Alpha");
        parsed[1].Id.ShouldBe(20);
        parsed[1].Fields["System.Title"].ShouldBe("Beta");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parse: metadata extraction
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_ExtractsIdRevisionTypeNameFromMetadata()
    {
        var markdown = """
            <!-- item: id=123 rev=45 type=User Story -->

            ## 123 — A user story

            ## Title
            A user story

            """;

        var parsed = WorkItemExportFormat.Parse(markdown, StandardFields);

        parsed.Count.ShouldBe(1);
        parsed[0].Id.ShouldBe(123);
        parsed[0].Revision.ShouldBe(45);
        parsed[0].TypeName.ShouldBe("User Story");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Generate_EmptyItemsList_ProducesHeaderOnly()
    {
        var result = WorkItemExportFormat.Generate([], StandardFields);

        result.ShouldContain("<!-- twig-export:");
        result.ShouldNotContain("<!-- item:");
        result.ShouldNotContain("##");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void Parse_EmptyOrWhitespaceInput_ReturnsEmptyList(string input)
    {
        WorkItemExportFormat.Parse(input, StandardFields).Count.ShouldBe(0);
    }

    [Fact]
    public void Parse_MissingMetadataComment_ReturnsEmptyList()
    {
        var markdown = """
            ## Title
            Some title

            ## Description
            Some description
            """;

        var parsed = WorkItemExportFormat.Parse(markdown, StandardFields);
        parsed.Count.ShouldBe(0);
    }

    [Fact]
    public void Parse_MalformedMetadata_ReturnsEmptyList()
    {
        var markdown = """
            <!-- item: id=abc rev=xyz type= -->

            ## Title
            Some title
            """;

        var parsed = WorkItemExportFormat.Parse(markdown, StandardFields);
        parsed.Count.ShouldBe(0);
    }

    [Fact]
    public void Parse_NullFieldsRoundTrip_ProducesNullValues()
    {
        var item = BuildItem(1, "Sparse Item", revision: 1);
        // No extra fields set — Description, Priority, Effort, Tags should all be null
        var markdown = WorkItemExportFormat.Generate([item], StandardFields);
        var parsed = WorkItemExportFormat.Parse(markdown, StandardFields);

        parsed.Count.ShouldBe(1);
        parsed[0].Fields["System.Title"].ShouldBe("Sparse Item");
        parsed[0].Fields["System.Description"].ShouldBeNull();
        parsed[0].Fields["Microsoft.VSTS.Common.Priority"].ShouldBeNull();
    }

    [Fact]
    public void Generate_ExcludesAllExplicitlyExcludedSystemFields()
    {
        // All excluded fields from WorkItemExportFormat, even when marked writable
        var fieldsWithExcluded = new List<FieldDefinition>
        {
            new("System.Title", "Title", "String", false),
            new("System.Id", "ID", "Integer", false),
            new("System.Rev", "Rev", "Integer", false),
            new("System.State", "State", "String", false),
            new("System.CreatedDate", "Created Date", "DateTime", false),
            new("System.ChangedDate", "Changed Date", "DateTime", false),
            new("System.Watermark", "Watermark", "Integer", false),
            new("System.CreatedBy", "Created By", "String", false),
            new("System.ChangedBy", "Changed By", "String", false),
            new("System.AuthorizedDate", "Authorized Date", "DateTime", false),
            new("System.RevisedDate", "Revised Date", "DateTime", false),
            new("System.BoardColumn", "Board Column", "String", false),
            new("System.BoardColumnDone", "Board Column Done", "Boolean", false),
            new("System.BoardLane", "Board Lane", "String", false),
        };

        var item = BuildItem(1, "Item");
        var result = WorkItemExportFormat.Generate([item], fieldsWithExcluded);

        result.ShouldContain("## Title");
        result.ShouldNotContain("## ID");
        result.ShouldNotContain("## Rev");
        result.ShouldNotContain("## State");
        result.ShouldNotContain("## Created Date");
        result.ShouldNotContain("## Changed Date");
        result.ShouldNotContain("## Watermark");
        result.ShouldNotContain("## Created By");
        result.ShouldNotContain("## Changed By");
        result.ShouldNotContain("## Authorized Date");
        result.ShouldNotContain("## Revised Date");
        result.ShouldNotContain("## Board Column");
        result.ShouldNotContain("## Board Column Done");
        result.ShouldNotContain("## Board Lane");
    }

    [Fact]
    public void Parse_MultilineFieldValue_PreservesContent()
    {
        var markdown = """
            <!-- item: id=1 rev=1 type=Task -->

            ## 1 — Test

            ## Title
            Test

            ## Description
            Line one
            Line two
            Line three

            """;

        var parsed = WorkItemExportFormat.Parse(markdown, StandardFields);

        parsed.Count.ShouldBe(1);
        var desc = parsed[0].Fields["System.Description"];
        desc.ShouldNotBeNull();
        desc.ShouldContain("Line one");
        desc.ShouldContain("Line two");
        desc.ShouldContain("Line three");
    }

    [Fact]
    public void Generate_WorkItemType_ReflectedInMetadata()
    {
        var item = new WorkItemBuilder(50, "Epic item")
            .AsEpic()
            .Build();
        item.MarkSynced(3);

        var result = WorkItemExportFormat.Generate([item], StandardFields);

        result.ShouldContain("<!-- item: id=50 rev=3 type=Epic -->");
    }
}
