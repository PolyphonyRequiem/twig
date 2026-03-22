using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;

namespace Twig.TestKit;

/// <summary>
/// Fluent builder for <see cref="ProcessConfiguration"/> instances in tests.
/// Provides factory methods for standard ADO process templates (Agile, Scrum, Basic, CMMI)
/// and a custom builder for ad-hoc configurations.
/// </summary>
public sealed class ProcessConfigBuilder
{
    private readonly List<ProcessTypeRecord> _records = new();

    /// <summary>Adds a type record with full <see cref="StateEntry"/> metadata.</summary>
    public ProcessConfigBuilder AddType(string typeName, StateEntry[] states, params string[] childTypes)
    {
        _records.Add(new ProcessTypeRecord
        {
            TypeName = typeName,
            States = states,
            ValidChildTypes = childTypes,
        });
        return this;
    }

    /// <summary>Adds a type record using simple state names (category defaults to <see cref="StateCategory.Unknown"/>).</summary>
    public ProcessConfigBuilder AddType(string typeName, string[] states, params string[] childTypes)
    {
        _records.Add(new ProcessTypeRecord
        {
            TypeName = typeName,
            States = states.Select(n => new StateEntry(n, StateCategory.Unknown, null)).ToArray(),
            ValidChildTypes = childTypes,
        });
        return this;
    }

    public ProcessConfiguration Build() => ProcessConfiguration.FromRecords(_records);

    // ── Standard process templates ──────────────────────────────────

    /// <summary>
    /// Full Agile process template with proper <see cref="StateCategory"/> metadata.
    /// Types: Epic → Feature → User Story / Bug → Task.
    /// </summary>
    public static ProcessConfiguration Agile() => new ProcessConfigBuilder()
        .AddType("Epic", S(("New", StateCategory.Proposed), ("Active", StateCategory.InProgress), ("Closed", StateCategory.Completed), ("Removed", StateCategory.Removed)), "Feature")
        .AddType("Feature", S(("New", StateCategory.Proposed), ("Active", StateCategory.InProgress), ("Closed", StateCategory.Completed), ("Removed", StateCategory.Removed)), "User Story", "Bug")
        .AddType("User Story", S(("New", StateCategory.Proposed), ("Active", StateCategory.InProgress), ("Resolved", StateCategory.Resolved), ("Closed", StateCategory.Completed), ("Removed", StateCategory.Removed)), "Task")
        .AddType("Bug", S(("New", StateCategory.Proposed), ("Active", StateCategory.InProgress), ("Resolved", StateCategory.Resolved), ("Closed", StateCategory.Completed)), "Task")
        .AddType("Task", S(("New", StateCategory.Proposed), ("Active", StateCategory.InProgress), ("Closed", StateCategory.Completed), ("Removed", StateCategory.Removed)), Array.Empty<string>())
        .Build();

    /// <summary>
    /// Minimal Agile config with only User Story → Task hierarchy.
    /// Useful for tests that only need one type chain.
    /// </summary>
    public static ProcessConfiguration AgileUserStoryOnly() => new ProcessConfigBuilder()
        .AddType("User Story", S(("New", StateCategory.Proposed), ("Active", StateCategory.InProgress), ("Resolved", StateCategory.Resolved), ("Closed", StateCategory.Completed), ("Removed", StateCategory.Removed)), "Task")
        .Build();

    /// <summary>
    /// Full Scrum process template.
    /// Types: Epic → Feature → Product Backlog Item / Bug → Task.
    /// </summary>
    public static ProcessConfiguration Scrum() => new ProcessConfigBuilder()
        .AddType("Epic", S(("New", StateCategory.Proposed), ("In Progress", StateCategory.InProgress), ("Done", StateCategory.Completed), ("Removed", StateCategory.Removed)), "Feature")
        .AddType("Feature", S(("New", StateCategory.Proposed), ("In Progress", StateCategory.InProgress), ("Done", StateCategory.Completed), ("Removed", StateCategory.Removed)), "Product Backlog Item", "Bug")
        .AddType("Product Backlog Item", S(("New", StateCategory.Proposed), ("Approved", StateCategory.Proposed), ("Committed", StateCategory.InProgress), ("Done", StateCategory.Completed), ("Removed", StateCategory.Removed)), "Task")
        .AddType("Bug", S(("New", StateCategory.Proposed), ("Approved", StateCategory.Proposed), ("Committed", StateCategory.InProgress), ("Done", StateCategory.Completed), ("Removed", StateCategory.Removed)), "Task")
        .AddType("Task", S(("To Do", StateCategory.Proposed), ("In Progress", StateCategory.InProgress), ("Done", StateCategory.Completed), ("Removed", StateCategory.Removed)), Array.Empty<string>())
        .Build();

    /// <summary>
    /// Basic process template.
    /// Types: Epic → Issue → Task.
    /// </summary>
    public static ProcessConfiguration Basic() => new ProcessConfigBuilder()
        .AddType("Epic", S(("To Do", StateCategory.Proposed), ("Doing", StateCategory.InProgress), ("Done", StateCategory.Completed)), "Issue")
        .AddType("Issue", S(("To Do", StateCategory.Proposed), ("Doing", StateCategory.InProgress), ("Done", StateCategory.Completed)), "Task")
        .AddType("Task", S(("To Do", StateCategory.Proposed), ("Doing", StateCategory.InProgress), ("Done", StateCategory.Completed)), Array.Empty<string>())
        .Build();

    /// <summary>
    /// CMMI process template.
    /// Types: Epic → Feature → Requirement → Task, Bug → Task.
    /// </summary>
    public static ProcessConfiguration Cmmi() => new ProcessConfigBuilder()
        .AddType("Epic", S(("Proposed", StateCategory.Proposed), ("Active", StateCategory.InProgress), ("Resolved", StateCategory.Resolved), ("Closed", StateCategory.Completed), ("Removed", StateCategory.Removed)), "Feature")
        .AddType("Feature", S(("Proposed", StateCategory.Proposed), ("Active", StateCategory.InProgress), ("Resolved", StateCategory.Resolved), ("Closed", StateCategory.Completed), ("Removed", StateCategory.Removed)), "Requirement")
        .AddType("Requirement", S(("Proposed", StateCategory.Proposed), ("Active", StateCategory.InProgress), ("Resolved", StateCategory.Resolved), ("Closed", StateCategory.Completed), ("Removed", StateCategory.Removed)), "Task")
        .AddType("Bug", S(("Proposed", StateCategory.Proposed), ("Active", StateCategory.InProgress), ("Resolved", StateCategory.Resolved), ("Closed", StateCategory.Completed), ("Removed", StateCategory.Removed)), "Task")
        .AddType("Task", S(("Proposed", StateCategory.Proposed), ("Active", StateCategory.InProgress), ("Resolved", StateCategory.Resolved), ("Closed", StateCategory.Completed), ("Removed", StateCategory.Removed)), Array.Empty<string>())
        .Build();

    /// <summary>Helper to create <see cref="StateEntry"/> arrays with category metadata.</summary>
    public static StateEntry[] S(params (string Name, StateCategory Category)[] entries) =>
        entries.Select(e => new StateEntry(e.Name, e.Category, null)).ToArray();
}
