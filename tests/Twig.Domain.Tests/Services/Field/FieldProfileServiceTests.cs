using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services.Field;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services.Field;

public class FieldProfileServiceTests
{
    private static WorkItem CreateItem(int id, string type, Dictionary<string, string?>? fields = null)
    {
        var typeResult = WorkItemType.Parse(type);
        var item = new WorkItem
        {
            Id = id,
            Type = typeResult.IsSuccess ? typeResult.Value : WorkItemType.Task,
            Title = $"Item {id}",
            State = "Active",
        };
        if (fields is not null)
        {
            foreach (var kvp in fields)
                item.SetField(kvp.Key, kvp.Value);
        }
        return item;
    }

    [Fact]
    public void ComputeProfiles_EmptyItems_ReturnsEmpty()
    {
        var result = FieldProfileService.ComputeProfiles(Array.Empty<WorkItem>());
        result.ShouldBeEmpty();
    }

    [Fact]
    public void ComputeProfiles_ExcludesCoreFields()
    {
        var items = new[]
        {
            CreateItem(1, "Task", new Dictionary<string, string?>
            {
                ["System.Id"] = "1",
                ["System.Title"] = "Test",
                ["System.State"] = "Active",
                ["System.WorkItemType"] = "Task",
                ["System.AssignedTo"] = "Alice",
                ["System.IterationPath"] = @"Project\Sprint 1",
                ["System.AreaPath"] = @"Project\Team",
                ["Microsoft.VSTS.Common.Priority"] = "1",
            })
        };

        var profiles = FieldProfileService.ComputeProfiles(items);

        profiles.ShouldNotBeEmpty();
        profiles.ShouldAllBe(p => !p.ReferenceName.Equals("System.Id", StringComparison.OrdinalIgnoreCase));
        profiles.ShouldAllBe(p => !p.ReferenceName.Equals("System.Title", StringComparison.OrdinalIgnoreCase));
        profiles.ShouldAllBe(p => !p.ReferenceName.Equals("System.State", StringComparison.OrdinalIgnoreCase));
        profiles.ShouldAllBe(p => !p.ReferenceName.Equals("System.WorkItemType", StringComparison.OrdinalIgnoreCase));
        profiles.ShouldAllBe(p => !p.ReferenceName.Equals("System.AssignedTo", StringComparison.OrdinalIgnoreCase));

        // Priority should be included
        profiles.ShouldContain(p => p.ReferenceName == "Microsoft.VSTS.Common.Priority");
    }

    [Fact]
    public void ComputeProfiles_ComputesFillRateCorrectly()
    {
        var items = new[]
        {
            CreateItem(1, "Task", new Dictionary<string, string?>
            {
                ["Microsoft.VSTS.Common.Priority"] = "1",
                ["Microsoft.VSTS.Scheduling.StoryPoints"] = "5",
            }),
            CreateItem(2, "Task", new Dictionary<string, string?>
            {
                ["Microsoft.VSTS.Common.Priority"] = "2",
                // StoryPoints is absent
            }),
            CreateItem(3, "Task", new Dictionary<string, string?>
            {
                ["Microsoft.VSTS.Common.Priority"] = "1",
                ["Microsoft.VSTS.Scheduling.StoryPoints"] = null,
            }),
        };

        var profiles = FieldProfileService.ComputeProfiles(items);

        var priority = profiles.First(p => p.ReferenceName == "Microsoft.VSTS.Common.Priority");
        priority.FillRate.ShouldBe(1.0); // 3/3

        var storyPoints = profiles.First(p => p.ReferenceName == "Microsoft.VSTS.Scheduling.StoryPoints");
        storyPoints.FillRate.ShouldBe(1.0 / 3.0, 0.01); // 1/3 (null and missing don't count)
    }

    [Fact]
    public void ComputeProfiles_SortedByFillRateDescending()
    {
        var items = new[]
        {
            CreateItem(1, "Task", new Dictionary<string, string?>
            {
                ["Microsoft.VSTS.Common.Priority"] = "1",
                ["System.Tags"] = "tag1",
                ["Microsoft.VSTS.Scheduling.StoryPoints"] = "5",
            }),
            CreateItem(2, "Task", new Dictionary<string, string?>
            {
                ["Microsoft.VSTS.Common.Priority"] = "2",
                // Tags missing
                // StoryPoints missing
            }),
        };

        var profiles = FieldProfileService.ComputeProfiles(items);

        profiles.Count.ShouldBeGreaterThanOrEqualTo(2);
        // Priority should be first (fill rate 1.0)
        profiles[0].ReferenceName.ShouldBe("Microsoft.VSTS.Common.Priority");
        profiles[0].FillRate.ShouldBe(1.0);
    }

    [Fact]
    public void ComputeProfiles_CollectsSampleValues()
    {
        var items = new[]
        {
            CreateItem(1, "Task", new Dictionary<string, string?> { ["System.Tags"] = "tag1" }),
            CreateItem(2, "Task", new Dictionary<string, string?> { ["System.Tags"] = "tag2" }),
            CreateItem(3, "Task", new Dictionary<string, string?> { ["System.Tags"] = "tag3" }),
            CreateItem(4, "Task", new Dictionary<string, string?> { ["System.Tags"] = "tag4" }),
        };

        var profiles = FieldProfileService.ComputeProfiles(items);
        var tags = profiles.First(p => p.ReferenceName == "System.Tags");

        // Should cap at 3 sample values
        tags.SampleValues.Count.ShouldBe(3);
    }

    [Fact]
    public void ComputeProfiles_IgnoresWhitespaceOnlyValues()
    {
        var items = new[]
        {
            CreateItem(1, "Task", new Dictionary<string, string?> { ["System.Tags"] = "   " }),
            CreateItem(2, "Task", new Dictionary<string, string?> { ["System.Tags"] = "real" }),
        };

        var profiles = FieldProfileService.ComputeProfiles(items);
        var tags = profiles.First(p => p.ReferenceName == "System.Tags");
        tags.FillRate.ShouldBe(0.5); // only 1 of 2 has non-whitespace
    }
}
