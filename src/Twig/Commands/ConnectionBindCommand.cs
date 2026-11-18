using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Binds this user's credential selector to explicit provider dimensions, never to repository configuration.</summary>
internal sealed class ConnectionBindCommand(Func<IConnectionBindingService> bindings, RendererFactory renderers)
{
    internal async Task<int> ExecuteAsync(string? provider, string? organization, string? project,
        string? host, string? repository, string? database, string? identity, string? account, string? clientId,
        string outputFormat, CancellationToken ct)
    {
        try
        {
            var requirement = ProviderConnectionRequirement.Create(provider, organization, project, host, repository, database);
            var credential = requirement.Provider switch
            {
                "ado" when account is null && clientId is null && NativeSetupEnrollment.AliasProblem(identity) is null
                    => new ProviderConnectionCredential("ado", identity!.Trim()),
                "github" when account is not null && identity is null && clientId is null
                    => new ProviderConnectionCredential("gh", account),
                "github" when account is null && identity is not null && clientId is not null
                    => new ProviderConnectionCredential("github-app", identity, clientId),
                _ => throw new InvalidOperationException(
                    "Supply --identity <registered-alias> for ADO, --account <gh-user> for GitHub, or --identity and --client-id for a GitHub App. Native connections need no binding."),
            };
            var service = bindings();
            var registry = new ProviderConnectionRegistry(service.RegistryPath);
            // Validate registration before creating an authentication binding in the native registry.
            if (!(await registry.ListAsync(ct)).Any(value => value.Requirement == requirement))
                throw new InvalidOperationException($"Register the connection first: twig connection add {requirement.SetupArguments}");
            if (requirement.Provider == "ado")
                await service.CreateBindingAsync(organization!, project!, credential.Selector, makeDefault: false, ct);
            await registry.BindAsync(requirement, credential, ct);
            renderers.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(
                [new RenderNode.Record("connectionBound", ConnectionRenderHelpers.ConnectionFields(new(requirement, credential)))]));
            return 0;
        }
        catch (Exception ex) when (ConnectionCommandFailure.IsExpected(ex))
        {
            CommandError.Write(renderers, Console.Error, outputFormat, ex.Message);
            return 1;
        }
    }
}
