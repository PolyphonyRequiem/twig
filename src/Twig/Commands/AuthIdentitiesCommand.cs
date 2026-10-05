using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig auth identities</c>: lists every AAD identity registered through
/// <see cref="IConnectionBindingService.RegisterAadIdentityAsync"/> with its safe principal
/// metadata (alias, tenant, object id, authority host, account name). Credential pointers
/// (<c>CredentialRef</c>) and refresh tokens are never surfaced; the service owns credential
/// materialization.
/// </summary>
internal sealed class AuthIdentitiesCommand
{
    private readonly IConnectionBindingService _bindingService;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly RendererFactory _rendererFactory;

    public AuthIdentitiesCommand(
        IConnectionBindingService bindingService,
        OutputFormatterFactory formatterFactory,
        RendererFactory? rendererFactory = null)
    {
        _bindingService = bindingService;
        _formatterFactory = formatterFactory;
        _rendererFactory = rendererFactory ?? new RendererFactory();
    }

    public async Task<int> ExecuteAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = _formatterFactory.GetFormatter(outputFormat);
        IReadOnlyList<AuthenticationIdentity> identities;
        try
        {
            identities = await _bindingService.ListIdentitiesAsync(ct);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(fmt.FormatError($"Could not list identities: {ex.Message}"));
            return 1;
        }

        var human = ConnectionRenderHelpers.IsHumanFormat(outputFormat);
        if (identities.Count == 0)
        {
            var empty = human
                ? (RenderNode)new RenderNode.Section("No identities registered.", new RenderNode[]
                {
                    new RenderNode.Hint("Run 'twig auth login --identity <alias>' (AAD) or 'twig auth pat --identity <alias> --org <org>' (PAT) to register one."),
                })
                : new RenderNode.Record("identityList", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["count"] = RenderCell.Integer(0),
                });
            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { empty }));
            return 0;
        }

        var rows = identities
            .Select(ConnectionRenderHelpers.IdentityRow)
            .ToList();
        var table = new RenderNode.Table("Identities", ConnectionRenderHelpers.IdentityColumns, rows);
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { (RenderNode)table }));
        return 0;
    }
}
