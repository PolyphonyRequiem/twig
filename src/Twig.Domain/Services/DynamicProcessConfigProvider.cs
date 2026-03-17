using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;

namespace Twig.Domain.Services;

/// <summary>
/// Implements <see cref="IProcessConfigurationProvider"/> by building configuration
/// from dynamic type data read from <see cref="IProcessTypeStore"/>.
/// Results are cached after the first call since the CLI is short-lived (one command per process).
/// </summary>
/// <remarks>
/// <b>Thread safety</b>: <see cref="_cachedConfig"/> is updated non-atomically.
/// This is intentional — the Twig CLI is single-threaded. Do NOT use this class
/// in concurrent scenarios (e.g., ASP.NET request handlers) without adding synchronization.
/// </remarks>
public sealed class DynamicProcessConfigProvider : IProcessConfigurationProvider
{
    private readonly IProcessTypeStore _processTypeStore;
    private ProcessConfiguration? _cachedConfig;

    public DynamicProcessConfigProvider(IProcessTypeStore processTypeStore)
    {
        _processTypeStore = processTypeStore;
    }

    public ProcessConfiguration GetConfiguration()
    {
        if (_cachedConfig is not null)
            return _cachedConfig;

        // WARNING (CLI-only pattern): .GetAwaiter().GetResult() is safe here because the Twig CLI
        // runs in a console application with no custom SynchronizationContext, eliminating the
        // deadlock risk present in hosted environments (ASP.NET, WinForms, etc.).
        // Do NOT replicate this sync-over-async pattern outside of console/CLI applications.
        var records = _processTypeStore.GetAllAsync().GetAwaiter().GetResult();
        if (records.Count == 0)
            throw new InvalidOperationException(
                "Process configuration not available. Run 'twig init' to initialize.");

        _cachedConfig = ProcessConfiguration.FromRecords(records);
        return _cachedConfig;
    }
}
