using Shouldly;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.TestKit;

public class ProcessConfigBuilderTests
{
    [Fact]
    public void Agile_ContainsAllStandardTypes()
    {
        var config = ProcessConfigBuilder.Agile();

        config.TypeConfigs.ShouldContainKey(WorkItemType.Epic);
        config.TypeConfigs.ShouldContainKey(WorkItemType.Feature);
        config.TypeConfigs.ShouldContainKey(WorkItemType.UserStory);
        config.TypeConfigs.ShouldContainKey(WorkItemType.Bug);
        config.TypeConfigs.ShouldContainKey(WorkItemType.Task);
    }

    [Fact]
    public void Agile_UserStoryStates_HaveCorrectCategories()
    {
        var config = ProcessConfigBuilder.Agile();
        var storyConfig = config.TypeConfigs[WorkItemType.UserStory];

        storyConfig.StateEntries.Count.ShouldBe(5);
        storyConfig.StateEntries[0].Name.ShouldBe("New");
        storyConfig.StateEntries[0].Category.ShouldBe(StateCategory.Proposed);
        storyConfig.StateEntries[1].Name.ShouldBe("Active");
        storyConfig.StateEntries[1].Category.ShouldBe(StateCategory.InProgress);
        storyConfig.StateEntries[2].Name.ShouldBe("Resolved");
        storyConfig.StateEntries[2].Category.ShouldBe(StateCategory.Resolved);
        storyConfig.StateEntries[3].Name.ShouldBe("Closed");
        storyConfig.StateEntries[3].Category.ShouldBe(StateCategory.Completed);
        storyConfig.StateEntries[4].Name.ShouldBe("Removed");
        storyConfig.StateEntries[4].Category.ShouldBe(StateCategory.Removed);
    }

    [Fact]
    public void Agile_ChildTypeHierarchy_IsCorrect()
    {
        var config = ProcessConfigBuilder.Agile();

        config.GetAllowedChildTypes(WorkItemType.Epic).ShouldContain(WorkItemType.Feature);
        config.GetAllowedChildTypes(WorkItemType.Feature).ShouldContain(WorkItemType.UserStory);
        config.GetAllowedChildTypes(WorkItemType.Feature).ShouldContain(WorkItemType.Bug);
        config.GetAllowedChildTypes(WorkItemType.UserStory).ShouldContain(WorkItemType.Task);
    }

    [Fact]
    public void Agile_ForwardTransition_IsForward()
    {
        var config = ProcessConfigBuilder.Agile();
        config.GetTransitionKind(WorkItemType.UserStory, "New", "Active").ShouldBe(TransitionKind.Forward);
    }

    [Fact]
    public void Agile_CutTransition_IsCut()
    {
        var config = ProcessConfigBuilder.Agile();
        config.GetTransitionKind(WorkItemType.UserStory, "Active", "Removed").ShouldBe(TransitionKind.Cut);
    }

    [Fact]
    public void AgileUserStoryOnly_ContainsOnlyUserStory()
    {
        var config = ProcessConfigBuilder.AgileUserStoryOnly();

        config.TypeConfigs.Count.ShouldBe(1);
        config.TypeConfigs.ShouldContainKey(WorkItemType.UserStory);
    }

    [Fact]
    public void Scrum_ContainsAllStandardTypes()
    {
        var config = ProcessConfigBuilder.Scrum();

        config.TypeConfigs.ShouldContainKey(WorkItemType.Epic);
        config.TypeConfigs.ShouldContainKey(WorkItemType.Feature);
        config.TypeConfigs.ShouldContainKey(WorkItemType.ProductBacklogItem);
        config.TypeConfigs.ShouldContainKey(WorkItemType.Bug);
        config.TypeConfigs.ShouldContainKey(WorkItemType.Task);
    }

    [Fact]
    public void Scrum_PBI_ForwardTransition()
    {
        var config = ProcessConfigBuilder.Scrum();
        config.GetTransitionKind(WorkItemType.ProductBacklogItem, "New", "Approved").ShouldBe(TransitionKind.Forward);
    }

    [Fact]
    public void Basic_ContainsAllTypes()
    {
        var config = ProcessConfigBuilder.Basic();

        config.TypeConfigs.ShouldContainKey(WorkItemType.Epic);
        config.TypeConfigs.ShouldContainKey(WorkItemType.Issue);
        config.TypeConfigs.ShouldContainKey(WorkItemType.Task);
    }

    [Fact]
    public void Basic_ForwardTransition()
    {
        var config = ProcessConfigBuilder.Basic();
        config.GetTransitionKind(WorkItemType.Issue, "To Do", "Doing").ShouldBe(TransitionKind.Forward);
    }

    [Fact]
    public void Cmmi_ContainsAllTypes()
    {
        var config = ProcessConfigBuilder.Cmmi();

        config.TypeConfigs.ShouldContainKey(WorkItemType.Epic);
        config.TypeConfigs.ShouldContainKey(WorkItemType.Feature);
        config.TypeConfigs.ShouldContainKey(WorkItemType.Requirement);
        config.TypeConfigs.ShouldContainKey(WorkItemType.Bug);
        config.TypeConfigs.ShouldContainKey(WorkItemType.Task);
    }

    [Fact]
    public void Cmmi_ForwardTransition()
    {
        var config = ProcessConfigBuilder.Cmmi();
        config.GetTransitionKind(WorkItemType.Requirement, "Proposed", "Active").ShouldBe(TransitionKind.Forward);
    }

    [Fact]
    public void Custom_Builder_AddsTypes()
    {
        var config = new ProcessConfigBuilder()
            .AddType("Custom Type", new[] { "Open", "Closed" }, "Sub Type")
            .AddType("Sub Type", new[] { "Open", "Closed" })
            .Build();

        config.TypeConfigs.Count.ShouldBe(2);
    }

    [Fact]
    public void SHelper_CreatesStateEntries()
    {
        var entries = ProcessConfigBuilder.S(
            ("New", StateCategory.Proposed),
            ("Active", StateCategory.InProgress));

        entries.Length.ShouldBe(2);
        entries[0].Name.ShouldBe("New");
        entries[0].Category.ShouldBe(StateCategory.Proposed);
    }
}
