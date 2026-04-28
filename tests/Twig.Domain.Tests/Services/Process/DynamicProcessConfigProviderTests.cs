using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Process;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services.Process;

public class DynamicProcessConfigProviderTests
{
    private static ProcessTypeRecord MakeRecord(string typeName, params string[] states) =>
        new() { TypeName = typeName, States = states.Select(s => new StateEntry(s, StateCategory.Unknown, null)).ToArray(), ValidChildTypes = Array.Empty<string>() };

    [Fact]
    public void GetConfiguration_EmptyStore_ThrowsInvalidOperationException()
    {
        var provider = new DynamicProcessConfigProvider(new FakeProcessTypeStore());

        var ex = Should.Throw<InvalidOperationException>(() => provider.GetConfiguration());
        ex.Message.ShouldContain("twig sync");
    }

    [Fact]
    public void GetConfiguration_WithCustomTypeRecords_IncludesCustomTypes()
    {
        var store = new FakeProcessTypeStore(MakeRecord("Scenario", "Draft", "Active", "Done"));
        var provider = new DynamicProcessConfigProvider(store);

        var config = provider.GetConfiguration();

        var scenarioType = WorkItemType.Parse("Scenario").Value;
        config.TypeConfigs.ShouldContainKey(scenarioType);
        config.TypeConfigs[scenarioType].States.ShouldBe(new[] { "Draft", "Active", "Done" });
    }

    [Fact]
    public void GetConfiguration_CalledTwice_ReturnsCachedResult()
    {
        var store = new CountingProcessTypeStore(MakeRecord("Bug", "New", "Active", "Done"));
        var provider = new DynamicProcessConfigProvider(store);

        var config1 = provider.GetConfiguration();
        var config2 = provider.GetConfiguration();

        config1.ShouldBeSameAs(config2);
        store.GetAllCallCount.ShouldBe(1);
    }

    [Fact]
    public void GetConfiguration_MultipleCalls_SingleStoreAccess()
    {
        var store = new CountingProcessTypeStore(MakeRecord("Task", "To Do", "In Progress", "Done"));
        var provider = new DynamicProcessConfigProvider(store);

        provider.GetConfiguration();
        provider.GetConfiguration();
        provider.GetConfiguration();

        store.GetAllCallCount.ShouldBe(1);
    }

    [Fact]
    public void GetConfiguration_WithMultipleTypes_BuildsFullConfig()
    {
        var store = new FakeProcessTypeStore(
            MakeRecord("Epic", "New", "Active", "Done"),
            MakeRecord("Feature", "New", "Active", "Done"),
            MakeRecord("Task", "To Do", "Doing", "Done"));
        var provider = new DynamicProcessConfigProvider(store);

        var config = provider.GetConfiguration();

        config.TypeConfigs.Count.ShouldBe(3);
        config.TypeConfigs.ShouldContainKey(WorkItemType.Epic);
        config.TypeConfigs.ShouldContainKey(WorkItemType.Feature);
        config.TypeConfigs.ShouldContainKey(WorkItemType.Task);
    }

    private sealed class FakeProcessTypeStore : IProcessTypeStore
    {
        private readonly IReadOnlyList<ProcessTypeRecord> _records;

        public FakeProcessTypeStore(params ProcessTypeRecord[] records) =>
            _records = records;

        public Task<ProcessTypeRecord?> GetByNameAsync(string typeName, CancellationToken ct = default) =>
            Task.FromResult<ProcessTypeRecord?>(_records.FirstOrDefault(r => r.TypeName == typeName));

        public Task<IReadOnlyList<ProcessTypeRecord>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult(_records);

        public Task SaveAsync(ProcessTypeRecord record, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SaveProcessConfigurationDataAsync(ProcessConfigurationData config, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<ProcessConfigurationData?> GetProcessConfigurationDataAsync(CancellationToken ct = default) =>
            Task.FromResult<ProcessConfigurationData?>(null);
    }

    private sealed class CountingProcessTypeStore : IProcessTypeStore
    {
        private readonly IReadOnlyList<ProcessTypeRecord> _records;
        public int GetAllCallCount { get; private set; }

        public CountingProcessTypeStore(params ProcessTypeRecord[] records) =>
            _records = records;

        public Task<ProcessTypeRecord?> GetByNameAsync(string typeName, CancellationToken ct = default) =>
            Task.FromResult<ProcessTypeRecord?>(null);

        public Task<IReadOnlyList<ProcessTypeRecord>> GetAllAsync(CancellationToken ct = default)
        {
            GetAllCallCount++;
            return Task.FromResult(_records);
        }

        public Task SaveAsync(ProcessTypeRecord record, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SaveProcessConfigurationDataAsync(ProcessConfigurationData config, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<ProcessConfigurationData?> GetProcessConfigurationDataAsync(CancellationToken ct = default) =>
            Task.FromResult<ProcessConfigurationData?>(null);
    }
}
