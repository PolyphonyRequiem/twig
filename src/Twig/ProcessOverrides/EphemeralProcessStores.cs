using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.ProcessOverrides;

/// <summary>
/// In-memory <see cref="IProcessTypeStore"/> holding one fetch's worth of process types.
/// </summary>
/// <remarks>
/// AB#216. <c>twig process</c> normally reads the SQLite cache that <c>twig sync</c> fills,
/// so under <c>--org</c>/<c>--project</c> — where there is no workspace and therefore no
/// cache — the types have to come from ADO directly and live only for the invocation.
/// <para/>
/// 🔴 <b>The write methods are deliberately no-ops rather than throwing.</b> Acceptance 2 of
/// AB#216 is "no cache/config/auth state is written", and a store that cannot persist is the
/// structural way to guarantee it: even if a future caller wires a sync service into this
/// path, it can only write into memory that is discarded when the process exits. Throwing
/// would make the guarantee depend on nobody calling, which is a convention rather than a
/// property.
/// </remarks>
internal sealed class EphemeralProcessTypeStore(IReadOnlyList<ProcessTypeRecord> records)
    : IProcessTypeStore
{
    private readonly IReadOnlyList<ProcessTypeRecord> _records = records;
    private ProcessConfigurationData? _configurationData;

    public Task<ProcessTypeRecord?> GetByNameAsync(string typeName, CancellationToken ct = default) =>
        Task.FromResult(_records.FirstOrDefault(
            r => string.Equals(r.TypeName, typeName, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<ProcessTypeRecord>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult(_records);

    public Task SaveAsync(ProcessTypeRecord record, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SaveProcessConfigurationDataAsync(ProcessConfigurationData config, CancellationToken ct = default)
    {
        _configurationData = config;
        return Task.CompletedTask;
    }

    public Task<ProcessConfigurationData?> GetProcessConfigurationDataAsync(CancellationToken ct = default) =>
        Task.FromResult(_configurationData);
}

/// <summary>
/// In-memory <see cref="IFieldDefinitionStore"/> holding one fetch's worth of field
/// definitions. See <see cref="EphemeralProcessTypeStore"/> for why writes are no-ops.
/// </summary>
internal sealed class EphemeralFieldDefinitionStore(IReadOnlyList<FieldDefinition> definitions)
    : IFieldDefinitionStore
{
    private readonly IReadOnlyList<FieldDefinition> _definitions = definitions;

    public Task<FieldDefinition?> GetByReferenceNameAsync(string referenceName, CancellationToken ct = default) =>
        Task.FromResult(_definitions.FirstOrDefault(
            d => string.Equals(d.ReferenceName, referenceName, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<FieldDefinition>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult(_definitions);

    public Task SaveBatchAsync(IReadOnlyList<FieldDefinition> definitions, CancellationToken ct = default) =>
        Task.CompletedTask;
}
