using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig connection bind</c>: creates an explicit association between an
/// endpoint (org/project) and a previously registered identity. <c>--default</c> requests
/// that this binding become the initial default for the endpoint; the service refuses to
/// switch an existing <i>different</i> default (that transition belongs to a later guarded
/// management command).
///
/// <para>
/// When <c>--org</c> and <c>--project</c> are omitted, the current workspace's checked-in
/// coordinates (<see cref="TwigConfiguration.Organization"/> /
/// <see cref="TwigConfiguration.Project"/>) are used. Either both or neither are inferred —
/// mixing one explicit flag with one implicit value is an error, because that pattern leads
/// to silent cross-project binds.
/// </para>
/// </summary>
internal sealed class ConnectionBindCommand
{
    private readonly IConnectionBindingService _bindingService;
    private readonly TwigConfiguration _config;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly RendererFactory _rendererFactory;

    public ConnectionBindCommand(
        IConnectionBindingService bindingService,
        TwigConfiguration config,
        OutputFormatterFactory formatterFactory,
        RendererFactory? rendererFactory = null)
    {
        _bindingService = bindingService;
        _config = config;
        _formatterFactory = formatterFactory;
        _rendererFactory = rendererFactory ?? new RendererFactory();
    }

    public async Task<int> ExecuteAsync(
        string? organization,
        string? project,
        string? identity,
        bool makeDefault,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = _formatterFactory.GetFormatter(outputFormat);

        if (string.IsNullOrWhiteSpace(identity))
        {
            Console.Error.WriteLine(fmt.FormatError("'--identity <alias>' is required."));
            Console.Error.WriteLine("Register one with 'twig auth login --identity <alias>' first; list them with 'twig auth identities'.");
            return 1;
        }

        var orgProvided = !string.IsNullOrWhiteSpace(organization);
        var projectProvided = !string.IsNullOrWhiteSpace(project);
        if (orgProvided ^ projectProvided)
        {
            Console.Error.WriteLine(fmt.FormatError("Pass both --org and --project together, or neither (to use the workspace defaults)."));
            return 1;
        }

        var resolvedOrg = orgProvided ? organization!.Trim() : _config.Organization;
        var resolvedProject = projectProvided ? project!.Trim() : _config.Project;

        if (string.IsNullOrWhiteSpace(resolvedOrg) || string.IsNullOrWhiteSpace(resolvedProject))
        {
            Console.Error.WriteLine(fmt.FormatError("No endpoint coordinates: pass --org and --project, or run inside a workspace with Organization/Project configured."));
            return 1;
        }

        IdentityBinding binding;
        try
        {
            binding = await _bindingService.CreateBindingAsync(resolvedOrg, resolvedProject, identity.Trim(), makeDefault, ct);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(fmt.FormatError($"Could not bind '{resolvedOrg}/{resolvedProject}' → '{identity}': {ex.Message}"));
            return 1;
        }

        RenderBound(binding, identity.Trim(), resolvedOrg, resolvedProject, makeDefault, outputFormat);
        return 0;
    }

    private void RenderBound(IdentityBinding binding, string identity, string org, string project, bool makeDefault, string outputFormat)
    {
        var defaultSuffix = makeDefault ? " (default)" : string.Empty;
        var message = $"Bound {org}/{project} → '{identity}'{defaultSuffix}";

        RenderNode node = ConnectionRenderHelpers.IsHumanFormat(outputFormat)
            ? new RenderNode.Section(message, new RenderNode[]
            {
                new RenderNode.Text($"  bindingId:  {binding.BindingId}"),
                new RenderNode.Text($"  connection: {binding.ConnectionRef}"),
                new RenderNode.Text($"  identityId: {binding.IdentityId}"),
                new RenderNode.Text($"  revision:   {binding.Revision}"),
                new RenderNode.Hint("Run 'twig connection status' to confirm the active binding."),
            })
            : (outputFormat ?? string.Empty).Equals("minimal", StringComparison.OrdinalIgnoreCase)
                ? (RenderNode)new RenderNode.Text(message)
                : ConnectionRenderHelpers.BindingRecord("connectionBound", binding, identityName: identity, message: message);
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
    }
}
