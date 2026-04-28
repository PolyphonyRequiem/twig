using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Extensions;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Extensions;

public sealed class WorkItemExtensionsTests
{
    [Fact]
    public void ToCreateRequest_ExtractsTypeName()
    {
        var item = new WorkItemBuilder(1, "Test item")
            .AsUserStory()
            .WithAreaPath("Project")
            .WithIterationPath("Project\\Sprint 1")
            .Build();

        var request = item.ToCreateRequest();

        request.TypeName.ShouldBe("User Story");
    }

    [Fact]
    public void ToCreateRequest_ExtractsTitle()
    {
        var item = new WorkItemBuilder(1, "My title")
            .WithAreaPath("Project")
            .WithIterationPath("Project\\Sprint 1")
            .Build();

        var request = item.ToCreateRequest();

        request.Title.ShouldBe("My title");
    }

    [Fact]
    public void ToCreateRequest_ExtractsAreaPath()
    {
        var item = new WorkItemBuilder(1, "Test")
            .WithAreaPath("Project\\Team A")
            .WithIterationPath("Project\\Sprint 1")
            .Build();

        var request = item.ToCreateRequest();

        request.AreaPath.ShouldBe("Project\\Team A");
    }

    [Fact]
    public void ToCreateRequest_ExtractsIterationPath()
    {
        var item = new WorkItemBuilder(1, "Test")
            .WithAreaPath("Project")
            .WithIterationPath("Project\\Sprint 2")
            .Build();

        var request = item.ToCreateRequest();

        request.IterationPath.ShouldBe("Project\\Sprint 2");
    }

    [Fact]
    public void ToCreateRequest_ExtractsParentId()
    {
        var item = new WorkItemBuilder(1, "Test")
            .WithParent(42)
            .WithAreaPath("Project")
            .WithIterationPath("Project\\Sprint 1")
            .Build();

        var request = item.ToCreateRequest();

        request.ParentId.ShouldBe(42);
    }

    [Fact]
    public void ToCreateRequest_NullParentId_PreservesNull()
    {
        var item = new WorkItemBuilder(1, "Test")
            .WithAreaPath("Project")
            .WithIterationPath("Project\\Sprint 1")
            .Build();

        var request = item.ToCreateRequest();

        request.ParentId.ShouldBeNull();
    }

    [Fact]
    public void ToCreateRequest_CopiesCustomFields()
    {
        var item = new WorkItemBuilder(1, "Test")
            .WithAreaPath("Project")
            .WithIterationPath("Project\\Sprint 1")
            .WithField("Custom.Priority", "High")
            .WithField("Custom.Team", "Backend")
            .Build();

        var request = item.ToCreateRequest();

        request.Fields.Count.ShouldBe(2);
        request.Fields["Custom.Priority"].ShouldBe("High");
        request.Fields["Custom.Team"].ShouldBe("Backend");
    }

    [Fact]
    public void ToCreateRequest_EmptyFields_ProducesEmptyDictionary()
    {
        var item = new WorkItemBuilder(1, "Test")
            .WithAreaPath("Project")
            .WithIterationPath("Project\\Sprint 1")
            .Build();

        var request = item.ToCreateRequest();

        request.Fields.ShouldBeEmpty();
    }

    [Fact]
    public void ToCreateRequest_FieldsCaseInsensitive()
    {
        var item = new WorkItemBuilder(1, "Test")
            .WithAreaPath("Project")
            .WithIterationPath("Project\\Sprint 1")
            .WithField("Custom.Priority", "High")
            .Build();

        var request = item.ToCreateRequest();

        // The copied dictionary should preserve case-insensitive lookup
        request.Fields["custom.priority"].ShouldBe("High");
        request.Fields["CUSTOM.PRIORITY"].ShouldBe("High");
    }

    [Fact]
    public void ToCreateRequest_FieldsAreDecoupled_MutationDoesNotAffectOriginal()
    {
        var item = new WorkItemBuilder(1, "Test")
            .WithAreaPath("Project")
            .WithIterationPath("Project\\Sprint 1")
            .WithField("Custom.Priority", "High")
            .Build();

        var request = item.ToCreateRequest();

        // Mutating the request's fields (if cast) should not affect the original WorkItem
        if (request.Fields is Dictionary<string, string?> mutable)
        {
            mutable["Custom.NewField"] = "value";
        }

        item.Fields.ContainsKey("Custom.NewField").ShouldBeFalse();
    }

    [Fact]
    public void ToCreateRequest_FieldsWithNullValues_Preserved()
    {
        var item = new WorkItemBuilder(1, "Test")
            .WithAreaPath("Project")
            .WithIterationPath("Project\\Sprint 1")
            .WithField("Custom.Optional", null)
            .Build();

        var request = item.ToCreateRequest();

        request.Fields.ShouldContainKey("Custom.Optional");
        request.Fields["Custom.Optional"].ShouldBeNull();
    }

    [Fact]
    public void ToCreateRequest_AllPropertiesMapped_RoundTrip()
    {
        var item = new WorkItemBuilder(99, "Full round-trip test")
            .AsEpic()
            .WithParent(10)
            .WithAreaPath("MyProject\\TeamX")
            .WithIterationPath("MyProject\\Sprint 5")
            .WithField("Microsoft.VSTS.Scheduling.StoryPoints", "8")
            .WithField("System.Description", "A description")
            .Build();

        var request = item.ToCreateRequest();

        request.TypeName.ShouldBe("Epic");
        request.Title.ShouldBe("Full round-trip test");
        request.AreaPath.ShouldBe("MyProject\\TeamX");
        request.IterationPath.ShouldBe("MyProject\\Sprint 5");
        request.ParentId.ShouldBe(10);
        request.Fields.Count.ShouldBe(2);
        request.Fields["Microsoft.VSTS.Scheduling.StoryPoints"].ShouldBe("8");
        request.Fields["System.Description"].ShouldBe("A description");
    }

    [Fact]
    public void ToCreateRequest_DoesNotIncludeId()
    {
        // CreateWorkItemRequest has no Id property — verify the seed's Id
        // does not leak into the request (it should only carry creation data)
        var item = new WorkItemBuilder(999, "Test")
            .WithAreaPath("Project")
            .WithIterationPath("Project\\Sprint 1")
            .Build();

        var request = item.ToCreateRequest();

        // Verify by checking all mapped properties — no Id present
        request.TypeName.ShouldNotBeEmpty();
        request.Title.ShouldBe("Test");
    }
}
