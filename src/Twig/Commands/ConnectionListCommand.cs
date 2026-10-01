using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig connection list</c>: lists every binding for the given endpoint
/// (org/project). When neither <c>--org</c> nor <c>--project</c> is supplied, the current
/// workspace's checked-in coordinates are used.
/// </summary>
internal sealed class ConnectionListCommand
{
    private readonly IConnectionBindingService _bindingService;
    private readonly TwigConfiguration _config;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly RendererFactory _rendererFactory;

    public ConnectionListCommand(
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
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = _formatterFactory.GetFormatter(outputFormat);

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

        IReadOnlyList<IdentityBinding> bindings;
        try
        {
            bindings = await _bindingService.ListBindingsAsync(resolvedOrg, resolvedProject, ct);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(fmt.FormatError($"Could not list bindings for '{resolvedOrg}/{resolvedProject}': {ex.Message}"));
            return 1;
        }

        var human = ConnectionRenderHelpers.IsHumanFormat(outputFormat);
        if (bindings.Count == 0)
        {
            var empty = human
                ? (RenderNode)new RenderNode.Section($"No bindings for {resolvedOrg}/{resolvedProject}.", new RenderNode[]
                {
                    new RenderNode.Hint($"Create one with 'twig connection bind --org {resolvedOrg} --project {resolvedProject} --identity <alias> --default'."),
                })
                : new RenderNode.Record("bindingList", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["organization"] = RenderCell.String(resolvedOrg),
                    ["project"] = RenderCell.String(resolvedProject),
                    ["count"] = RenderCell.Integer(0),
                });
            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { empty }));
            return 0;
        }

        var rows = bindings
            .Select(ConnectionRenderHelpers.BindingRow)
            .ToList();
        var table = new RenderNode.Table($"{resolvedOrg}/{resolvedProject}", ConnectionRenderHelpers.BindingColumns, rows);
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { (RenderNode)table }));
        return 0;
    }
}
