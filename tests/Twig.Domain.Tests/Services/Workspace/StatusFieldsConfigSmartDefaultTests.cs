using Shouldly;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services.Workspace;

/// <summary>
/// Unit tests for process-aware smart defaults in <see cref="StatusFieldsConfig"/>.
/// </summary>
public class StatusFieldsConfigSmartDefaultTests
{
    // ── Helper factories ────────────────────────────────────────────

    private static FieldDefinition Def(
        string refName, string displayName, string dataType = "string", bool isReadOnly = false)
        => new(refName, displayName, dataType, isReadOnly);

    /// <summary>
    /// A broad set of fields that spans Agile, Scrum, and CMMI templates.
    /// </summary>
    private static List<FieldDefinition> AllTemplateFields() =>
    [
        // Agile fields
        Def("Microsoft.VSTS.Common.Priority", "Priority", "integer"),
        Def("Microsoft.VSTS.Scheduling.StoryPoints", "Story Points", "double"),
        Def("Microsoft.VSTS.Common.ValueArea", "Value Area"),

        // Scrum fields
        Def("Microsoft.VSTS.Scheduling.Effort", "Effort", "double"),
        Def("Microsoft.VSTS.Common.BusinessValue", "Business Value", "integer"),
        Def("Microsoft.VSTS.Common.BacklogPriority", "Backlog Priority", "double"),

        // CMMI fields
        Def("Microsoft.VSTS.Scheduling.Size", "Size", "double"),
        Def("Microsoft.VSTS.CMMI.Blocked", "Blocked"),

        // Shared fields
        Def("System.Tags", "Tags", "plainText", isReadOnly: true),
        Def("System.CreatedDate", "Created Date", "dateTime", isReadOnly: true),
        Def("System.ChangedDate", "Changed Date", "dateTime", isReadOnly: true),

        // Non-template fields (should not be starred by template-specific defaults)
        Def("Microsoft.VSTS.Common.Severity", "Severity"),
        Def("System.Description", "Description", "html", isReadOnly: true),
    ];

    // ── (a) Agile template stars StoryPoints but not Effort ─────────

    [Fact]
    public void IsDefaultStarred_Agile_StarsStoryPointsNotEffort()
    {
        var storyPoints = Def("Microsoft.VSTS.Scheduling.StoryPoints", "Story Points", "double");
        var effort = Def("Microsoft.VSTS.Scheduling.Effort", "Effort", "double");

        StatusFieldsConfig.IsDefaultStarred(storyPoints, "Agile").ShouldBeTrue();
        StatusFieldsConfig.IsDefaultStarred(effort, "Agile").ShouldBeFalse();
    }

    [Fact]
    public void Generate_Agile_StarsExpectedFields()
    {
        var defs = AllTemplateFields();
        var content = StatusFieldsConfig.Generate(defs, null, "Agile");
        var entries = StatusFieldsConfig.Parse(content);

        var starred = entries.Where(e => e.IsIncluded).Select(e => e.ReferenceName).ToList();

        starred.ShouldContain("Microsoft.VSTS.Common.Priority");
        starred.ShouldContain("Microsoft.VSTS.Scheduling.StoryPoints");
        starred.ShouldContain("Microsoft.VSTS.Common.ValueArea");
        starred.ShouldContain("System.Tags");
        starred.ShouldContain("System.CreatedDate");
        starred.ShouldContain("System.ChangedDate");

        // Scrum/CMMI-specific fields should NOT be starred
        starred.ShouldNotContain("Microsoft.VSTS.Scheduling.Effort");
        starred.ShouldNotContain("Microsoft.VSTS.Common.BusinessValue");
        starred.ShouldNotContain("Microsoft.VSTS.Common.BacklogPriority");
        starred.ShouldNotContain("Microsoft.VSTS.Scheduling.Size");
        starred.ShouldNotContain("Microsoft.VSTS.CMMI.Blocked");
        starred.ShouldNotContain("Microsoft.VSTS.Common.Severity");
    }

    // ── (b) Scrum template stars Effort but not StoryPoints ─────────

    [Fact]
    public void IsDefaultStarred_Scrum_StarsEffortNotStoryPoints()
    {
        var effort = Def("Microsoft.VSTS.Scheduling.Effort", "Effort", "double");
        var storyPoints = Def("Microsoft.VSTS.Scheduling.StoryPoints", "Story Points", "double");

        StatusFieldsConfig.IsDefaultStarred(effort, "Scrum").ShouldBeTrue();
        StatusFieldsConfig.IsDefaultStarred(storyPoints, "Scrum").ShouldBeFalse();
    }

    [Fact]
    public void Generate_Scrum_StarsExpectedFields()
    {
        var defs = AllTemplateFields();
        var content = StatusFieldsConfig.Generate(defs, null, "Scrum");
        var entries = StatusFieldsConfig.Parse(content);

        var starred = entries.Where(e => e.IsIncluded).Select(e => e.ReferenceName).ToList();

        starred.ShouldContain("Microsoft.VSTS.Scheduling.Effort");
        starred.ShouldContain("Microsoft.VSTS.Common.BusinessValue");
        starred.ShouldContain("Microsoft.VSTS.Common.BacklogPriority");
        starred.ShouldContain("System.Tags");
        starred.ShouldContain("System.CreatedDate");
        starred.ShouldContain("System.ChangedDate");

        // Agile/CMMI-specific fields should NOT be starred
        starred.ShouldNotContain("Microsoft.VSTS.Scheduling.StoryPoints");
        starred.ShouldNotContain("Microsoft.VSTS.Common.Priority");
        starred.ShouldNotContain("Microsoft.VSTS.Common.ValueArea");
        starred.ShouldNotContain("Microsoft.VSTS.Scheduling.Size");
        starred.ShouldNotContain("Microsoft.VSTS.CMMI.Blocked");
        starred.ShouldNotContain("Microsoft.VSTS.Common.Severity");
    }

    // ── (c) CMMI template stars Blocked ─────────────────────────────

    [Fact]
    public void IsDefaultStarred_Cmmi_StarsBlocked()
    {
        var blocked = Def("Microsoft.VSTS.CMMI.Blocked", "Blocked");

        StatusFieldsConfig.IsDefaultStarred(blocked, "CMMI").ShouldBeTrue();
    }

    [Fact]
    public void Generate_Cmmi_StarsExpectedFields()
    {
        var defs = AllTemplateFields();
        var content = StatusFieldsConfig.Generate(defs, null, "CMMI");
        var entries = StatusFieldsConfig.Parse(content);

        var starred = entries.Where(e => e.IsIncluded).Select(e => e.ReferenceName).ToList();

        starred.ShouldContain("Microsoft.VSTS.Common.Priority");
        starred.ShouldContain("Microsoft.VSTS.Scheduling.Size");
        starred.ShouldContain("Microsoft.VSTS.CMMI.Blocked");
        starred.ShouldContain("System.Tags");
        starred.ShouldContain("System.CreatedDate");
        starred.ShouldContain("System.ChangedDate");

        // Agile/Scrum-specific fields should NOT be starred
        starred.ShouldNotContain("Microsoft.VSTS.Scheduling.StoryPoints");
        starred.ShouldNotContain("Microsoft.VSTS.Scheduling.Effort");
        starred.ShouldNotContain("Microsoft.VSTS.Common.BusinessValue");
        starred.ShouldNotContain("Microsoft.VSTS.Common.BacklogPriority");
        starred.ShouldNotContain("Microsoft.VSTS.Common.ValueArea");
        starred.ShouldNotContain("Microsoft.VSTS.Common.Severity");
    }

    // ── (d) Unknown template falls back to keyword heuristic ────────

    [Fact]
    public void IsDefaultStarred_UnknownTemplate_FallsBackToKeywordHeuristic()
    {
        var effort = Def("Microsoft.VSTS.Scheduling.Effort", "Effort", "double");
        var severity = Def("Microsoft.VSTS.Common.Severity", "Severity");
        var valueArea = Def("Microsoft.VSTS.Common.ValueArea", "Value Area");

        // "Effort" and "Severity" match keywords, so they should be starred
        StatusFieldsConfig.IsDefaultStarred(effort, "CustomProcess").ShouldBeTrue();
        StatusFieldsConfig.IsDefaultStarred(severity, "CustomProcess").ShouldBeTrue();

        // "Value Area" does not match any keyword
        StatusFieldsConfig.IsDefaultStarred(valueArea, "CustomProcess").ShouldBeFalse();
    }

    [Fact]
    public void Generate_UnknownTemplate_MatchesKeywordHeuristic()
    {
        var defs = AllTemplateFields();
        var withTemplate = StatusFieldsConfig.Generate(defs, null, "SomeCustomProcess");
        var withoutTemplate = StatusFieldsConfig.Generate(defs);

        var entriesWithTemplate = StatusFieldsConfig.Parse(withTemplate);
        var entriesWithout = StatusFieldsConfig.Parse(withoutTemplate);

        // Should produce identical star selections
        entriesWithTemplate.Count.ShouldBe(entriesWithout.Count);
        for (var i = 0; i < entriesWithTemplate.Count; i++)
        {
            entriesWithTemplate[i].ReferenceName.ShouldBe(entriesWithout[i].ReferenceName);
            entriesWithTemplate[i].IsIncluded.ShouldBe(entriesWithout[i].IsIncluded);
        }
    }

    // ── (e) Null template falls back to keyword heuristic ───────────

    [Fact]
    public void IsDefaultStarred_NullTemplate_FallsBackToKeywordHeuristic()
    {
        var effort = Def("Microsoft.VSTS.Scheduling.Effort", "Effort", "double");
        var severity = Def("Microsoft.VSTS.Common.Severity", "Severity");
        var valueArea = Def("Microsoft.VSTS.Common.ValueArea", "Value Area");

        StatusFieldsConfig.IsDefaultStarred(effort, null).ShouldBeTrue();
        StatusFieldsConfig.IsDefaultStarred(severity, null).ShouldBeTrue();
        StatusFieldsConfig.IsDefaultStarred(valueArea, null).ShouldBeFalse();
    }

    [Fact]
    public void Generate_NullTemplate_MatchesKeywordHeuristic()
    {
        var defs = AllTemplateFields();
        var withNull = StatusFieldsConfig.Generate(defs, null, null);
        var withoutTemplate = StatusFieldsConfig.Generate(defs);

        var entriesWithNull = StatusFieldsConfig.Parse(withNull);
        var entriesWithout = StatusFieldsConfig.Parse(withoutTemplate);

        entriesWithNull.Count.ShouldBe(entriesWithout.Count);
        for (var i = 0; i < entriesWithNull.Count; i++)
        {
            entriesWithNull[i].ReferenceName.ShouldBe(entriesWithout[i].ReferenceName);
            entriesWithNull[i].IsIncluded.ShouldBe(entriesWithout[i].IsIncluded);
        }
    }

    // ── (f) Template name matching is case-insensitive ───────────────

    [Theory]
    [InlineData("agile")]
    [InlineData("Agile")]
    [InlineData("AGILE")]
    [InlineData("aGiLe")]
    public void IsDefaultStarred_TemplateName_CaseInsensitive(string templateName)
    {
        var storyPoints = Def("Microsoft.VSTS.Scheduling.StoryPoints", "Story Points", "double");
        var effort = Def("Microsoft.VSTS.Scheduling.Effort", "Effort", "double");

        StatusFieldsConfig.IsDefaultStarred(storyPoints, templateName).ShouldBeTrue();
        StatusFieldsConfig.IsDefaultStarred(effort, templateName).ShouldBeFalse();
    }

    [Theory]
    [InlineData("scrum")]
    [InlineData("Scrum")]
    [InlineData("SCRUM")]
    public void Generate_ScrumCaseInsensitive_StarsEffort(string templateName)
    {
        var defs = AllTemplateFields();
        var content = StatusFieldsConfig.Generate(defs, null, templateName);
        var entries = StatusFieldsConfig.Parse(content);

        var starred = entries.Where(e => e.IsIncluded).Select(e => e.ReferenceName).ToList();
        starred.ShouldContain("Microsoft.VSTS.Scheduling.Effort");
        starred.ShouldNotContain("Microsoft.VSTS.Scheduling.StoryPoints");
    }

    [Theory]
    [InlineData("cmmi")]
    [InlineData("Cmmi")]
    [InlineData("CMMI")]
    public void Generate_CmmiCaseInsensitive_StarsBlocked(string templateName)
    {
        var defs = AllTemplateFields();
        var content = StatusFieldsConfig.Generate(defs, null, templateName);
        var entries = StatusFieldsConfig.Parse(content);

        var starred = entries.Where(e => e.IsIncluded).Select(e => e.ReferenceName).ToList();
        starred.ShouldContain("Microsoft.VSTS.CMMI.Blocked");
    }

    // ── Merge behavior unchanged by template awareness ──────────────

    [Fact]
    public void Generate_MergeMode_IgnoresProcessTemplate()
    {
        var defs = AllTemplateFields();

        // First generate fresh with no template (keyword heuristic)
        var freshContent = StatusFieldsConfig.Generate(defs);

        // User edits: unstar everything, star only ValueArea
        var edited = freshContent
            .Replace("* ", "  ")
            .Replace("  Value Area", "* Value Area");

        // Merge with Agile template — user selections must be preserved
        var merged = StatusFieldsConfig.Generate(defs, edited, "Agile");
        var entries = StatusFieldsConfig.Parse(merged);

        // ValueArea should still be starred (user selected it)
        var valueArea = entries.First(e => e.ReferenceName == "Microsoft.VSTS.Common.ValueArea");
        valueArea.IsIncluded.ShouldBeTrue();

        // All other non-dateTime fields should remain unstarred (user unstarred them)
        foreach (var entry in entries.Where(e => e.ReferenceName != "Microsoft.VSTS.Common.ValueArea"))
        {
            entry.IsIncluded.ShouldBeFalse(
                $"Field {entry.ReferenceName} should remain unstarred (user's merge selection)");
        }
    }

    // ── DateTime fields always starred regardless of template ────────

    [Theory]
    [InlineData("Agile")]
    [InlineData("Scrum")]
    [InlineData("CMMI")]
    [InlineData(null)]
    public void IsDefaultStarred_DateTimeFields_AlwaysStarred(string? templateName)
    {
        var dateField = Def("System.CreatedDate", "Created Date", "dateTime", isReadOnly: true);
        StatusFieldsConfig.IsDefaultStarred(dateField, templateName).ShouldBeTrue();
    }
}
