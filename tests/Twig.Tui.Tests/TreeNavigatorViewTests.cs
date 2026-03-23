using NSubstitute;
using Shouldly;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Twig.Tui.Views;
using Xunit;

namespace Twig.Tui.Tests;

public class TreeNavigatorViewTests
{
    private static WorkItem CreateWorkItem(int id, string title, string type = "User Story", int? parentId = null, string state = "Active", string? assignedTo = null)
    {
        var wit = WorkItemType.Parse(type).Value;
        return new WorkItem
        {
            Id = id,
            Title = title,
            Type = wit,
            State = state,
            ParentId = parentId,
            AssignedTo = assignedTo,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project\\Team").Value,
        };
    }

    [Fact]
    public void WorkItemNode_ToString_ShowsIdTypeAndTitle()
    {
        var item = CreateWorkItem(42, "My Story");
        var node = new WorkItemNode(item, isActive: false);

        var text = node.ToString();

        text.ShouldContain("#42");
        text.ShouldContain("My Story");
        text.ShouldContain("User Story");
    }

    [Fact]
    public void WorkItemNode_Active_ShowsMarker()
    {
        var item = CreateWorkItem(7, "Active Item");
        var node = new WorkItemNode(item, isActive: true);

        node.ToString().ShouldStartWith("►");
    }

    [Fact]
    public void WorkItemNode_Inactive_NoMarker()
    {
        var item = CreateWorkItem(7, "Inactive Item");
        var node = new WorkItemNode(item, isActive: false);

        node.ToString().ShouldNotStartWith("►");
    }

    [Fact]
    public void WorkItemNode_ToString_IncludesBadge_WhenProvided()
    {
        var item = CreateWorkItem(42, "My Story");
        var badge = IconSet.ResolveTypeBadge("unicode", "User Story", null);
        var node = new WorkItemNode(item, isActive: false, badge: badge);

        var text = node.ToString();

        text.ShouldContain(badge);
        text.ShouldContain("#42");
    }

    [Fact]
    public void WorkItemNode_ToString_BadgeMatchesResolveTypeBadge()
    {
        // Verify G1: TUI badge matches HumanOutputFormatter/SpectreTheme output
        var types = new[] { "Epic", "Feature", "User Story", "Bug", "Task" };
        foreach (var typeName in types)
        {
            var item = CreateWorkItem(1, "Test", typeName);
            var expectedBadge = IconSet.ResolveTypeBadge("unicode", typeName, null);
            var node = new WorkItemNode(item, badge: expectedBadge);

            node.ToString().ShouldContain(expectedBadge);
        }
    }

    [Fact]
    public void WorkItemNode_ToString_BadgeWithTypeIconIds()
    {
        var typeIconIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bug"] = "icon_insect",
        };
        var item = CreateWorkItem(1, "A Bug", "Bug");
        var badge = IconSet.ResolveTypeBadge("unicode", "Bug", typeIconIds);
        var node = new WorkItemNode(item, badge: badge);

        node.ToString().ShouldContain(badge);
        badge.ShouldBe("✦"); // icon_insect → ✦ in Unicode mode
    }

    [Fact]
    public void WorkItemNode_ToString_DefaultsBadgeToFirstCharOfType()
    {
        var item = CreateWorkItem(1, "Custom Type", "MyCustomType");
        var node = new WorkItemNode(item); // no badge parameter

        node.ToString().ShouldContain("M "); // first char of "MyCustomType", uppercase
    }

    [Fact]
    public void WorkItemTreeBuilder_CanExpand_TrueForNonTask()
    {
        var repo = Substitute.For<IWorkItemRepository>();
        var builder = new WorkItemTreeBuilder(repo);

        var epic = CreateWorkItem(1, "An Epic", "Epic");
        builder.CanExpand(new WorkItemNode(epic)).ShouldBeTrue();
    }

    [Fact]
    public void WorkItemTreeBuilder_CanExpand_FalseForTask()
    {
        var repo = Substitute.For<IWorkItemRepository>();
        var builder = new WorkItemTreeBuilder(repo);

        var task = CreateWorkItem(1, "A Task", "Task");
        builder.CanExpand(new WorkItemNode(task)).ShouldBeFalse();
    }

    [Fact]
    public void WorkItemTreeBuilder_CanExpand_UsesProcessConfig_WhenAvailable()
    {
        var repo = Substitute.For<IWorkItemRepository>();
        var processConfig = Substitute.For<IProcessConfigurationProvider>();
        var config = CreateProcessConfigWithLeafType("Bug");
        processConfig.GetConfiguration().Returns(config);

        var builder = new WorkItemTreeBuilder(repo, processConfig);

        // Bug has no children in this config → not expandable
        var bug = CreateWorkItem(1, "A Bug", "Bug");
        builder.CanExpand(new WorkItemNode(bug)).ShouldBeFalse();

        // User Story has children → expandable
        var story = CreateWorkItem(2, "A Story", "User Story");
        builder.CanExpand(new WorkItemNode(story)).ShouldBeTrue();
    }

    [Fact]
    public void WorkItemTreeBuilder_CanExpand_FallsBackOnConfigError()
    {
        var repo = Substitute.For<IWorkItemRepository>();
        var processConfig = Substitute.For<IProcessConfigurationProvider>();
        processConfig.GetConfiguration().Returns(_ => throw new InvalidOperationException("Config unavailable"));

        var builder = new WorkItemTreeBuilder(repo, processConfig);

        // Falls back to hardcoded check: Task = false
        var task = CreateWorkItem(1, "A Task", "Task");
        builder.CanExpand(new WorkItemNode(task)).ShouldBeFalse();

        // Falls back to hardcoded check: Epic = true
        var epic = CreateWorkItem(2, "An Epic", "Epic");
        builder.CanExpand(new WorkItemNode(epic)).ShouldBeTrue();
    }

    [Fact]
    public void WorkItemTreeBuilder_GetChildren_ReturnsRepoChildren()
    {
        var repo = Substitute.For<IWorkItemRepository>();
        var parent = CreateWorkItem(1, "Parent", "Feature");
        var children = new[]
        {
            CreateWorkItem(2, "Child 1", parentId: 1),
            CreateWorkItem(3, "Child 2", parentId: 1),
        };
        repo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var builder = new WorkItemTreeBuilder(repo);
        var result = builder.GetChildren(new WorkItemNode(parent)).ToList();

        result.Count.ShouldBe(2);
        result[0].WorkItem.Id.ShouldBe(2);
        result[1].WorkItem.Id.ShouldBe(3);
    }

    [Fact]
    public void WorkItemTreeBuilder_GetChildren_IncludesBadges()
    {
        var repo = Substitute.For<IWorkItemRepository>();
        var parent = CreateWorkItem(1, "Parent", "Feature");
        var children = new[]
        {
            CreateWorkItem(2, "Child Story", "User Story", parentId: 1),
            CreateWorkItem(3, "Child Bug", "Bug", parentId: 1),
        };
        repo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var builder = new WorkItemTreeBuilder(repo, iconMode: "unicode");
        var result = builder.GetChildren(new WorkItemNode(parent)).ToList();

        var storyBadge = IconSet.ResolveTypeBadge("unicode", "User Story", null);
        var bugBadge = IconSet.ResolveTypeBadge("unicode", "Bug", null);

        result[0].ToString().ShouldContain(storyBadge);
        result[1].ToString().ShouldContain(bugBadge);
    }

    [Fact]
    public void WorkItemTreeBuilder_GetChildren_EmptyForLeaf()
    {
        var repo = Substitute.For<IWorkItemRepository>();
        var leaf = CreateWorkItem(10, "Leaf", "Feature");
        repo.GetChildrenAsync(10, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var builder = new WorkItemTreeBuilder(repo);
        var result = builder.GetChildren(new WorkItemNode(leaf)).ToList();

        result.ShouldBeEmpty();
    }

    [Fact]
    public void OnKeyDown_J_SetsHandled()
    {
        var repo = Substitute.For<IWorkItemRepository>();
        var contextStore = Substitute.For<IContextStore>();
        var view = new TreeNavigatorView(repo, contextStore);

        var key = new Key(KeyCode.J);
        view.OnKeyDown(null, key);

        key.Handled.ShouldBeTrue();
    }

    [Fact]
    public void OnKeyDown_K_SetsHandled()
    {
        var repo = Substitute.For<IWorkItemRepository>();
        var contextStore = Substitute.For<IContextStore>();
        var view = new TreeNavigatorView(repo, contextStore);

        var key = new Key(KeyCode.K);
        view.OnKeyDown(null, key);

        key.Handled.ShouldBeTrue();
    }

    [Fact]
    public void OnKeyDown_Enter_SetsHandled()
    {
        var repo = Substitute.For<IWorkItemRepository>();
        var contextStore = Substitute.For<IContextStore>();
        var view = new TreeNavigatorView(repo, contextStore);

        var key = new Key(KeyCode.Enter);
        view.OnKeyDown(null, key);

        key.Handled.ShouldBeTrue();
    }

    [Fact]
    public void OnKeyDown_Q_SetsHandled()
    {
        var repo = Substitute.For<IWorkItemRepository>();
        var contextStore = Substitute.For<IContextStore>();
        var view = new TreeNavigatorView(repo, contextStore);

        var key = new Key(KeyCode.Q);
        view.OnKeyDown(null, key);

        key.Handled.ShouldBeTrue();
    }

    [Fact]
    public void OnKeyDown_UnhandledKey_NotHandled()
    {
        var repo = Substitute.For<IWorkItemRepository>();
        var contextStore = Substitute.For<IContextStore>();
        var view = new TreeNavigatorView(repo, contextStore);

        var key = new Key(KeyCode.X);
        view.OnKeyDown(null, key);

        key.Handled.ShouldBeFalse();
    }

    [Fact]
    public async Task ExpandToActiveAsync_TargetInSecondSibling_SelectsCorrectNode()
    {
        // Scenario: Root → [A, B] where A has grandchildren that are NOT the target,
        // and B has a child that IS the target.
        // This verifies BUG-001 fix: the method should not return prematurely after
        // recursing into A's subtree.
        var repo = Substitute.For<IWorkItemRepository>();
        var contextStore = Substitute.For<IContextStore>();

        var root = CreateWorkItem(1, "Root", "Epic");
        var childA = CreateWorkItem(2, "Child A", "Feature", parentId: 1);
        var childB = CreateWorkItem(3, "Child B", "Feature", parentId: 1);
        var grandchildA1 = CreateWorkItem(4, "Grandchild A1", "User Story", parentId: 2);
        var grandchildA2 = CreateWorkItem(5, "Grandchild A2", "User Story", parentId: 2);
        var target = CreateWorkItem(6, "Target in B", "User Story", parentId: 3);

        // Root's children
        repo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { childA, childB });
        // A's children (not the target)
        repo.GetChildrenAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { grandchildA1, grandchildA2 });
        // B's children (contains the target)
        repo.GetChildrenAsync(3, Arg.Any<CancellationToken>())
            .Returns(new[] { target });
        // Leaf nodes
        repo.GetChildrenAsync(4, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        repo.GetChildrenAsync(5, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        repo.GetChildrenAsync(6, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var view = new TreeNavigatorView(repo, contextStore);

        // Manually add root and expand it so the TreeBuilder populates children
        var rootNode = new WorkItemNode(root);
        view._treeView.AddObject(rootNode);

        var found = await view.ExpandToActiveAsync(rootNode, targetId: 6);

        found.ShouldBeTrue();
        view._treeView.SelectedObject.ShouldNotBeNull();
        view._treeView.SelectedObject!.WorkItem.Id.ShouldBe(6);
    }

    [Fact]
    public void WorkItemNode_ToString_ShowsAssignedTo_WhenPresent()
    {
        var item = new WorkItemBuilder(42, "My Story")
            .AsUserStory()
            .AssignedTo("Alice")
            .Build();
        var node = new WorkItemNode(item, isActive: false);

        var text = node.ToString();

        text.ShouldContain("→ Alice");
    }

    [Fact]
    public void WorkItemNode_ToString_OmitsAssignedTo_WhenNull()
    {
        var item = new WorkItemBuilder(42, "My Story")
            .AsUserStory()
            .AssignedTo(null)
            .Build();
        var node = new WorkItemNode(item, isActive: false);

        var text = node.ToString();

        text.ShouldNotContain("→");
    }

    [Fact]
    public void WorkItemNode_ToString_OmitsAssignedTo_WhenEmpty()
    {
        var item = new WorkItemBuilder(42, "My Story")
            .AsUserStory()
            .AssignedTo("")
            .Build();
        var node = new WorkItemNode(item, isActive: false);

        var text = node.ToString();

        text.ShouldNotContain("→");
    }

    [Fact]
    public void WorkItemNode_ToString_ShowsDirtyMarker_WhenDirty()
    {
        var item = new WorkItemBuilder(42, "My Story")
            .AsUserStory()
            .Dirty()
            .Build();
        var node = new WorkItemNode(item, isActive: false);

        var text = node.ToString();

        text.ShouldEndWith("•");
    }

    [Fact]
    public void WorkItemNode_ToString_OmitsDirtyMarker_WhenNotDirty()
    {
        var item = new WorkItemBuilder(42, "My Story")
            .AsUserStory()
            .Build();
        var node = new WorkItemNode(item, isActive: false);

        var text = node.ToString();

        text.ShouldNotContain("•");
    }

    [Fact]
    public void WorkItemNode_ToString_ShowsBothAssignedToAndDirty()
    {
        var item = new WorkItemBuilder(42, "Dirty Assigned Story")
            .AsUserStory()
            .AssignedTo("Bob")
            .Dirty()
            .Build();
        var node = new WorkItemNode(item, isActive: true);

        var text = node.ToString();

        text.ShouldContain("→ Bob");
        text.ShouldContain("•");
        // AssignedTo should appear before dirty marker
        var assignedIdx = text.IndexOf("→ Bob");
        var dirtyIdx = text.IndexOf("•");
        assignedIdx.ShouldBeLessThan(dirtyIdx);
    }

    /// <summary>
    /// Creates a minimal ProcessConfiguration where the specified type is a leaf (no children).
    /// </summary>
    private static ProcessConfiguration CreateProcessConfigWithLeafType(string leafTypeName)
    {
        var records = new[]
        {
            new ProcessTypeRecord
            {
                TypeName = "Epic",
                States = [new StateEntry("Active", StateCategory.InProgress, null)],
                ValidChildTypes = ["Feature"],
            },
            new ProcessTypeRecord
            {
                TypeName = "Feature",
                States = [new StateEntry("Active", StateCategory.InProgress, null)],
                ValidChildTypes = ["User Story"],
            },
            new ProcessTypeRecord
            {
                TypeName = "User Story",
                States = [new StateEntry("Active", StateCategory.InProgress, null)],
                ValidChildTypes = ["Task"],
            },
            new ProcessTypeRecord
            {
                TypeName = "Task",
                States = [new StateEntry("Active", StateCategory.InProgress, null)],
                ValidChildTypes = [],
            },
            new ProcessTypeRecord
            {
                TypeName = leafTypeName,
                States = [new StateEntry("Active", StateCategory.InProgress, null)],
                ValidChildTypes = [],
            },
        };
        return ProcessConfiguration.FromRecords(records);
    }
}
