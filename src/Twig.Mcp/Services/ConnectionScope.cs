using Microsoft.Extensions.DependencyInjection;
using Twig.Infrastructure.Config;

namespace Twig.Mcp.Services;

/// <summary>
/// Owns one <see cref="IServiceProvider"/> per <see cref="Services.Connection"/> and resolves
/// per-Connection services from it.
/// </summary>
/// <remarks>
/// Replaces the former <c>WorkspaceContext</c> — a 33-member bundle whose 33 constructor
/// arguments hand-mirrored <c>TwigServiceRegistration</c> with no compiler forcing the two
/// copies to agree. That gap is the mechanism behind PolyphonyRequiem/twig#269 and #270
/// (wayfinder 0016). A provider has no argument list to drift, so the defect is unexpressible.
/// <para/>
/// <see cref="Get{T}"/> is a deliberate service-locator: MCP tool classes are process-wide
/// singletons, but the services they need vary per Connection, and the Connection is not known
/// until a tool call arrives. A value that varies per invocation cannot be constructor-injected
/// into a singleton, so the scope is passed to the call instead.
/// </remarks>
public sealed class ConnectionScope : IDisposable
{
    private readonly ServiceProvider _provider;

    internal ConnectionScope(Connection connection, ServiceProvider provider)
    {
        Connection = connection;
        _provider = provider;
    }

    /// <summary>The Azure DevOps organization/project this scope resolves services for.</summary>
    public Connection Connection { get; }

    /// <summary>Configuration for this Connection.</summary>
    public TwigConfiguration Config => _provider.GetRequiredService<TwigConfiguration>();

    /// <summary>Resolved paths for this Connection.</summary>
    public TwigPaths Paths => _provider.GetRequiredService<TwigPaths>();

    /// <summary>Resolves a required service from this Connection's provider.</summary>
    public T Get<T>() where T : notnull => _provider.GetRequiredService<T>();

    /// <summary>
    /// Resolves an optional service, returning <see langword="null"/> when it is not registered.
    /// Used for conditionally registered services such as <c>IAdoGitService</c>.
    /// </summary>
    public T? GetOptional<T>() => _provider.GetService<T>();

    /// <summary>
    /// Disposes the underlying provider, which disposes the singletons it created — including
    /// the Connection's <c>SqliteCacheStore</c>.
    /// </summary>
    public void Dispose() => _provider.Dispose();
}
