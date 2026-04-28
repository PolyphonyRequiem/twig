using Shouldly;
using Twig.Domain.Services;
using Twig.Domain.Services.Field;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

/// <summary>
/// Unit tests for <see cref="StatusFieldsConfig"/>.
/// </summary>
public class StatusFieldsConfigTests
{
    // ── Helper factories ────────────────────────────────────────────

    private static FieldDefinition Def(
        string refName, string displayName, string dataType = "string", bool isReadOnly = false)
        => new(refName, displayName, dataType, isReadOnly);

    private static List<FieldDefinition> SampleImportableDefinitions() =>
    [
        Def("Microsoft.VSTS.Common.Priority", "Priority", "integer"),
        Def("Microsoft.VSTS.Scheduling.StoryPoints", "Story Points", "double"),
        Def("System.Tags", "Tags", "plainText", isReadOnly: true),
        Def("Microsoft.VSTS.Common.ValueArea", "Value Area"),
        Def("System.CreatedDate", "Created Date", "dateTime", isReadOnly: true),
        Def("System.ChangedDate", "Changed Date", "dateTime", isReadOnly: true),
        Def("Microsoft.VSTS.Common.Severity", "Severity"),
        Def("System.Description", "Description", "html", isReadOnly: true),
        Def("Microsoft.VSTS.Scheduling.Effort", "Effort", "double"),
    ];

    private static List<FieldDefinition> SampleWithCoreAndNonImportable()
    {
        var defs = SampleImportableDefinitions();
        // Add core fields — should be excluded
        defs.Add(Def("System.Id", "ID", "integer", isReadOnly: true));
        defs.Add(Def("System.Rev", "Rev", "integer", isReadOnly: true));
        defs.Add(Def("System.WorkItemType", "Work Item Type"));
        defs.Add(Def("System.Title", "Title"));
        defs.Add(Def("System.State", "State"));
        defs.Add(Def("System.AssignedTo", "Assigned To"));
        defs.Add(Def("System.IterationPath", "Iteration Path"));
        defs.Add(Def("System.AreaPath", "Area Path"));
        defs.Add(Def("System.TeamProject", "Team Project"));
        // Add non-importable fields — should be excluded
        defs.Add(Def("System.Watermark", "Watermark", "integer", isReadOnly: true));
        defs.Add(Def("Custom.IsBlocked", "Is Blocked", "boolean"));
        defs.Add(Def("System.AreaId", "Area ID", "treePath"));
        defs.Add(Def("System.History", "History", "history"));
        return defs;
    }

    // ── 1. Generate from scratch produces valid content with correct defaults starred ──

    [Fact]
    public void Generate_FromScratch_ProducesCommentHeaderAndStarredDefaults()
    {
        var defs = SampleImportableDefinitions();
        var content = StatusFieldsConfig.Generate(defs);

        content.ShouldContain("# twig status-fields configuration");
        content.ShouldContain("# Prefix a line with '*'");

        // Starred defaults: Priority, Story Points, Tags, Severity, CreatedDate, ChangedDate, Effort
        content.ShouldContain("* Changed Date");
        content.ShouldContain("* Created Date");
        content.ShouldContain("* Effort");
        content.ShouldContain("* Priority");
        content.ShouldContain("* Severity");
        content.ShouldContain("* Story Points");
        content.ShouldContain("* Tags");

        // Unstarred: ValueArea, Description
        content.ShouldContain("  Description");
        content.ShouldContain("  Value Area");
    }

    // ── 2. Parse extracts correct entries from generated content ────

    [Fact]
    public void Parse_ExtractsCorrectEntriesFromGeneratedContent()
    {
        var defs = SampleImportableDefinitions();
        var content = StatusFieldsConfig.Generate(defs);
        var entries = StatusFieldsConfig.Parse(content);

        entries.Count.ShouldBe(9);

        var included = entries.Where(e => e.IsIncluded).Select(e => e.ReferenceName).ToList();
        included.ShouldContain("Microsoft.VSTS.Common.Priority");
        included.ShouldContain("Microsoft.VSTS.Scheduling.StoryPoints");
        included.ShouldContain("System.Tags");
        included.ShouldContain("Microsoft.VSTS.Common.Severity");
        included.ShouldContain("System.CreatedDate");
        included.ShouldContain("System.ChangedDate");
        included.ShouldContain("Microsoft.VSTS.Scheduling.Effort");

        var excluded = entries.Where(e => !e.IsIncluded).Select(e => e.ReferenceName).ToList();
        excluded.ShouldContain("Microsoft.VSTS.Common.ValueArea");
        excluded.ShouldContain("System.Description");
    }

    // ── 3. Generate→Parse round-trip produces expected entries ──────

    [Fact]
    public void GenerateParse_RoundTrip_IsStable()
    {
        var defs = SampleImportableDefinitions();
        var content1 = StatusFieldsConfig.Generate(defs);
        var entries1 = StatusFieldsConfig.Parse(content1);

        // Re-generate from the parsed content (merge mode)
        var content2 = StatusFieldsConfig.Generate(defs, content1);
        var entries2 = StatusFieldsConfig.Parse(content2);

        entries2.Count.ShouldBe(entries1.Count);
        for (var i = 0; i < entries1.Count; i++)
        {
            entries2[i].ReferenceName.ShouldBe(entries1[i].ReferenceName);
            entries2[i].IsIncluded.ShouldBe(entries1[i].IsIncluded);
        }
    }

    // ── 4. IsDefaultStarred correctly identifies fields ─────────────

    [Theory]
    [InlineData("Effort", "double")]
    [InlineData("Story Points", "double")]
    [InlineData("Priority", "integer")]
    [InlineData("Severity", "string")]
    [InlineData("Tags", "plainText")]
    [InlineData("Original Effort", "double")]
    [InlineData("Sprint Points Remaining", "double")]
    [InlineData("Created Date", "dateTime")]
    [InlineData("Changed Date", "dateTime")]
    [InlineData("Some DateTime Field", "dateTime")]
    public void IsDefaultStarred_MatchingFields_ReturnsTrue(string displayName, string dataType)
    {
        var def = Def("Custom.Field", displayName, dataType);
        StatusFieldsConfig.IsDefaultStarred(def).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Value Area", "string")]
    [InlineData("Description", "html")]
    [InlineData("Board Column", "string")]
    [InlineData("Changed By", "string")]
    [InlineData("Custom Field", "integer")]
    public void IsDefaultStarred_NonMatchingFields_ReturnsFalse(string displayName, string dataType)
    {
        var def = Def("Custom.Field", displayName, dataType);
        StatusFieldsConfig.IsDefaultStarred(def).ShouldBeFalse();
    }

    // ── 5. Merge preserves existing order and selections ────────────

    [Fact]
    public void Generate_Merge_PreservesOrderAndSelectionsAppendsNewDropsRemoved()
    {
        // Initial: Priority (starred), ValueArea (unstarred)
        var initialDefs = new List<FieldDefinition>
        {
            Def("Microsoft.VSTS.Common.Priority", "Priority", "integer"),
            Def("Microsoft.VSTS.Common.ValueArea", "Value Area"),
        };

        var existingContent = StatusFieldsConfig.Generate(initialDefs);

        // Toggle: user unstarred Priority, starred ValueArea
        existingContent = existingContent
            .Replace("* Priority", "  Priority")
            .Replace("  Value Area", "* Value Area");

        // New definitions: add Severity, remove ValueArea
        var newDefs = new List<FieldDefinition>
        {
            Def("Microsoft.VSTS.Common.Priority", "Priority", "integer"),
            Def("Microsoft.VSTS.Common.Severity", "Severity"),
        };

        var merged = StatusFieldsConfig.Generate(newDefs, existingContent);
        var entries = StatusFieldsConfig.Parse(merged);

        // Priority should keep unstarred state (user changed it)
        entries[0].ReferenceName.ShouldBe("Microsoft.VSTS.Common.Priority");
        entries[0].IsIncluded.ShouldBeFalse();

        // ValueArea should be dropped (no longer in definitions)
        entries.ShouldNotContain(e => e.ReferenceName == "Microsoft.VSTS.Common.ValueArea");

        // Severity should be appended unmarked
        entries[1].ReferenceName.ShouldBe("Microsoft.VSTS.Common.Severity");
        entries[1].IsIncluded.ShouldBeFalse();
    }

    // ── 6. Parse handles malformed lines gracefully ─────────────────

    [Fact]
    public void Parse_MalformedLines_SkipsGracefully()
    {
        var content = """
            # comment line
            
            This line has no parens
            * Priority                    (Microsoft.VSTS.Common.Priority)           [integer]
              another malformed line
            
            # another comment
              Value Area                  (Microsoft.VSTS.Common.ValueArea)          [string]
            """;

        var entries = StatusFieldsConfig.Parse(content);

        entries.Count.ShouldBe(2);
        entries[0].ReferenceName.ShouldBe("Microsoft.VSTS.Common.Priority");
        entries[0].IsIncluded.ShouldBeTrue();
        entries[1].ReferenceName.ShouldBe("Microsoft.VSTS.Common.ValueArea");
        entries[1].IsIncluded.ShouldBeFalse();
    }

    [Fact]
    public void Parse_CommentOnlyContent_ReturnsEmpty()
    {
        var content = """
            # only comments
            # nothing else
            
            """;

        var entries = StatusFieldsConfig.Parse(content);
        entries.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_EmptyString_ReturnsEmpty()
    {
        var entries = StatusFieldsConfig.Parse("");
        entries.ShouldBeEmpty();
    }

    // ── 7. Core fields (all 9) are excluded from generated output ───

    [Theory]
    [InlineData("System.Id")]
    [InlineData("System.Rev")]
    [InlineData("System.WorkItemType")]
    [InlineData("System.Title")]
    [InlineData("System.State")]
    [InlineData("System.AssignedTo")]
    [InlineData("System.IterationPath")]
    [InlineData("System.AreaPath")]
    [InlineData("System.TeamProject")]
    public void Generate_ExcludesCoreFields(string refName)
    {
        var defs = new List<FieldDefinition>
        {
            Def(refName, "Core Field"),
            Def("Microsoft.VSTS.Common.Priority", "Priority", "integer"),
        };

        var content = StatusFieldsConfig.Generate(defs);
        var entries = StatusFieldsConfig.Parse(content);

        entries.ShouldNotContain(e => e.ReferenceName == refName);
        entries.ShouldContain(e => e.ReferenceName == "Microsoft.VSTS.Common.Priority");
    }

    // ── 8. IsImportable excludes read-only non-display-worthy fields ──

    [Fact]
    public void IsImportable_ReadOnlyNonDisplayWorthy_ReturnsFalse()
    {
        var def = Def("System.Watermark", "Watermark", "integer", isReadOnly: true);
        StatusFieldsConfig.IsImportable(def).ShouldBeFalse();
    }

    [Fact]
    public void IsImportable_ReadOnlyNonDisplayWorthyString_ReturnsFalse()
    {
        // A random read-only field that's not in the display-worthy list
        var def = Def("Custom.ReadOnlyField", "Read Only Field", "string", isReadOnly: true);
        StatusFieldsConfig.IsImportable(def).ShouldBeFalse();
    }

    // ── 9. IsImportable excludes non-importable data types ──────────

    [Theory]
    [InlineData("boolean")]
    [InlineData("treePath")]
    [InlineData("history")]
    public void IsImportable_NonImportableDataType_ReturnsFalse(string dataType)
    {
        var def = Def("Custom.Field", "Field", dataType);
        StatusFieldsConfig.IsImportable(def).ShouldBeFalse();
    }

    // ── 10. IsImportable allows display-worthy read-only fields ─────

    [Theory]
    [InlineData("System.CreatedDate", "Created Date", "dateTime")]
    [InlineData("System.ChangedDate", "Changed Date", "dateTime")]
    [InlineData("System.CreatedBy", "Created By", "string")]
    [InlineData("System.ChangedBy", "Changed By", "string")]
    [InlineData("System.Tags", "Tags", "plainText")]
    [InlineData("System.Description", "Description", "html")]
    [InlineData("System.BoardColumn", "Board Column", "string")]
    [InlineData("System.BoardColumnDone", "Board Column Done", "string")]
    public void IsImportable_DisplayWorthyReadOnly_ReturnsTrue(
        string refName, string displayName, string dataType)
    {
        var def = Def(refName, displayName, dataType, isReadOnly: true);
        StatusFieldsConfig.IsImportable(def).ShouldBeTrue();
    }

    // ── 11. IsImportable excludes System.TeamProject ────────────────

    [Fact]
    public void IsImportable_SystemTeamProject_ReturnsFalse()
    {
        // System.TeamProject is not in FieldImportFilter.CoreFieldRefs (only 8 fields),
        // but IS in StatusFieldsConfig.CoreFields (9 fields). Verify exclusion.
        var def = Def("System.TeamProject", "Team Project");
        StatusFieldsConfig.IsImportable(def).ShouldBeFalse();
    }

    [Fact]
    public void IsImportable_SystemTeamProject_PassesFieldImportFilter()
    {
        // Confirm that FieldImportFilter alone would allow it through
        var def = Def("System.TeamProject", "Team Project");
        FieldImportFilter.ShouldImport("System.TeamProject", def).ShouldBeTrue();
    }

    // ── 12. Generated config file only contains importable fields ───

    [Fact]
    public void Generate_OnlyContainsImportableFields()
    {
        var defs = SampleWithCoreAndNonImportable();
        var content = StatusFieldsConfig.Generate(defs);
        var entries = StatusFieldsConfig.Parse(content);

        // Should have exactly the 9 importable fields from SampleImportableDefinitions
        entries.Count.ShouldBe(9);

        // Verify no core fields (uses internal CoreFields directly)
        foreach (var entry in entries)
            StatusFieldsConfig.CoreFields.ShouldNotContain(entry.ReferenceName);

        // Verify no non-importable fields
        entries.ShouldNotContain(e => e.ReferenceName == "System.Watermark");
        entries.ShouldNotContain(e => e.ReferenceName == "Custom.IsBlocked");
        entries.ShouldNotContain(e => e.ReferenceName == "System.AreaId");
        entries.ShouldNotContain(e => e.ReferenceName == "System.History");
    }

    // ── Additional: format correctness ──────────────────────────────

    [Fact]
    public void Generate_LinesContainReferenceNameInParensAndDataTypeInBrackets()
    {
        var defs = new List<FieldDefinition>
        {
            Def("Microsoft.VSTS.Common.Priority", "Priority", "integer"),
        };

        var content = StatusFieldsConfig.Generate(defs);

        content.ShouldContain("(Microsoft.VSTS.Common.Priority)");
        content.ShouldContain("[integer]");
    }

    [Fact]
    public void Generate_StarredFieldsAppearBeforeUnstarred()
    {
        var defs = SampleImportableDefinitions();
        var content = StatusFieldsConfig.Generate(defs);
        var entries = StatusFieldsConfig.Parse(content);

        // All starred entries should come before all unstarred entries
        var foundUnstarred = false;
        foreach (var entry in entries)
        {
            if (!entry.IsIncluded)
                foundUnstarred = true;
            else if (foundUnstarred)
                Assert.Fail("Starred entry found after unstarred entry in fresh generation");
        }
    }
}
