using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public sealed class CreateWorkItemRequestTests
{
    [Fact]
    public void Construction_RequiredPropertiesOnly_SetsDefaults()
    {
        var request = new CreateWorkItemRequest
        {
            TypeName = "User Story",
            Title = "Implement feature",
        };

        request.TypeName.ShouldBe("User Story");
        request.Title.ShouldBe("Implement feature");
        request.AreaPath.ShouldBeNull();
        request.IterationPath.ShouldBeNull();
        request.ParentId.ShouldBeNull();
        request.Fields.ShouldBeEmpty();
    }

    [Fact]
    public void Construction_AllProperties_SetsValues()
    {
        var fields = new Dictionary<string, string?>
        {
            ["System.Description"] = "A description",
            ["Custom.Priority"] = "High",
        };

        var request = new CreateWorkItemRequest
        {
            TypeName = "Bug",
            Title = "Fix crash",
            AreaPath = "Project\\Team A",
            IterationPath = "Project\\Sprint 3",
            ParentId = 42,
            Fields = fields,
        };

        request.TypeName.ShouldBe("Bug");
        request.Title.ShouldBe("Fix crash");
        request.AreaPath.ShouldBe("Project\\Team A");
        request.IterationPath.ShouldBe("Project\\Sprint 3");
        request.ParentId.ShouldBe(42);
        request.Fields.Count.ShouldBe(2);
        request.Fields["System.Description"].ShouldBe("A description");
        request.Fields["Custom.Priority"].ShouldBe("High");
    }

    [Fact]
    public void Fields_DefaultsToEmptyDictionary_NotNull()
    {
        var request = new CreateWorkItemRequest
        {
            TypeName = "Task",
            Title = "Test",
        };

        request.Fields.ShouldNotBeNull();
        request.Fields.ShouldBeEmpty();
    }

    [Fact]
    public void Fields_WithNullValue_Preserved()
    {
        var request = new CreateWorkItemRequest
        {
            TypeName = "Task",
            Title = "Test",
            Fields = new Dictionary<string, string?>
            {
                ["Custom.Optional"] = null,
            },
        };

        request.Fields.ShouldContainKey("Custom.Optional");
        request.Fields["Custom.Optional"].ShouldBeNull();
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var fields = new Dictionary<string, string?> { ["Key"] = "Value" };

        var a = new CreateWorkItemRequest
        {
            TypeName = "Task",
            Title = "Test",
            AreaPath = "Project",
            IterationPath = "Project\\Sprint 1",
            ParentId = 5,
            Fields = fields,
        };

        var b = new CreateWorkItemRequest
        {
            TypeName = "Task",
            Title = "Test",
            AreaPath = "Project",
            IterationPath = "Project\\Sprint 1",
            ParentId = 5,
            Fields = fields, // same reference — record equality uses reference equality for collections
        };

        a.ShouldBe(b);
    }

    [Fact]
    public void Inequality_DifferentTypeName()
    {
        var a = new CreateWorkItemRequest { TypeName = "Bug", Title = "Test" };
        var b = new CreateWorkItemRequest { TypeName = "Task", Title = "Test" };

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_DifferentTitle()
    {
        var a = new CreateWorkItemRequest { TypeName = "Task", Title = "Alpha" };
        var b = new CreateWorkItemRequest { TypeName = "Task", Title = "Beta" };

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_DifferentAreaPath()
    {
        var a = new CreateWorkItemRequest { TypeName = "Task", Title = "Test", AreaPath = "A" };
        var b = new CreateWorkItemRequest { TypeName = "Task", Title = "Test", AreaPath = "B" };

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_DifferentIterationPath()
    {
        var a = new CreateWorkItemRequest { TypeName = "Task", Title = "Test", IterationPath = "Sprint 1" };
        var b = new CreateWorkItemRequest { TypeName = "Task", Title = "Test", IterationPath = "Sprint 2" };

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_DifferentParentId()
    {
        var a = new CreateWorkItemRequest { TypeName = "Task", Title = "Test", ParentId = 1 };
        var b = new CreateWorkItemRequest { TypeName = "Task", Title = "Test", ParentId = 2 };

        a.ShouldNotBe(b);
    }

    [Fact]
    public void WithExpression_CreatesModifiedCopy()
    {
        var original = new CreateWorkItemRequest
        {
            TypeName = "Task",
            Title = "Original",
            ParentId = 10,
        };

        var modified = original with { Title = "Modified", ParentId = 20 };

        modified.TypeName.ShouldBe("Task");
        modified.Title.ShouldBe("Modified");
        modified.ParentId.ShouldBe(20);

        // Original unchanged
        original.Title.ShouldBe("Original");
        original.ParentId.ShouldBe(10);
    }

    [Fact]
    public void WithExpression_FieldsDefaultPreserved()
    {
        var original = new CreateWorkItemRequest
        {
            TypeName = "Task",
            Title = "Test",
        };

        var modified = original with { Title = "Updated" };

        modified.Fields.ShouldBeEmpty();
    }

    [Fact]
    public void ParentId_Zero_IsDistinctFromNull()
    {
        var withZero = new CreateWorkItemRequest { TypeName = "Task", Title = "T", ParentId = 0 };
        var withNull = new CreateWorkItemRequest { TypeName = "Task", Title = "T", ParentId = null };

        withZero.ParentId.ShouldBe(0);
        withNull.ParentId.ShouldBeNull();
        withZero.ShouldNotBe(withNull);
    }
}
