using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig connection status</c>: asks
/// <see cref="IConnectionBindingService.ResolveAsync"/> what identity + binding the current
/// attached worktree resolves to and renders a safe principal/binding summary. Exits with
/// an actionable error (not a fallback) when there is no attachment or the binding is
/// ambiguous.
/// </summary>
internal sealed class ConnectionStatusCommand
{
    private readonly IConnectionBindingService _bindingService;
    private readonly TwigConfiguration _config;
    private readonly TwigPaths _paths;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly RendererFactory _rendererFactory;

    public ConnectionStatusCommand(
        IConnectionBindingService bindingService,
        TwigConfiguration config,
        TwigPaths paths,
        OutputFormatterFactory formatterFactory,
        RendererFactory? rendererFactory = null)
    {
        _bindingService = bindingService;
        _config = config;
        _paths = paths;
        _formatterFactory = formatterFactory;
        _rendererFactory = rendererFactory ?? new RendererFactory();
    }

    public async Task<int> ExecuteAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = _formatterFactory.GetFormatter(outputFormat);
        ResolvedConnectionBinding resolved;
        try
        {
            resolved = await _bindingService.ResolveAsync(_config, _paths, ct);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(fmt.FormatError($"No active connection binding: {ex.Message}"));
            Console.Error.WriteLine("Run 'twig auth login --identity <alias>' to register, then 'twig connection bind --org <org> --project <project> --identity <alias> --default'.");
            return 1;
        }

        RenderResolved(resolved, outputFormat);
        return 0;
    }

    private void RenderResolved(ResolvedConnectionBinding resolved, string outputFormat)
    {
        var identity = resolved.Identity;
        var binding = resolved.Binding;
        var message = $"Using identity '{identity.Name}' for {binding.ConnectionRef}";

        RenderNode node;
        if (ConnectionRenderHelpers.IsHumanFormat(outputFormat))
        {
            node = new RenderNode.Section(message, new RenderNode[]
            {
                new RenderNode.Text($"  identity:          {identity.Name}"),
                new RenderNode.Text($"  method:            {identity.Method}"),
                new RenderNode.Text($"  endpoint:          {resolved.Operation.Organization}/{resolved.Operation.Project}"),
                new RenderNode.Text($"  issuer:            {identity.Issuer}"),
                new RenderNode.Text($"  tenant:            {identity.TenantId}"),
                new RenderNode.Text($"  objectId:          {identity.ObjectId}"),
                new RenderNode.Text($"  authorityHost:     {identity.AuthorityHost}"),
                new RenderNode.Text($"  account:           {identity.AccountName ?? "(unknown)"}"),
                new RenderNode.Text($"  bindingId:         {binding.BindingId}"),
                new RenderNode.Text($"  revision:          {binding.Revision}"),
                new RenderNode.Text($"  worktreeRoot:      {resolved.WorktreeRoot}"),
                new RenderNode.Text($"  selectionSource:   {resolved.SelectionSource}"),
                new RenderNode.Text($"  selectionRevision: {resolved.SelectionRevision}"),
                new RenderNode.Text($"  attachmentRevision: {resolved.Operation.AttachmentRevision}"),
                new RenderNode.Text($"  endpoint/policy/defaults source: {resolved.Operation.PortableConfigurationSource}"),
                new RenderNode.Text($"  local preferences source: {resolved.Operation.UserPreferencesSource}"),
                new RenderNode.Text($"  display.icons:     {resolved.Operation.DisplayIcons} ({resolved.Operation.DisplayIconsSource})"),
            });
        }
        else if ((outputFormat ?? string.Empty).Equals("minimal", StringComparison.OrdinalIgnoreCase))
        {
            node = new RenderNode.Text(message);
        }
        else
        {
            var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["message"] = RenderCell.String(message),
                ["identity"] = RenderCell.String(identity.Name),
                ["method"] = RenderCell.String(identity.Method),
                ["organization"] = RenderCell.String(resolved.Operation.Organization),
                ["project"] = RenderCell.String(resolved.Operation.Project),
                ["team"] = RenderCell.String(resolved.Operation.Team),
                ["identityId"] = RenderCell.String(identity.IdentityId),
                ["tenant"] = RenderCell.String(identity.TenantId),
                ["objectId"] = RenderCell.String(identity.ObjectId),
                ["issuer"] = RenderCell.String(identity.Issuer),
                ["authorityHost"] = RenderCell.String(identity.AuthorityHost),
                ["account"] = RenderCell.String(identity.AccountName ?? string.Empty),
                ["bindingId"] = RenderCell.String(binding.BindingId),
                ["connection"] = RenderCell.String(binding.ConnectionRef),
                ["revision"] = RenderCell.Integer(binding.Revision),
                ["worktreeRoot"] = RenderCell.String(resolved.WorktreeRoot),
                ["selectionSource"] = RenderCell.String(resolved.SelectionSource),
                ["selectionRevision"] = RenderCell.Integer(resolved.SelectionRevision),
                ["attachmentRevision"] = RenderCell.Integer(resolved.Operation.AttachmentRevision),
                ["endpointSource"] = RenderCell.String(resolved.Operation.PortableConfigurationSource),
                ["policySource"] = RenderCell.String(resolved.Operation.PortableConfigurationSource),
                ["defaultsSource"] = RenderCell.String(resolved.Operation.PortableConfigurationSource),
                ["userPreferencesSource"] = RenderCell.String(resolved.Operation.UserPreferencesSource),
                ["displayIcons"] = RenderCell.String(resolved.Operation.DisplayIcons),
                ["displayIconsSource"] = RenderCell.String(resolved.Operation.DisplayIconsSource),
            };
            node = new RenderNode.Record("connectionStatus", fields);
        }

        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
    }
}
