using Twig.Infrastructure.Auth;
using Twig.RenderTree;

namespace Twig.Commands;

/// <summary>
/// Shared projection helpers mapping the connection-binding records
/// (<see cref="AuthenticationIdentity"/>, <see cref="IdentityBinding"/>,
/// <see cref="ResolvedConnectionBinding"/>) into <see cref="RenderNode"/>s for every
/// surface. Keeps structured JSON field names in one place so the auth and
/// connection commands cannot drift apart.
///
/// <para>
/// Secrets are never rendered. <see cref="AuthenticationIdentity.CredentialRef"/> is
/// an opaque pointer into the central credential store and is deliberately omitted
/// from every projection — the service owns credential materialization; callers only
/// ever see principal metadata.
/// </para>
/// </summary>
internal static class ConnectionRenderHelpers
{
    internal static RenderNode IdentityRecord(string kind, AuthenticationIdentity identity, string? message = null)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["identityId"] = RenderCell.String(identity.IdentityId),
            ["name"] = RenderCell.String(identity.Name),
            ["tenant"] = RenderCell.String(identity.TenantId),
            ["objectId"] = RenderCell.String(identity.ObjectId),
            ["issuer"] = RenderCell.String(identity.Issuer),
            ["authorityHost"] = RenderCell.String(identity.AuthorityHost),
            ["account"] = RenderCell.String(identity.AccountName ?? string.Empty),
        };
        if (!string.IsNullOrEmpty(message))
            fields["message"] = RenderCell.String(message);
        return new RenderNode.Record(kind, fields);
    }

    internal static RenderNode BindingRecord(string kind, IdentityBinding binding, string? identityName = null, string? message = null)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["bindingId"] = RenderCell.String(binding.BindingId),
            ["connection"] = RenderCell.String(binding.ConnectionRef),
            ["identityId"] = RenderCell.String(binding.IdentityId),
            ["revision"] = RenderCell.Integer(binding.Revision),
        };
        if (!string.IsNullOrEmpty(identityName))
            fields["identity"] = RenderCell.String(identityName);
        if (!string.IsNullOrEmpty(message))
            fields["message"] = RenderCell.String(message);
        return new RenderNode.Record(kind, fields);
    }

    internal static IReadOnlyList<RenderColumn> IdentityColumns { get; } =
    [
        new RenderColumn("name", "name"),
        new RenderColumn("tenant", "tenant"),
        new RenderColumn("objectId", "objectId"),
        new RenderColumn("authorityHost", "authorityHost"),
        new RenderColumn("account", "account"),
    ];

    internal static RenderRow IdentityRow(AuthenticationIdentity identity)
        => new("identity", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["name"] = RenderCell.String(identity.Name),
            ["tenant"] = RenderCell.String(identity.TenantId),
            ["objectId"] = RenderCell.String(identity.ObjectId),
            ["authorityHost"] = RenderCell.String(identity.AuthorityHost),
            ["account"] = RenderCell.String(identity.AccountName ?? string.Empty),
        });

    internal static IReadOnlyList<RenderColumn> BindingColumns { get; } =
    [
        new RenderColumn("connection", "connection"),
        new RenderColumn("identityId", "identity"),
        new RenderColumn("revision", "revision"),
    ];

    internal static RenderRow BindingRow(IdentityBinding binding)
        => new("binding", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["connection"] = RenderCell.String(binding.ConnectionRef),
            ["identityId"] = RenderCell.String(binding.IdentityId),
            ["revision"] = RenderCell.Integer(binding.Revision),
        });

    internal static bool IsHumanFormat(string? outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        return lower is not ("json" or "json-full" or "json-compact" or "minimal" or "ids");
    }
}
