using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Infrastructure;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;

namespace Twig.Mcp.Services;

/// <summary>
/// Creates and caches <see cref="ConnectionScope"/> instances per <see cref="Connection"/>.
/// Extracted for testability of <see cref="ConnectionResolver"/>.
/// </summary>
public interface IConnectionScopeFactory
{
    /// <summary>Gets or creates a <see cref="ConnectionScope"/> for the given Connection.</summary>
    ConnectionScope GetOrCreate(Connection connection);
}

/// <summary>
/// Builds one <see cref="ServiceProvider"/> per <see cref="Connection"/> from
/// <c>AddConnectionServices</c> + <c>AddTwigNetworkServices</c> — the same registrations the CLI
/// and TUI use — and caches the resulting scopes for the process lifetime.
/// </summary>
/// <remarks>
/// These are <b>sibling</b> <see cref="ServiceCollection"/>s, never
/// <c>IServiceProvider.CreateScope</c>. <c>AddConnectionServices</c> captures
/// <c>twigDir</c>/<c>startDir</c> as closure parameters before any provider exists, and a child
/// scope inherits the parent's registrations — closures included — so every Connection after the
/// first would resolve the first Connection's paths (wayfinder 0016).
/// <para/>
/// Genuinely process-wide singletons (<see cref="HttpClient"/>,
/// <see cref="IAuthenticationProvider"/>, <see cref="AdoConcurrencyThrottle"/>) are injected as
/// pre-built instances so they are shared across Connections rather than duplicated per provider.
/// </remarks>
public sealed class ConnectionScopeFactory : IConnectionScopeFactory, IDisposable
{
    private readonly ConnectionRegistry _registry;
    private readonly HttpClient _httpClient;
    private readonly IAuthenticationProvider _authProvider;
    private readonly AdoConcurrencyThrottle _throttle = new();
    private readonly string _twigRoot;
    private readonly ConcurrentDictionary<Connection, Lazy<ConnectionScope>> _scopes = new();
    private bool _disposed;

    /// <param name="registry">Discovers available Connections.</param>
    /// <param name="httpClient">Shared HTTP client for all ADO calls.</param>
    /// <param name="authProvider">Shared auth provider for all ADO calls.</param>
    /// <param name="twigRoot">Path to the <c>.twig/</c> directory.</param>
    public ConnectionScopeFactory(
        ConnectionRegistry registry,
        HttpClient httpClient,
        IAuthenticationProvider authProvider,
        string twigRoot)
    {
        _registry = registry;
        _httpClient = httpClient;
        _authProvider = authProvider;
        _twigRoot = twigRoot;
    }

    /// <summary>
    /// Gets or creates the <see cref="ConnectionScope"/> for <paramref name="connection"/>.
    /// Created lazily on first access and cached for the process lifetime.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when <paramref name="connection"/> is not registered.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown when the factory has been disposed.</exception>
    public ConnectionScope GetOrCreate(Connection connection)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _scopes.GetOrAdd(
            connection,
            c => new Lazy<ConnectionScope>(() => CreateScope(c))).Value;
    }

    private ConnectionScope CreateScope(Connection connection)
    {
        var config = _registry.GetConfig(connection);

        var services = new ServiceCollection();

        // The same shared registrations the CLI and TUI compose. Nothing is duplicated here:
        // when a dependency is added to a service below, this provider picks it up for free.
        services.AddConnectionServices(config, _twigRoot);

        // Process-wide singletons, injected as pre-built instances so all Connections share them.
        services.AddSingleton(_httpClient);
        services.AddSingleton(_authProvider);
        services.AddSingleton(_throttle);

        services.AddTwigNetworkServices(
            config,
            resolvedGitProject: config.GetGitProject(),
            resolvedRepository: config.Git.Repository);

        // MCP's headless flusher — no IConsoleInput, no output formatting.
        services.AddSingleton(sp => new McpPendingChangeFlusher(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IPendingChangeStore>()));

        // BranchLinkService is only meaningful when git project + repository are configured.
        // Registered conditionally rather than as a nullable service so GetOptional<T>() returns
        // null exactly when git is unconfigured, matching IAdoGitService's own registration.
        if (!string.IsNullOrWhiteSpace(config.GetGitProject())
            && !string.IsNullOrWhiteSpace(config.Git.Repository))
        {
            services.AddSingleton(sp => new BranchLinkService(
                sp.GetRequiredService<IAdoGitService>(),
                sp.GetRequiredService<IAdoWorkItemService>()));
        }

        return new ConnectionScope(connection, services.BuildServiceProvider());
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var entry in _scopes.Values)
        {
            if (entry.IsValueCreated)
                entry.Value.Dispose();
        }

        _scopes.Clear();
    }
}
