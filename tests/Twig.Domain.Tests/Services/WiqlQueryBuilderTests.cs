using Shouldly;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class WiqlQueryBuilderTests
{
    // ═══════════════════════════════════════════════════════════════
    //  No-filter default
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_NoFilters_ProducesValidQueryWithOrderBy()
    {
        var result = WiqlQueryBuilder.Build(new QueryParameters());

        result.ShouldBe(
            "SELECT [System.Id] FROM WorkItems ORDER BY [System.ChangedDate] DESC");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Individual clauses
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_SearchText_ContainsOnTitleAndDescription()
    {
        var result = WiqlQueryBuilder.Build(new QueryParameters { SearchText = "login bug" });

        result.ShouldContain("[System.Title] CONTAINS 'login bug'");
        result.ShouldContain("[System.Description] CONTAINS 'login bug'");
        result.ShouldContain(" OR ");
    }

    [Fact]
    public void Build_TypeFilter_EqualsClause()
    {
        var result = WiqlQueryBuilder.Build(new QueryParameters { TypeFilter = "Bug" });

        result.ShouldContain("[System.WorkItemType] = 'Bug'");
    }

    [Fact]
    public void Build_StateFilter_EqualsClause()
    {
        var result = WiqlQueryBuilder.Build(new QueryParameters { StateFilter = "Active" });

        result.ShouldContain("[System.State] = 'Active'");
    }

    [Fact]
    public void Build_AssignedToFilter_EqualsClause()
    {
        var result = WiqlQueryBuilder.Build(new QueryParameters { AssignedToFilter = "Jane Doe" });

        result.ShouldContain("[System.AssignedTo] = 'Jane Doe'");
    }

    [Fact]
    public void Build_AreaPathFilter_UnderClause()
    {
        var result = WiqlQueryBuilder.Build(new QueryParameters { AreaPathFilter = @"Project\TeamA" });

        result.ShouldContain(@"[System.AreaPath] UNDER 'Project\TeamA'");
    }

    [Fact]
    public void Build_IterationPathFilter_UnderClause()
    {
        var result = WiqlQueryBuilder.Build(new QueryParameters { IterationPathFilter = @"Project\Sprint 1" });

        result.ShouldContain(@"[System.IterationPath] UNDER 'Project\Sprint 1'");
    }

    [Fact]
    public void Build_CreatedSinceDays_DateClause()
    {
        var result = WiqlQueryBuilder.Build(new QueryParameters { CreatedSinceDays = 7 });

        result.ShouldContain("[System.CreatedDate] >= @Today - 7");
    }

    [Fact]
    public void Build_ChangedSinceDays_SevenDays_ProducesTodayMinus7()
    {
        var result = WiqlQueryBuilder.Build(new QueryParameters { ChangedSinceDays = 7 });

        result.ShouldContain("[System.ChangedDate] >= @Today - 7");
    }

    // ═══════════════════════════════════════════════════════════════
    //  DefaultAreaPaths
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_DefaultAreaPaths_SingleWithIncludeChildren_UnderClause()
    {
        var paths = new List<(string Path, bool IncludeChildren)>
        {
            (@"Project\TeamA", true),
        };

        var result = WiqlQueryBuilder.Build(new QueryParameters { DefaultAreaPaths = paths });

        result.ShouldContain(@"[System.AreaPath] UNDER 'Project\TeamA'");
    }

    [Fact]
    public void Build_DefaultAreaPaths_SingleWithoutIncludeChildren_EqualsClause()
    {
        var paths = new List<(string Path, bool IncludeChildren)>
        {
            (@"Project\TeamB", false),
        };

        var result = WiqlQueryBuilder.Build(new QueryParameters { DefaultAreaPaths = paths });

        result.ShouldContain(@"[System.AreaPath] = 'Project\TeamB'");
        result.ShouldNotContain("UNDER");
    }

    [Fact]
    public void Build_DefaultAreaPaths_Multiple_OrJoined()
    {
        var paths = new List<(string Path, bool IncludeChildren)>
        {
            (@"Project\TeamA", true),
            (@"Project\TeamB", false),
        };

        var result = WiqlQueryBuilder.Build(new QueryParameters { DefaultAreaPaths = paths });

        result.ShouldContain(@"[System.AreaPath] UNDER 'Project\TeamA'");
        result.ShouldContain(@"[System.AreaPath] = 'Project\TeamB'");
        result.ShouldContain(" OR ");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Combined filters
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_AllFilters_ProducesAndJoinedClauses()
    {
        var parameters = new QueryParameters
        {
            SearchText = "login",
            TypeFilter = "Bug",
            StateFilter = "Active",
            AssignedToFilter = "Jane Doe",
            AreaPathFilter = @"Project\TeamA",
            IterationPathFilter = @"Project\Sprint 1",
            CreatedSinceDays = 7,
            ChangedSinceDays = 3,
        };

        var result = WiqlQueryBuilder.Build(parameters);

        result.ShouldStartWith("SELECT [System.Id] FROM WorkItems WHERE ");
        result.ShouldEndWith(" ORDER BY [System.ChangedDate] DESC");

        // Verify AND-joined structure (search text clause is parenthesized)
        result.ShouldContain(" AND [System.WorkItemType] = 'Bug'");
        result.ShouldContain(" AND [System.State] = 'Active'");
        result.ShouldContain(" AND [System.AssignedTo] = 'Jane Doe'");
        result.ShouldContain("[System.ChangedDate] >= @Today - 3");
        result.ShouldContain("[System.CreatedDate] >= @Today - 7");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Edge cases
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_WhitespaceOrNullSearchText_NoContainsClause(string? searchText)
    {
        var result = WiqlQueryBuilder.Build(new QueryParameters { SearchText = searchText });

        result.ShouldNotContain("CONTAINS");
    }

    [Fact]
    public void Build_SearchTextWithSingleQuote_EscapedCorrectly()
    {
        var result = WiqlQueryBuilder.Build(new QueryParameters { SearchText = "can't login" });

        result.ShouldContain("CONTAINS 'can''t login'");
    }

    [Fact]
    public void Build_TypeFilterWithSingleQuote_EscapedCorrectly()
    {
        var result = WiqlQueryBuilder.Build(new QueryParameters { TypeFilter = "User's Story" });

        result.ShouldContain("[System.WorkItemType] = 'User''s Story'");
    }

    [Fact]
    public void Build_AssignedToFilter_OBrien_QuotesDoubled()
    {
        var result = WiqlQueryBuilder.Build(new QueryParameters { AssignedToFilter = "O'Brien" });

        result.ShouldContain("[System.AssignedTo] = 'O''Brien'");
    }

    [Fact]
    public void Build_StateFilter_WontFix_QuotesDoubled()
    {
        var result = WiqlQueryBuilder.Build(new QueryParameters { StateFilter = "Won't Fix" });

        result.ShouldContain("[System.State] = 'Won''t Fix'");
    }

    [Theory]
    [InlineData("'; DROP TABLE WorkItems --", "''; DROP TABLE WorkItems --")]
    [InlineData("' OR 1=1 --", "'' OR 1=1 --")]
    [InlineData("'''", "''''''")]
    public void Build_SearchText_InjectionPatterns_AllQuotesEscaped(
        string malicious, string expectedEscaped)
    {
        var result = WiqlQueryBuilder.Build(new QueryParameters { SearchText = malicious });

        // Every single-quote in the input must be doubled so it cannot
        // break out of the WIQL string literal context.
        result.ShouldContain($"CONTAINS '{expectedEscaped}'");
    }

    [Fact]
    public void Build_TopValue_NeverAppearsInWiql()
    {
        var result = WiqlQueryBuilder.Build(new QueryParameters { Top = 50, TypeFilter = "Bug" });

        result.ShouldNotContain("TOP");
        result.ShouldNotContain("top");
    }

    [Fact]
    public void Build_AreaPathFilterAndDefaultAreaPaths_BothIncluded()
    {
        var defaults = new List<(string Path, bool IncludeChildren)>
        {
            (@"Project\Default", true),
        };

        var result = WiqlQueryBuilder.Build(new QueryParameters
        {
            AreaPathFilter = @"Project\Explicit",
            DefaultAreaPaths = defaults,
        });

        // Both explicit and default area path clauses should appear
        result.ShouldContain(@"[System.AreaPath] UNDER 'Project\Explicit'");
        result.ShouldContain(@"[System.AreaPath] UNDER 'Project\Default'");
    }

    [Fact]
    public void Build_EmptyDefaultAreaPaths_NoAreaPathClause()
    {
        var result = WiqlQueryBuilder.Build(new QueryParameters
        {
            DefaultAreaPaths = new List<(string Path, bool IncludeChildren)>(),
        });

        result.ShouldNotContain("System.AreaPath");
    }

    [Fact]
    public void Build_ChangedSinceDays_Zero_ProducesValidClause()
    {
        var result = WiqlQueryBuilder.Build(new QueryParameters { ChangedSinceDays = 0 });

        result.ShouldContain("[System.ChangedDate] >= @Today - 0");
    }

    [Fact]
    public void Build_DefaultAreaPaths_PathWithSingleQuote_Escaped()
    {
        var paths = new List<(string Path, bool IncludeChildren)>
        {
            ("Team's Area", true),
        };

        var result = WiqlQueryBuilder.Build(new QueryParameters { DefaultAreaPaths = paths });

        result.ShouldContain("[System.AreaPath] UNDER 'Team''s Area'");
    }
}