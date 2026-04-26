using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Aggregates;

public class ProcessTypeRecordTests
{
    [Fact]
    public void ProcessTypeRecord_DefaultValues_AreEmpty()
    {
        var record = new ProcessTypeRecord();

        record.TypeName.ShouldBe(string.Empty);
        record.States.ShouldBeEmpty();
        record.ValidChildTypes.ShouldBeEmpty();
        record.DefaultChildType.ShouldBeNull();
        record.ColorHex.ShouldBeNull();
        record.IconId.ShouldBeNull();
    }

    [Fact]
    public void ProcessTypeRecord_InitProperties_RoundTrip()
    {
        var states = new StateEntry[]
        {
            new("New", StateCategory.Proposed, null),
            new("Active", StateCategory.InProgress, null),
            new("Done", StateCategory.Completed, null),
        };

        var record = new ProcessTypeRecord
        {
            TypeName = "Scenario",
            States = states,
            DefaultChildType = "Task",
            ValidChildTypes = new[] { "Task", "Subtask" },
            ColorHex = "009CCC",
            IconId = "icon_list",
        };

        record.TypeName.ShouldBe("Scenario");
        record.States.Select(s => s.Name).ShouldBe(new[] { "New", "Active", "Done" });
        record.DefaultChildType.ShouldBe("Task");
        record.ValidChildTypes.ShouldBe(new[] { "Task", "Subtask" });
        record.ColorHex.ShouldBe("009CCC");
        record.IconId.ShouldBe("icon_list");
    }
}

public class ProcessConfigurationFromRecordsTests
{
    private static StateEntry[] ToStateEntries(params string[] names) =>
        names.Select(n => new StateEntry(n, StateCategory.Unknown, null)).ToArray();

    [Fact]
    public void FromRecords_CustomTypeRecord_AddsCustomTypeConfig()
    {
        var records = new[]
        {
            new ProcessTypeRecord
            {
                TypeName = "Scenario",
                States = ToStateEntries("Draft", "Active", "Done"),
                ValidChildTypes = new[] { "Task" },
                DefaultChildType = "Task",
            },
        };

        var config = ProcessConfiguration.FromRecords(records);

        var scenarioType = WorkItemType.Parse("Scenario").Value;
        config.TypeConfigs.ShouldContainKey(scenarioType);
        var typeConfig = config.TypeConfigs[scenarioType];
        typeConfig.States.ShouldBe(new[] { "Draft", "Active", "Done" });
        typeConfig.AllowedChildTypes.ShouldBe(new[] { WorkItemType.Task });
    }

    [Fact]
    public void FromRecords_StandardTypeRecord_CreatesConfig()
    {
        var records = new[]
        {
            new ProcessTypeRecord
            {
                TypeName = "Bug",
                States = ToStateEntries("New", "InReview", "Fixed"),
                ValidChildTypes = Array.Empty<string>(),
            },
        };

        var config = ProcessConfiguration.FromRecords(records);

        var typeConfig = config.TypeConfigs[WorkItemType.Bug];
        typeConfig.States.ShouldBe(new[] { "New", "InReview", "Fixed" });
    }

    [Fact]
    public void FromRecords_RecordWithNoStates_IsSkipped()
    {
        var records = new[]
        {
            new ProcessTypeRecord
            {
                TypeName = "EmptyType",
                States = Array.Empty<StateEntry>(),
            },
        };

        var config = ProcessConfiguration.FromRecords(records);

        var emptyType = WorkItemType.Parse("EmptyType").Value;
        config.TypeConfigs.ShouldNotContainKey(emptyType);
    }

    [Fact]
    public void FromRecords_RecordWithEmptyTypeName_IsSkipped()
    {
        var records = new[]
        {
            new ProcessTypeRecord
            {
                TypeName = "",
                States = ToStateEntries("New", "Done"),
            },
        };

        var config = ProcessConfiguration.FromRecords(records);

        config.TypeConfigs.ShouldBeEmpty();
    }

    [Fact]
    public void FromRecords_TransitionRules_GeneratedForCustomType()
    {
        var records = new[]
        {
            new ProcessTypeRecord
            {
                TypeName = "Deliverable",
                States = new StateEntry[]
                {
                    new("Draft", StateCategory.Proposed, null),
                    new("Active", StateCategory.InProgress, null),
                    new("Closed", StateCategory.Completed, null),
                    new("Removed", StateCategory.Removed, null),
                },
                ValidChildTypes = Array.Empty<string>(),
            },
        };

        var config = ProcessConfiguration.FromRecords(records);
        var deliverableType = WorkItemType.Parse("Deliverable").Value;
        var typeConfig = config.TypeConfigs[deliverableType];

        // Draft → Active = Forward
        typeConfig.TransitionRules[("Draft", "Active")].ShouldBe(TransitionKind.Forward);
        // Active → Draft = Forward (ordinal-backward is now Forward)
        typeConfig.TransitionRules[("Active", "Draft")].ShouldBe(TransitionKind.Forward);
        // Any → Removed = Cut
        typeConfig.TransitionRules[("Active", "Removed")].ShouldBe(TransitionKind.Cut);
    }
}
