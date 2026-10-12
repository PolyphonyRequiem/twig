using Microsoft.Extensions.DependencyInjection;
using Twig.DependencyInjection;

namespace Twig.Browser;

/// <summary>Reopens the same native composition only on startup or explicit reconnect.</summary>
internal sealed class BrowserRuntimeFactory
{
    internal BrowserRuntime Open(string cwd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        var root = Path.GetFullPath(cwd);
        if (!Directory.Exists(root))
            throw new ArgumentException("The captured browser working directory does not exist.", nameof(cwd));

        var services = new ServiceCollection();
        services.AddTwigWorkspaceServices(root, []);
        return new BrowserRuntime(services.BuildServiceProvider());
    }
}

/// <summary>Owns a reopenable native provider; retained observations own no provider or store.</summary>
internal sealed class BrowserRuntime(ServiceProvider services) : IDisposable
{
    internal IServiceProvider Services => services;

    public void Dispose() => services.Dispose();
}
