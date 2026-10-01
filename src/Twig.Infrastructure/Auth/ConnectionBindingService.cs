using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// Task #1104 — the only entry point that mints <see cref="IAuthenticationProvider"/>
/// instances for the normal attached-worktree route and the explicit init
/// bootstrap route. Owns the central <c>system.db</c> identity/binding/default
/// tables and the sibling per-credential files; refuses to answer when the
/// configured endpoint, the registered binding, and the refreshed principal
/// do not line up byte-for-byte before any work HTTP leaves the process.
/// <para>
/// Credential material lives in private per-credential JSON files under
/// <c>{userHome}/credentials/</c>, not in SQLite. The registry only carries
/// opaque <c>credential_ref</c> handles plus the stamped principal
/// (tenant / object / issuer / authority host / optional account name) so
/// row dumps and diagnostics never leak a refresh token.
/// </para>
/// </summary>
internal sealed class ConnectionBindingService : IConnectionBindingService, IDisposable
{
    /// <summary>Explicit absolute metadata home. Invalid values fail closed.</summary>
    public const string UserHomeEnvVar = "TWIG_USER_HOME";

    private const string CredentialsDirName = "credentials";
    private const string SystemDbFileName = "system.db";

    private readonly string _userHome;
    private readonly string _credentialsDir;
    private readonly SqliteSystemWorktreeRegistry _registry;
    private readonly ITokenRefresher _refresher;
    private readonly TimeProvider _clock;
    private bool _disposed;

    public ConnectionBindingService(string userHome, ITokenRefresher? refresher = null, TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(userHome) || !Path.IsPathFullyQualified(userHome))
            throw new ArgumentException("userHome must be a non-empty absolute path.", nameof(userHome));

        _userHome = Path.GetFullPath(userHome);
        _credentialsDir = Path.Combine(_userHome, CredentialsDirName);
        _clock = clock ?? TimeProvider.System;
        _refresher = refresher ?? new MsalTokenRefresher();
        Directory.CreateDirectory(_userHome);
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(_credentialsDir);
        else
        {
            const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(_credentialsDir, privateMode);
            File.SetUnixFileMode(_credentialsDir, privateMode);
        }
        _registry = new SqliteSystemWorktreeRegistry(Path.Combine(_userHome, SystemDbFileName), _clock);
    }

    /// <summary>Resolve the metadata home used when the DI composition root
    /// does not inject one explicitly. Honors <c>TWIG_USER_HOME</c> when it is
    /// a nonblank absolute path; otherwise uses the existing system-state root. This is not
    /// an identity selector — it only names where metadata and credential
    /// blobs live. The service refuses credential writes outside this root.</summary>
    public static string ResolveUserHome()
    {
        var env = Environment.GetEnvironmentVariable(UserHomeEnvVar);
        if (env is not null)
        {
            if (string.IsNullOrWhiteSpace(env) || !Path.IsPathFullyQualified(env.Trim()))
                throw new InvalidOperationException("TWIG_USER_HOME must name a non-empty absolute metadata home; refusing an ambient-home fallback.");
            return Path.GetFullPath(env.Trim());
        }
        return WorkspaceDiscovery.GlobalHomePath;
    }

    public async Task<AuthenticationIdentity> RegisterAadIdentityAsync(
        string name,
        TwigRefreshTokenStoreEntry credential,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Identity name must be non-empty.", nameof(name));
        if (name.Length > 64)
            throw new ArgumentException("Identity name must be 64 characters or fewer.", nameof(name));
        ArgumentNullException.ThrowIfNull(credential);

        var normalizedName = name.Trim();
        var rt = Require(credential.RefreshToken, "refresh_token");
        var clientId = Require(credential.ClientId, "client_id");
        var tenantId = Require(credential.TenantId, "tenant_id");
        var authorityHost = Require(credential.AuthorityHost, "authority_host");

        // 1. Mint a token through the injected refresher. Service never trusts
        // an un-exchanged credential — a refresh token that cannot mint a
        // valid ADO access token is not a usable identity.
        var (accessToken, rotated, invalidGrant) = await _refresher.TryRefreshAsync(
            rt, clientId, tenantId, authorityHost, ct).ConfigureAwait(false);
        if (accessToken is null)
        {
            throw new InvalidOperationException(invalidGrant
                ? $"Refresh token for identity '{normalizedName}' was rejected by AAD (invalid_grant). Run the login flow again to collect a fresh refresh token."
                : $"Could not exchange the supplied refresh token for an ADO access token (identity '{normalizedName}'). Check network connectivity and tenant/authority configuration.");
        }

        // Check principal claims on the token returned by authenticated issuance.
        // Decoding is consistency checking, not local signature verification.
        var info = JwtAccessTokenInspector.TryDecode(accessToken);
        if (info is null)
            throw new InvalidOperationException(
                $"Minted access token for '{normalizedName}' is not a decodable JWT; refusing to register an unverifiable principal.");
        if (!info.IsValidAdoAudience)
            throw new InvalidOperationException(
                $"Minted access token for '{normalizedName}' does not carry the Azure DevOps audience. Observed audience: {info.Audience ?? "(none)"}.");
        if (string.IsNullOrWhiteSpace(info.TenantId))
            throw new InvalidOperationException(
                $"Minted access token for '{normalizedName}' is missing a tenant claim; refusing to register an unverifiable principal.");
        if (string.IsNullOrWhiteSpace(info.ObjectId))
            throw new InvalidOperationException(
                $"Minted access token for '{normalizedName}' is missing an oid claim; refusing to register an unverifiable principal.");
        if (!string.Equals(info.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refresh entry tenant_id ({tenantId}) disagrees with the minted token tenant ({info.TenantId}); refusing to register a mismatched principal.");
        if (!string.IsNullOrWhiteSpace(credential.ObjectId)
            && !string.Equals(credential.ObjectId, info.ObjectId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refresh entry oid ({credential.ObjectId}) disagrees with the minted token oid ({info.ObjectId}); refusing to register a mismatched principal.");

        var issuer = Require(info.Issuer, "iss (minted token issuer)");
        var objectId = info.ObjectId!;
        var accountName = !string.IsNullOrWhiteSpace(info.UserPrincipalName) ? info.UserPrincipalName
            : (!string.IsNullOrWhiteSpace(credential.UserPrincipalName) ? credential.UserPrincipalName : null);

        var credentialRef = "cred-" + Guid.NewGuid().ToString("N");
        var identityId = ComputeIdentityId(normalizedName);

        // 3. Refuse an alias collision whose principal differs from the
        //    newly-verified one — that is the "login does not switch
        //    identities" guarantee. Same alias + same principal is a benign
        //    credential refresh that updates the stored RT in place.
        var existingRes = await _registry.FindIdentityByNameAsync(normalizedName, ct).ConfigureAwait(false);
        if (!existingRes.IsSuccess) throw new InvalidOperationException($"Registry refused identity lookup: {existingRes.Error}");
        var existing = existingRes.Value;
        var now = _clock.GetUtcNow();

        // Freshly materialized blob so the caller's instance is never mutated
        // and never holds the AAD-stamped principal they did not know about.
        var persisted = new TwigRefreshTokenStoreEntry
        {
            RefreshToken = rotated ?? rt,
            ClientId = clientId,
            TenantId = tenantId,
            AuthorityHost = authorityHost,
            UserPrincipalName = accountName,
            ObjectId = objectId,
            BootstrappedAt = now.ToString("u", CultureInfo.InvariantCulture),
            Source = string.IsNullOrWhiteSpace(credential.Source) ? "aad" : credential.Source,
        };

        if (existing is not null)
        {
            if (!string.Equals(existing.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.ObjectId, objectId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.Issuer, issuer, StringComparison.Ordinal)
                || !string.Equals(existing.AuthorityHost, authorityHost, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Identity '{normalizedName}' is already registered to a different principal (tenant={existing.TenantId}, oid={existing.ObjectId}). Register the new principal under a different alias; identity transitions are deliberate guarded management operations and must not silently overwrite stored credentials.");
            }

            accountName ??= existing.AccountName;
            persisted.UserPrincipalName = accountName;
            // Benign refresh of same principal: rewrite the credential blob
            // under the existing credential_ref so cached token-cache sidecars
            // stay valid, and bump the identity row's refresh stamp.
            WriteCredentialAtomic(existing.CredentialRef, persisted);
            var stamp = await _registry.UpdateIdentityRefreshStampAsync(existing.IdentityId, accountName, now, ct).ConfigureAwait(false);
            if (!stamp.IsSuccess) throw new InvalidOperationException($"Registry refused identity refresh stamp: {stamp.Error}");
            return ToIdentity(existing with { AccountName = accountName, UpdatedAt = now });
        }

        // 4. Fresh identity: write the credential blob first (so an insert
        //    collision cannot leave an orphan row referencing a missing file).
        WriteCredentialAtomic(credentialRef, persisted);
        var row = new IdentityRow(
            IdentityId: identityId,
            Name: normalizedName,
            TenantId: tenantId,
            ObjectId: objectId,
            Issuer: issuer,
            AuthorityHost: authorityHost,
            CredentialRef: credentialRef,
            AccountName: accountName,
            CreatedAt: now,
            UpdatedAt: now);
        var insert = await _registry.InsertIdentityAsync(row, ct).ConfigureAwait(false);
        if (!insert.IsSuccess)
        {
            // Credential blob was written but the row failed — clean up so the
            // credentials directory does not accumulate orphan files.
            TryDeleteCredential(credentialRef);
            throw new InvalidOperationException($"Registry refused new identity: {insert.Error}");
        }
        return ToIdentity(row);
    }

    public async Task<IReadOnlyList<AuthenticationIdentity>> ListIdentitiesAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var rows = await _registry.ListIdentitiesAsync(ct).ConfigureAwait(false);
        if (!rows.IsSuccess) throw new InvalidOperationException($"Registry refused identity list: {rows.Error}");
        var result = new List<AuthenticationIdentity>(rows.Value.Count);
        foreach (var row in rows.Value) result.Add(ToIdentity(row));
        return result;
    }

    public async Task<IdentityBinding> CreateBindingAsync(
        string organization,
        string project,
        string identityName,
        bool makeDefault,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(organization))
            throw new ArgumentException("organization must be non-empty.", nameof(organization));
        if (string.IsNullOrWhiteSpace(project))
            throw new ArgumentException("project must be non-empty.", nameof(project));
        if (string.IsNullOrWhiteSpace(identityName))
            throw new ArgumentException("identityName must be non-empty.", nameof(identityName));

        var identityRes = await _registry.FindIdentityByNameAsync(identityName.Trim(), ct).ConfigureAwait(false);
        if (!identityRes.IsSuccess) throw new InvalidOperationException($"Registry refused identity lookup: {identityRes.Error}");
        var identity = identityRes.Value
            ?? throw new InvalidOperationException(
                $"No identity named '{identityName}' is registered. Run 'twig auth login --identity {identityName}' first.");

        var connectionRef = ConnectionRefResolver.Compute(organization, project);

        // Keep the connections row in sync. Not FK-depended-on from the binding
        // tables, but downstream join/report code expects a row for every
        // referenced connection_ref.
        var upsertConn = await _registry.EnsureBindingConnectionAsync(connectionRef, organization, project, ct).ConfigureAwait(false);
        if (!upsertConn.IsSuccess) throw new InvalidOperationException($"Registry refused connection upsert: {upsertConn.Error}");

        var now = _clock.GetUtcNow();
        var bindingId = ComputeBindingId(connectionRef, identity.IdentityId);
        var pending = new BindingRow(
            BindingId: bindingId,
            ConnectionRef: connectionRef,
            IdentityId: identity.IdentityId,
            Revision: 1,
            CreatedAt: now,
            UpdatedAt: now);
        var upsert = await _registry.InsertOrGetBindingAsync(pending, ct).ConfigureAwait(false);
        if (!upsert.IsSuccess) throw new InvalidOperationException($"Registry refused binding insert: {upsert.Error}");
        var row = upsert.Value;

        if (makeDefault)
        {
            var defaultRow = new DefaultBindingRow(
                ConnectionRef: connectionRef,
                BindingId: row.BindingId,
                Revision: row.Revision,
                UpdatedAt: now);
            var setDefault = await _registry.UpsertDefaultBindingAsync(defaultRow, ct).ConfigureAwait(false);
            if (!setDefault.IsSuccess)
            {
                if (string.Equals(setDefault.Error, "default-binding-exists", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"A different default binding is already set for {organization}/{project}. Identity transitions are deliberate guarded management operations and not a side effect of 'twig connection bind --default'.");
                }
                throw new InvalidOperationException($"Registry refused default binding: {setDefault.Error}");
            }
        }

        return ToBinding(row);
    }

    public async Task<IReadOnlyList<IdentityBinding>> ListBindingsAsync(
        string organization,
        string project,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var connectionRef = ConnectionRefResolver.Compute(organization, project);
        var rows = await _registry.ListBindingsForConnectionAsync(connectionRef, ct).ConfigureAwait(false);
        if (!rows.IsSuccess) throw new InvalidOperationException($"Registry refused binding list: {rows.Error}");
        var result = new List<IdentityBinding>(rows.Value.Count);
        foreach (var row in rows.Value) result.Add(ToBinding(row));
        return result;
    }

    public async Task<ResolvedConnectionBinding> ResolveAsync(
        TwigConfiguration configuration,
        TwigPaths paths,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);
        var connectionRef = ConnectionRefResolver.Compute(configuration);

        // Validate the local attachment before selecting an identity.
        using var attachmentStore = new WorktreeLocalAttachmentStore(paths, configuration, _clock);
        var attachment = await attachmentStore.ReadWithRevisionAsync(ct).ConfigureAwait(false);
        if (!attachment.IsSuccess)
            throw new InvalidOperationException($"Managed attachment validation refused: {attachment.Error}");
        if (!WorktreeAnchorDetector.TryDetect(paths.StartDir ?? paths.TwigDir, out var anchor, out var anchorFailure))
            throw new InvalidOperationException(
                $"Current directory is not an attached managed worktree ({anchorFailure}). Run 'twig init' to attach, or use the bootstrap provider for explicit init.");
        var fingerprint = WorktreeFingerprintProvider.CanonicalJson(anchor);
        var worktreeRow = await _registry.FindWorktreeAsync(fingerprint, ct).ConfigureAwait(false);
        if (!worktreeRow.IsSuccess) throw new InvalidOperationException($"Registry refused worktree lookup: {worktreeRow.Error}");
        if (worktreeRow.Value is not { } wt)
            throw new InvalidOperationException(
                $"Managed worktree at {anchor.WorktreeRoot} is not registered in system.db. Run 'twig init' to attach it before issuing work HTTP.");
        if (wt.RetiredAt is not null)
            throw new InvalidOperationException(
                $"Managed worktree at {anchor.WorktreeRoot} is retired. Reattach via 'twig init' before issuing work HTTP.");
        if (!string.Equals(wt.ConnectionRef, connectionRef, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Attached worktree is bound to a different connection (expected {connectionRef}, stored {wt.ConnectionRef}). Refusing to project across connection boundaries.");

        // 2. Default binding must exist. A single binding without a default is
        //    deliberately ambiguous — the caller must opt in via 'bind --default'
        //    so an implicit identity never slips in once a second alias lands.
        var defaultRow = await _registry.FindDefaultBindingAsync(connectionRef, ct).ConfigureAwait(false);
        if (!defaultRow.IsSuccess) throw new InvalidOperationException($"Registry refused default lookup: {defaultRow.Error}");
        if (defaultRow.Value is not { } d)
        {
            var bindings = await _registry.ListBindingsForConnectionAsync(connectionRef, ct).ConfigureAwait(false);
            if (!bindings.IsSuccess) throw new InvalidOperationException($"Registry refused binding list: {bindings.Error}");
            if (bindings.Value.Count == 0)
                throw new InvalidOperationException(
                    $"No identity is bound to {configuration.Organization}/{configuration.Project}. Run 'twig connection bind --identity <name> --default' to bind one.");
            throw new InvalidOperationException(
                $"{bindings.Value.Count} identities are bound to {configuration.Organization}/{configuration.Project} but no default is set. Run 'twig connection bind --identity <name> --default' to pick one explicitly.");
        }

        var bindingRes = await _registry.FindBindingByIdAsync(d.BindingId, ct).ConfigureAwait(false);
        if (!bindingRes.IsSuccess) throw new InvalidOperationException($"Registry refused binding lookup: {bindingRes.Error}");
        if (bindingRes.Value is not { } b)
            throw new InvalidOperationException(
                $"Default binding {d.BindingId} references a missing binding row — the identity registry is inconsistent. Rebuild it via 'twig connection bind --identity <name> --default'.");
        if (!string.Equals(b.ConnectionRef, connectionRef, StringComparison.Ordinal))
            throw new InvalidOperationException("Default binding belongs to another declared connection; refusing account crossover.");
        var identityRes = await _registry.FindIdentityByIdAsync(b.IdentityId, ct).ConfigureAwait(false);
        if (!identityRes.IsSuccess) throw new InvalidOperationException($"Registry refused identity lookup: {identityRes.Error}");
        if (identityRes.Value is not { } idRow)
            throw new InvalidOperationException(
                $"Binding {b.BindingId} references a missing identity row — the identity registry is inconsistent.");

        return new ResolvedConnectionBinding(
            Binding: ToBinding(b),
            Identity: ToIdentity(idRow),
            WorktreeRoot: anchor.WorktreeRoot,
            SelectionSource: "connection-default-binding",
            SelectionRevision: d.Revision,
            Operation: new ConnectionOperationSnapshot(
                configuration.Organization, configuration.Project, configuration.Team,
                fingerprint, attachment.Value.Revision,
                configuration.IsLegacyMode ? paths.ConfigPath : paths.RepoConfigPath,
                File.Exists(paths.ConfigPath) ? paths.ConfigPath : "built-in-defaults",
                configuration.Display.Icons,
                configuration.Display.IconsOverride is not null ? paths.ConfigPath
                    : GlobalDisplayPreferences.Load(paths.GlobalDisplayPath).Icons is not null
                        ? paths.GlobalDisplayPath : "built-in-defaults"));
    }
    public async Task<IAuthenticationProvider> CreateBootstrapProviderAsync(
        TwigConfiguration configuration,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionRef = ConnectionRefResolver.Compute(configuration);

        var defaultRow = await _registry.FindDefaultBindingAsync(connectionRef, ct).ConfigureAwait(false);
        if (!defaultRow.IsSuccess) throw new InvalidOperationException($"Registry refused default lookup: {defaultRow.Error}");
        if (defaultRow.Value is not { } d)
            throw new InvalidOperationException(
                $"No default identity is bound to {configuration.Organization}/{configuration.Project}. Bootstrap requires an explicitly-selected identity; run 'twig connection bind --identity <name> --default' first.");

        var bindingRes = await _registry.FindBindingByIdAsync(d.BindingId, ct).ConfigureAwait(false);
        if (!bindingRes.IsSuccess) throw new InvalidOperationException($"Registry refused binding lookup: {bindingRes.Error}");
        if (bindingRes.Value is not { } b)
            throw new InvalidOperationException(
                $"Default binding {d.BindingId} references a missing binding row — the identity registry is inconsistent.");
        if (!string.Equals(b.ConnectionRef, connectionRef, StringComparison.Ordinal))
            throw new InvalidOperationException("Bootstrap binding belongs to another declared connection; refusing account crossover.");
        var identityRes = await _registry.FindIdentityByIdAsync(b.IdentityId, ct).ConfigureAwait(false);
        if (!identityRes.IsSuccess) throw new InvalidOperationException($"Registry refused identity lookup: {identityRes.Error}");
        if (identityRes.Value is not { } idRow)
            throw new InvalidOperationException(
                $"Binding {b.BindingId} references a missing identity row — the identity registry is inconsistent.");

        return CreateProvider(ToIdentity(idRow));
    }

    public IAuthenticationProvider CreateAuthenticationProvider(ResolvedConnectionBinding binding)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(binding);
        return CreateProvider(binding.Identity);
    }

    private BoundConnectionAuthenticationProvider CreateProvider(AuthenticationIdentity identity)
    {
        var credentialPath = ResolveCredentialPath(identity.CredentialRef);
        var tokenCachePath = ResolveTokenCachePath(identity.CredentialRef);
        return new BoundConnectionAuthenticationProvider(
            identity: identity,
            refreshStore: new TwigRefreshTokenStore(credentialPath),
            tokenCache: new TwigTokenFileCache(tokenCachePath),
            refresher: _refresher,
            clock: _clock);
    }

    private string ResolveCredentialPath(string credentialRef)
    {
        var safe = ValidateCredentialRef(credentialRef);
        return Path.Combine(_credentialsDir, safe + ".json");
    }

    private string ResolveTokenCachePath(string credentialRef)
    {
        var safe = ValidateCredentialRef(credentialRef);
        return Path.Combine(_credentialsDir, safe + ".token-cache");
    }

    private void WriteCredentialAtomic(string credentialRef, TwigRefreshTokenStoreEntry entry)
    {
        var path = ResolveCredentialPath(credentialRef);
        var store = new TwigRefreshTokenStore(path);
        store.TryWrite(entry);
        if (!string.Equals(store.TryRead()?.RefreshToken, entry.RefreshToken, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Credential file write failed at {path}. Verify {_credentialsDir} is writable and retry.");
    }

    private void TryDeleteCredential(string credentialRef)
    {
        try
        {
            var path = ResolveCredentialPath(credentialRef);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort; this attempt owns a unique, unreferenced file.
        }
    }

    private static string ValidateCredentialRef(string credentialRef)
    {
        if (credentialRef.Length != 37 || !credentialRef.StartsWith("cred-", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid opaque credential reference.");
        foreach (var character in credentialRef.AsSpan(5))
            if (!char.IsAsciiHexDigit(character))
                throw new InvalidOperationException("Invalid opaque credential reference.");
        return credentialRef;
    }


    private static string ComputeIdentityId(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("identity\0" + name.ToLowerInvariant()));
        return "id-" + Convert.ToHexStringLower(hash)[..24];
    }

    private static string ComputeBindingId(string connectionRef, string identityId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("binding\0" + connectionRef + "\0" + identityId));
        return "bind-" + Convert.ToHexStringLower(hash)[..24];
    }

    private static AuthenticationIdentity ToIdentity(IdentityRow row) =>
        new(
            IdentityId: row.IdentityId,
            Name: row.Name,
            TenantId: row.TenantId,
            ObjectId: row.ObjectId,
            Issuer: row.Issuer,
            AuthorityHost: row.AuthorityHost,
            CredentialRef: row.CredentialRef,
            AccountName: row.AccountName);

    private static IdentityBinding ToBinding(BindingRow row) =>
        new(
            BindingId: row.BindingId,
            ConnectionRef: row.ConnectionRef,
            IdentityId: row.IdentityId,
            Revision: row.Revision);

    private static string Require(string? value, string field) =>
        !string.IsNullOrWhiteSpace(value) ? value!
            : throw new ArgumentException($"Credential field '{field}' must be non-empty.", nameof(value));

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ConnectionBindingService));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registry.Dispose();
    }
}
