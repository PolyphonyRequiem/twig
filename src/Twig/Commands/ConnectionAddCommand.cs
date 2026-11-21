using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

internal sealed class ConnectionAddCommand(Func<IConnectionBindingService> bindings, RendererFactory renderers)
{
    internal async Task<int> ExecuteAsync(string? provider, string? organization, string? project,
        string? host, string? repository, string? database, string outputFormat, CancellationToken ct)
    {
        try
        {
            var requirement = ProviderConnectionRequirement.Create(provider, organization, project, host, repository, database);
            var registered = await new DeviceConnectionCatalog(bindings().RegistryPath).AddAsync(requirement, ct);
            renderers.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(
                [new RenderNode.Record("connection", ConnectionRenderHelpers.ConnectionFields(registered))]));
            return 0;
        }
        catch (Exception ex) when (ConnectionCommandFailure.IsExpected(ex))
        {
            CommandError.Write(renderers, Console.Error, outputFormat, ex.Message);
            return 1;
        }
    }
}

internal static class ConnectionCommandFailure
{
    internal static bool IsExpected(Exception ex)
        => ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException
            or Microsoft.Data.Sqlite.SqliteException;
}
