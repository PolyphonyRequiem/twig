using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Lists daemon-owned registrations across all workspaces, without inferring a workspace filter.</summary>
internal sealed class ConnectionListCommand(IConnectionBindingService bindings, RendererFactory renderers)
{
    internal async Task<int> ExecuteAsync(string? provider, string? organization, string? project,
        string? host, string? repository, string? database, string outputFormat, CancellationToken ct)
    {
        try
        {
            if (provider is not (null or "ado" or "github" or "twig"))
                throw new InvalidOperationException("Choose --provider ado, github or twig.");
            var filter = organization is null && project is null && host is null && repository is null && database is null ? null
                : ProviderConnectionRequirement.Create(provider, organization, project, host, repository, database);
            var connections = await new DeviceConnectionCatalog(bindings.RegistryPath).ListAsync(ct);
            var rows = connections.Where(value => (provider is null || value.Requirement.Provider == provider)
                    && (filter is null || value.Requirement == filter))
                .Select(value => new RenderRow("connection", ConnectionRenderHelpers.ConnectionFields(value))).ToList();
            renderers.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(
                [new RenderNode.Table("Registered provider connections", ConnectionRenderHelpers.ConnectionColumns, rows)]));
            return 0;
        }
        catch (Exception ex) when (ConnectionCommandFailure.IsExpected(ex))
        {
            CommandError.Write(renderers, Console.Error, outputFormat, ex.Message);
            return 1;
        }
    }
}
