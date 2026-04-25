using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Abstraction for reading and writing process type metadata.
/// Implemented by <c>SqliteProcessTypeStore</c> in the Infrastructure layer.
/// </summary>
public interface IProcessTypeStore
{
    /// <summary>
    /// Gets the stored process type metadata for the given type name.
    /// Returns <c>null</c> if the type is not in the store.
    /// </summary>
    Task<ProcessTypeRecord?> GetByNameAsync(string typeName, CancellationToken ct = default);

    /// <summary>
    /// Gets all stored process type records.
    /// </summary>
    Task<IReadOnlyList<ProcessTypeRecord>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves or updates a process type record.
    /// </summary>
    Task SaveAsync(ProcessTypeRecord record, CancellationToken ct = default);

    /// <summary>
    /// Persists the full <see cref="ProcessConfigurationData"/> as serialized JSON
    /// in the metadata table for offline access.
    /// </summary>
    Task SaveProcessConfigurationDataAsync(ProcessConfigurationData config, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the cached <see cref="ProcessConfigurationData"/> from the metadata table.
    /// Returns <c>null</c> if no data is stored or the stored data is corrupt.
    /// </summary>
    Task<ProcessConfigurationData?> GetProcessConfigurationDataAsync(CancellationToken ct = default);
}
