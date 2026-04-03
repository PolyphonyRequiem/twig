using Shouldly;
using Twig.Domain.Enums;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class StateCategoryResolverTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Resolve — with entries
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_MatchingEntry_ReturnsEntryCategory()
    {
        var entries = new StateEntry[]
        {
            new("Draft", StateCategory.Proposed, null),
            new("Active", StateCategory.InProgress, "009CCC"),
        };

        StateCategoryResolver.Resolve("Active", entries).ShouldBe(StateCategory.InProgress);
    }

    [Fact]
    public void Resolve_MatchingEntry_CaseInsensitive()
    {
        var entries = new StateEntry[]
        {
            new("Active", StateCategory.InProgress, null),
        };

        StateCategoryResolver.Resolve("active", entries).ShouldBe(StateCategory.InProgress);
        StateCategoryResolver.Resolve("ACTIVE", entries).ShouldBe(StateCategory.InProgress);
    }

    [Fact]
    public void Resolve_NonMatchingEntries_FallsBackToFallbackCategory()
    {
        var entries = new StateEntry[]
        {
            new("Draft", StateCategory.Proposed, null),
        };

        // "Active" is not in entries, falls back to FallbackCategory → InProgress
        StateCategoryResolver.Resolve("Active", entries).ShouldBe(StateCategory.InProgress);
    }

    [Fact]
    public void Resolve_NullEntries_FallsBackToFallbackCategory()
    {
        StateCategoryResolver.Resolve("New", null).ShouldBe(StateCategory.Proposed);
        StateCategoryResolver.Resolve("Active", null).ShouldBe(StateCategory.InProgress);
        StateCategoryResolver.Resolve("Closed", null).ShouldBe(StateCategory.Completed);
    }

    // ═══════════════════════════════════════════════════════════════
    //  FallbackCategory — known state names
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("New", StateCategory.Proposed)]
    [InlineData("new", StateCategory.Proposed)]
    [InlineData("To Do", StateCategory.Proposed)]
    [InlineData("to do", StateCategory.Proposed)]
    [InlineData("Proposed", StateCategory.Proposed)]
    [InlineData("proposed", StateCategory.Proposed)]
    public void FallbackCategory_ProposedStates(string state, StateCategory expected)
    {
        StateCategoryResolver.FallbackCategory(state).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Active", StateCategory.InProgress)]
    [InlineData("active", StateCategory.InProgress)]
    [InlineData("Doing", StateCategory.InProgress)]
    [InlineData("doing", StateCategory.InProgress)]
    [InlineData("Committed", StateCategory.InProgress)]
    [InlineData("committed", StateCategory.InProgress)]
    [InlineData("In Progress", StateCategory.InProgress)]
    [InlineData("in progress", StateCategory.InProgress)]
    [InlineData("Approved", StateCategory.InProgress)]
    [InlineData("approved", StateCategory.InProgress)]
    public void FallbackCategory_InProgressStates(string state, StateCategory expected)
    {
        StateCategoryResolver.FallbackCategory(state).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Resolved", StateCategory.Resolved)]
    [InlineData("resolved", StateCategory.Resolved)]
    public void FallbackCategory_ResolvedStates(string state, StateCategory expected)
    {
        StateCategoryResolver.FallbackCategory(state).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Closed", StateCategory.Completed)]
    [InlineData("closed", StateCategory.Completed)]
    [InlineData("Done", StateCategory.Completed)]
    [InlineData("done", StateCategory.Completed)]
    public void FallbackCategory_CompletedStates(string state, StateCategory expected)
    {
        StateCategoryResolver.FallbackCategory(state).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Removed", StateCategory.Removed)]
    [InlineData("removed", StateCategory.Removed)]
    public void FallbackCategory_RemovedStates(string state, StateCategory expected)
    {
        StateCategoryResolver.FallbackCategory(state).ShouldBe(expected);
    }

    // ═══════════════════════════════════════════════════════════════
    //  FallbackCategory — unknown / empty / null
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("SomethingCustom")]
    [InlineData("Draft")]
    [InlineData("Review")]
    public void FallbackCategory_UnknownState_ReturnsUnknown(string state)
    {
        StateCategoryResolver.FallbackCategory(state).ShouldBe(StateCategory.Unknown);
    }

    [Fact]
    public void FallbackCategory_EmptyString_ReturnsUnknown()
    {
        StateCategoryResolver.FallbackCategory("").ShouldBe(StateCategory.Unknown);
    }

    [Fact]
    public void FallbackCategory_Null_ReturnsUnknown()
    {
        StateCategoryResolver.FallbackCategory(null).ShouldBe(StateCategory.Unknown);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ParseCategory — ADO category strings
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Proposed", StateCategory.Proposed)]
    [InlineData("InProgress", StateCategory.InProgress)]
    [InlineData("Resolved", StateCategory.Resolved)]
    [InlineData("Completed", StateCategory.Completed)]
    [InlineData("Removed", StateCategory.Removed)]
    public void ParseCategory_AdoCategoryStrings_ReturnsCorrectCategory(string category, StateCategory expected)
    {
        StateCategoryResolver.ParseCategory(category).ShouldBe(expected);
    }

    [Fact]
    public void ParseCategory_Null_ReturnsUnknown()
    {
        StateCategoryResolver.ParseCategory(null).ShouldBe(StateCategory.Unknown);
    }

    [Theory]
    [InlineData("")]
    [InlineData("proposed")]
    [InlineData("PROPOSED")]
    [InlineData("SomethingElse")]
    public void ParseCategory_UnrecognizedString_ReturnsUnknown(string category)
    {
        StateCategoryResolver.ParseCategory(category).ShouldBe(StateCategory.Unknown);
    }

    [Fact]
    public void ParseCategory_LowercaseInProgress_ReturnsUnknown()
    {
        // ParseCategory is intentionally case-sensitive (ADO returns exact-case strings).
        // "inprogress" does not match "InProgress", so Unknown is returned.
        StateCategoryResolver.ParseCategory("inprogress").ShouldBe(StateCategory.Unknown);
        StateCategoryResolver.ParseCategory("InProgress").ShouldBe(StateCategory.InProgress);
    }

    // ═══════════════════════════════════════════════════════════════
    //  EPIC-004 Task 5: Custom ADO process state
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("CustomPhase")]
    [InlineData("Design Review")]
    [InlineData("Waiting for Approval")]
    [InlineData("QA Testing")]
    [InlineData("Deployed")]
    public void Resolve_CustomState_NotInEntriesOrFallback_ReturnsUnknown(string customState)
    {
        // Custom ADO process states not in the hardcoded fallback map should return Unknown.
        // This is the intended behavior: custom states require authoritative entries from ADO
        // (populated during init/refresh). When entries are absent, Unknown is the safe default.
        var entries = new StateEntry[]
        {
            new("New", StateCategory.Proposed, null),
            new("Active", StateCategory.InProgress, null),
            new("Closed", StateCategory.Completed, null),
        };

        StateCategoryResolver.Resolve(customState, entries).ShouldBe(StateCategory.Unknown);
    }

    [Fact]
    public void Resolve_CustomState_NullEntries_ReturnsUnknown()
    {
        // With no entries and a custom state not in fallback map → Unknown
        StateCategoryResolver.Resolve("CustomPhase", null).ShouldBe(StateCategory.Unknown);
    }

    [Fact]
    public void Resolve_CustomState_EmptyEntries_FallsBackToFallbackCategory()
    {
        // Empty entries list → falls back to FallbackCategory
        StateCategoryResolver.Resolve("CustomPhase", Array.Empty<StateEntry>()).ShouldBe(StateCategory.Unknown);
    }

    [Fact]
    public void FallbackCategory_CustomPhase_ReturnsUnknown()
    {
        // Directly verify the fallback map behavior for custom states
        StateCategoryResolver.FallbackCategory("CustomPhase").ShouldBe(StateCategory.Unknown);
    }

    [Fact]
    public void Resolve_CustomState_InEntries_ReturnsConfiguredCategory()
    {
        // When a custom state IS in the entries (from ADO), it should return the correct category.
        var entries = new StateEntry[]
        {
            new("CustomPhase", StateCategory.InProgress, "009CCC"),
        };

        StateCategoryResolver.Resolve("CustomPhase", entries).ShouldBe(StateCategory.InProgress);
    }

    // ═══════════════════════════════════════════════════════════════
    //  NFR-03: Process template smoke rows with authoritative entries
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Basic", "Done", StateCategory.Completed)]
    [InlineData("Agile", "Closed", StateCategory.Completed)]
    [InlineData("Scrum", "Done", StateCategory.Completed)]
    [InlineData("CMMI", "Resolved", StateCategory.Resolved)]
    public void Resolve_ProcessTemplateCompletionState_WithEntries(
        string template, string stateName, StateCategory expected)
    {
        var config = template switch
        {
            "Basic" => ProcessConfigBuilder.Basic(),
            "Agile" => ProcessConfigBuilder.Agile(),
            "Scrum" => ProcessConfigBuilder.Scrum(),
            "CMMI" => ProcessConfigBuilder.Cmmi(),
            _ => throw new ArgumentOutOfRangeException(nameof(template)),
        };
        var entries = config.TypeConfigs[WorkItemType.Task].StateEntries;

        StateCategoryResolver.Resolve(stateName, entries).ShouldBe(expected);
    }

}
