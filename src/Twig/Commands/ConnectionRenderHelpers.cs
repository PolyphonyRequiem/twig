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
///
/// <para>
/// PAT identities render <c>method</c> / <c>adoPrincipalId</c> / <c>adoAuthority</c>
/// and OMIT the AAD-only claim fields (<c>tenant</c>, <c>objectId</c>, <c>issuer</c>,
/// <c>authorityHost</c>) so JSON consumers never read made-up tenant or oid values.
/// AAD identities keep their existing fields with the new <c>method</c> added.
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
            ["method"] = RenderCell.String(identity.Method),
            ["account"] = RenderCell.String(identity.AccountName ?? string.Empty),
        };
        if (string.Equals(identity.Method, "pat", StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(identity.AdoPrincipalId))
                fields["adoPrincipalId"] = RenderCell.String(identity.AdoPrincipalId);
            if (!string.IsNullOrEmpty(identity.AdoAuthority))
                fields["adoAuthority"] = RenderCell.String(identity.AdoAuthority);
        }
        else
        {
            fields["tenant"] = RenderCell.String(identity.TenantId);
            fields["objectId"] = RenderCell.String(identity.ObjectId);
            fields["issuer"] = RenderCell.String(identity.Issuer);
            fields["authorityHost"] = RenderCell.String(identity.AuthorityHost);
        }
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

    /// <summary>Method-specific columns; empty human cells do not invent JWT claims for PATs.</summary>
    internal static IReadOnlyList<RenderColumn> IdentityColumns { get; } =
    [
        new RenderColumn("name", "name"),
        new RenderColumn("method", "method"),
        new RenderColumn("tenant", "tenant"),
        new RenderColumn("objectId", "objectId"),
        new RenderColumn("authorityHost", "authorityHost"),
        new RenderColumn("adoPrincipalId", "adoPrincipalId"),
        new RenderColumn("adoAuthority", "adoAuthority"),
        new RenderColumn("account", "account"),
    ];

    internal static RenderRow IdentityRow(AuthenticationIdentity identity)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["name"] = RenderCell.String(identity.Name),
            ["method"] = RenderCell.String(identity.Method),
            ["account"] = RenderCell.String(identity.AccountName ?? string.Empty),
        };
        if (identity.Method == "pat")
        {
            fields["adoPrincipalId"] = RenderCell.String(identity.AdoPrincipalId ?? string.Empty);
            fields["adoAuthority"] = RenderCell.String(identity.AdoAuthority ?? string.Empty);
        }
        else
        {
            fields["tenant"] = RenderCell.String(identity.TenantId);
            fields["objectId"] = RenderCell.String(identity.ObjectId);
            fields["authorityHost"] = RenderCell.String(identity.AuthorityHost);
        }
        return new RenderRow("identity", fields);
    }

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
