using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Extensions;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Extensions;

public class ProcessConfigExtensionsTests
{
    [Fact]
    public void SafeGetConfiguration_NullProvider_ReturnsNull()
    {
        IProcessConfigurationProvider? provider = null;

        var result = provider.SafeGetConfiguration("Task");

        result.ShouldBeNull();
    }

    [Fact]
    public void SafeGetConfiguration_ProviderThrows_ReturnsNull()
    {
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Throws(new InvalidOperationException("No config"));

        var result = provider.SafeGetConfiguration("Task");

        result.ShouldBeNull();
    }

    [Fact]
    public void SafeGetConfiguration_ValidType_ReturnsTypeConfig()
    {
        var config = ProcessConfigBuilder.Agile();
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(config);

        var result = provider.SafeGetConfiguration("User Story");

        result.ShouldNotBeNull();
        result.StateEntries.ShouldNotBeEmpty();
    }

    [Fact]
    public void SafeGetConfiguration_UnknownType_ReturnsNull()
    {
        var config = ProcessConfigBuilder.Basic();
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(config);

        var result = provider.SafeGetConfiguration("NonExistentType");

        result.ShouldBeNull();
    }

    [Fact]
    public void SafeGetConfiguration_EmptyType_ReturnsNull()
    {
        var config = ProcessConfigBuilder.Basic();
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(config);

        var result = provider.SafeGetConfiguration("");

        result.ShouldBeNull();
    }

    [Fact]
    public void SafeGetConfiguration_AgileTask_HasClosedAsCompleted()
    {
        var config = ProcessConfigBuilder.Agile();
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(config);

        var result = provider.SafeGetConfiguration("Task");

        result.ShouldNotBeNull();
        result.StateEntries.ShouldContain(e =>
            e.Name == "Closed" && e.Category == Enums.StateCategory.Completed);
    }

    [Fact]
    public void SafeGetConfiguration_BasicTask_HasDoneAsCompleted()
    {
        var config = ProcessConfigBuilder.Basic();
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(config);

        var result = provider.SafeGetConfiguration("Task");

        result.ShouldNotBeNull();
        result.StateEntries.ShouldContain(e =>
            e.Name == "Done" && e.Category == Enums.StateCategory.Completed);
    }

    [Fact]
    public void SafeGetConfiguration_CaseInsensitiveTypeLookup()
    {
        var config = ProcessConfigBuilder.Agile();
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(config);

        // WorkItemType.Parse normalises known types
        var result = provider.SafeGetConfiguration("user story");

        result.ShouldNotBeNull();
    }

    // ── ComputeChildProgress ────────────────────────────────────────

    [Fact]
    public void ComputeChildProgress_NullProvider_FallsBackToHeuristic()
    {
        IProcessConfigurationProvider? provider = null;
        var children = new[]
        {
            CreateWorkItem(1, "Task", "Done"),
            CreateWorkItem(2, "Task", "Doing"),
        };

        var result = provider.ComputeChildProgress(children);

        result.ShouldNotBeNull();
        result.Value.Done.ShouldBe(1);
        result.Value.Total.ShouldBe(2);
    }

    [Fact]
    public void ComputeChildProgress_EmptyChildren_ReturnsNull()
    {
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(ProcessConfigBuilder.Basic());

        var result = provider.ComputeChildProgress(Array.Empty<WorkItem>());

        result.ShouldBeNull();
    }

    [Fact]
    public void ComputeChildProgress_BasicConfig_TwoDoneOneDoing_Returns2Of3()
    {
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(ProcessConfigBuilder.Basic());
        var children = new[]
        {
            CreateWorkItem(1, "Issue", "Done"),
            CreateWorkItem(2, "Issue", "Done"),
            CreateWorkItem(3, "Issue", "Doing"),
        };

        var result = provider.ComputeChildProgress(children);

        result.ShouldNotBeNull();
        result.Value.Done.ShouldBe(2);
        result.Value.Total.ShouldBe(3);
    }

    [Fact]
    public void ComputeChildProgress_ScrumConfig_OneDoneOneNew_Returns1Of2()
    {
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(ProcessConfigBuilder.Scrum());
        var children = new[]
        {
            CreateWorkItem(1, "Product Backlog Item", "Done"),
            CreateWorkItem(2, "Product Backlog Item", "New"),
        };

        var result = provider.ComputeChildProgress(children);

        result.ShouldNotBeNull();
        result.Value.Done.ShouldBe(1);
        result.Value.Total.ShouldBe(2);
    }

    [Fact]
    public void ComputeChildProgress_ProviderThrows_FallsBackToHeuristic()
    {
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Throws(new InvalidOperationException("No config"));
        var children = new[]
        {
            CreateWorkItem(1, "Task", "Active"),
            CreateWorkItem(2, "Task", "Active"),
        };

        var result = provider.ComputeChildProgress(children);

        result.ShouldNotBeNull();
        result.Value.Done.ShouldBe(0);
        result.Value.Total.ShouldBe(2);
    }

    private static WorkItem CreateWorkItem(int id, string typeName, string state)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Parse(typeName).Value,
            Title = $"Test {id}",
            State = state,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
