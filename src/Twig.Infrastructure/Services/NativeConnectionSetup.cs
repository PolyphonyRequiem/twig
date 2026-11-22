using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Auth.InteractiveAuth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence;
using Twig.Providers.Native;

namespace Twig.Infrastructure.Services;

public sealed record NativeAadEnrollmentRequest(string Alias, string? Tenant = null);
public sealed record NativeAadEnrollment(string Alias, string IdentityId, string TenantId, string ObjectId,
    string Issuer, string AuthorityHost, string? AccountName);
public sealed record NativeAdoBindingRequest(string Organization, string Project, string IdentityAlias);
public sealed record NativeAdoBinding(string BindingId, string ConnectionRef, string IdentityId, long Revision);
public abstract record NativeSetupTarget;
public sealed record NativeAdoSetupTarget(string Organization, string Project, string BindingId,
    string? Team = null, string? GitProject = null) : NativeSetupTarget;
public sealed record NativeGitHubSetupTarget(string Host, string Repository, string Account) : NativeSetupTarget;
public sealed record NativeGitHubAccount(string Login, bool Active);
public sealed record NativeTwigSetupTarget(string Database) : NativeSetupTarget;
public sealed record NativeFreshSetupRequest(string WorkspaceRoot, NativeSetupTarget Target, string? StoreFile = null);
public sealed record NativeManagedSetupResult(string WorkspaceRoot, NativeSourceObservation Observation);

public enum NativeSetupPhase { Validation, Enrollment, Binding, Initialization, Readback, Cancelled }
public enum NativeSetupEffectState { Possible, Confirmed }
public enum NativeSetupEffectKind
{
    NativeHome, CredentialsDirectory, Registry, CredentialWrite, IdentityRegistration, ConnectionBinding,
    ConnectionRegistration, WorkspaceDirectory, InitializationLock, IgnoreFile, Configuration,
    Attachment, WorktreeRegistration, ProviderTarget, CoreStore, WorkspacePin, WorkspaceMarker,
    CoreRegistration, SourceReadback,
    InteractiveSignIn,
    NativeAuthority,
    ManagedWorkspace,
}
public sealed record NativeSetupEffect(NativeSetupEffectKind Kind, NativeSetupEffectState State);
public sealed record NativeSetupFailureInfo(NativeSetupPhase Phase, bool Cancelled, ImmutableArray<NativeSetupEffect> Effects);

/// <summary>
/// Explicit human-host setup. Construction is inert; each operation owns its native composition.
/// Failures remain failures, retain possible local effects, and confer no retry or replay authority.
/// </summary>
public sealed partial class NativeConnectionSetup
{
    /// <summary>Resolves the native metadata home without creating directories, opening storage or selecting an account.</summary>
    public static string ResolveUserHome() => ConnectionBindingService.ResolveUserHome();

    private readonly Func<NativeSetupEffects, IConnectionBindingService> bindings;
    private readonly bool ownsBindings;
    private readonly Func<LoopbackPkceFlow> pkce;
    private readonly CoreTransportOverride? transport;
    private readonly IBoundedProcessRunner? processes;
    private static readonly string[] GitHubTokenVariables =
        ["GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN"];
    private readonly string? managedRoot;

    public NativeConnectionSetup(string metadataHome)
    {
        if (string.IsNullOrWhiteSpace(metadataHome) || !Path.IsPathFullyQualified(metadataHome))
            throw new ArgumentException("An explicit absolute native metadata home is required.", nameof(metadataHome));
        var home = Path.GetFullPath(metadataHome);
        managedRoot = Path.Combine(home, "sources");
        bindings = effects => new ConnectionBindingService(home, setupEffects: effects);
        ownsBindings = true;
        pkce = () => new LoopbackPkceFlow();
    }

    internal NativeConnectionSetup(Func<NativeSetupEffects, IConnectionBindingService> bindings,
        Func<LoopbackPkceFlow>? pkce = null, CoreTransportOverride? transport = null,
        IBoundedProcessRunner? processes = null, bool ownsBindings = false, string? managedRoot = null)
    {
        this.bindings = bindings;
        this.pkce = pkce ?? (() => new LoopbackPkceFlow());
        this.transport = transport;
        this.processes = processes;
        this.ownsBindings = ownsBindings;
        this.managedRoot = managedRoot;
    }

    public async Task<Result<NativeAadEnrollment[]>> ListAadIdentitiesAsync(CancellationToken ct = default)
    {
        IConnectionBindingService? resolved = null;
        var effects = new NativeSetupEffects { Phase = NativeSetupPhase.Enrollment };
        try
        {
            ct.ThrowIfCancellationRequested();
            resolved = bindings(effects);
            var identities = await resolved.ListIdentitiesAsync(ct).ConfigureAwait(false);
            return Result<NativeAadEnrollment[]>.Ok(identities.Where(identity => identity.Method == "aad")
                .Select(ConnectionBindingService.Enrollment).ToArray());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { return effects.Cancel<NativeAadEnrollment[]>(); }
        catch (Exception error) when (NativeSetupEffects.IsExpected(error))
        { return effects.Exception<NativeAadEnrollment[]>(error); }
        finally { if (ownsBindings && resolved is IDisposable disposable) disposable.Dispose(); }
    }

    /// <summary>Runs native loopback PKCE for one explicit alias; only the supplied callback presents its URL.</summary>
    public async Task<Result<NativeAadEnrollment>> EnrollAadAsync(NativeAadEnrollmentRequest request,
        Action<Uri> authorizationUrl, CancellationToken ct = default)
    {
        var effects = new NativeSetupEffects();
        var result = await NativeSetupEnrollment.EnrollAsync(request?.Alias, request?.Tenant,
            (tenant, token) => pkce().RunAsync(NativeSetupEnrollment.ClientId, tenant, launchBrowser: false,
                urlReporter: Report, ct: token),
            () => bindings(effects), ownsBindings, effects, ct, authorizationUrl is not null).ConfigureAwait(false);
        return result.IsSuccess
            ? Result<NativeAadEnrollment>.Ok(new(result.Value.Name, result.Value.IdentityId, result.Value.TenantId,
                result.Value.ObjectId, result.Value.Issuer, result.Value.AuthorityHost, result.Value.AccountName))
            : Result<NativeAadEnrollment>.Fail(result.Error);

        void Report(string url)
        {
            try { authorizationUrl(new Uri(url)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                throw new NativeSetupException(new(ErrorCode.Unavailable, "The native authorization URL callback failed; sign-in was stopped."));
            }
        }
    }

    /// <summary>Ensures one explicitly named ADO binding. Never creates or switches an endpoint default.</summary>
    public async Task<Result<NativeAdoBinding>> CreateAdoBindingAsync(NativeAdoBindingRequest request,
        CancellationToken ct = default)
    {
        var effects = new NativeSetupEffects();
        var result = await NativeSetupEnrollment.BindAsync(request?.Organization, request?.Project,
            request?.IdentityAlias, makeDefault: false, () => bindings(effects), ownsBindings, effects, ct).ConfigureAwait(false);
        return result.IsSuccess ? Result<NativeAdoBinding>.Ok(new(result.Value.BindingId, result.Value.ConnectionRef,
            result.Value.IdentityId, result.Value.Revision)) : Result<NativeAdoBinding>.Fail(result.Error);
    }

    /// <summary>
    /// Creates only a genuinely fresh Git workspace, then observes schema and online access against its creation stamp.
    /// Cancellation or failed readback preserves partial native state; it never returns initialized-as-ready.
    /// </summary>
    public async Task<Result<NativeSourceObservation>> InitializeFreshAsync(NativeFreshSetupRequest request,
        CancellationToken ct = default)
        => await InitializeFreshAsync(request, new NativeSetupEffects(), ct).ConfigureAwait(false);

    /// <summary>Creates a native-owned private client context for an explicit provider target without modifying a source checkout.</summary>
    public async Task<Result<NativeManagedSetupResult>> InitializeManagedAsync(NativeSetupTarget target,
        CancellationToken ct = default)
    {
        var effects = new NativeSetupEffects();
        try
        {
            ct.ThrowIfCancellationRequested();
            if (managedRoot is null || target is not (NativeTwigSetupTarget or NativeGitHubSetupTarget or NativeAdoSetupTarget))
                return effects.Fail<NativeManagedSetupResult>(new(ErrorCode.Unsupported, "Managed setup requires an explicit Twig, GitHub or bound ADO connection."));
            if (target is NativeAdoSetupTarget ado)
            {
                if (NativeSetupEnrollment.AdoProblem(ado.Organization, ado.Project) is { } problem)
                    return effects.Fail<NativeManagedSetupResult>(new(ErrorCode.InvalidInput, problem));
                if (!ExplicitBinding(ado.BindingId))
                    return effects.Fail<NativeManagedSetupResult>(new(ErrorCode.InvalidInput, "Choose an exact native ADO binding; no default was inferred."));
            }
            if (target is NativeTwigSetupTarget native && !NativeDatabaseName.IsValid(native.Database))
                return effects.Fail<NativeManagedSetupResult>(new(ErrorCode.InvalidInput, "Choose an explicit native database name: 1-64 lowercase letters, digits or internal hyphens."));
            if (target is NativeGitHubSetupTarget github)
            {
                var resolved = await ResolveManagedGitHubAccountAsync(github, processes ?? new BoundedProcessRunner(), ct).ConfigureAwait(false);
                if (!resolved.IsSuccess) return effects.Fail<NativeManagedSetupResult>(resolved.Error);
                target = resolved.Value;
            }
            ct.ThrowIfCancellationRequested();
            for (var path = managedRoot; path is not null; path = Path.GetDirectoryName(path))
                if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    return effects.Fail<NativeManagedSetupResult>(new(ErrorCode.PolicyViolation, "Managed connection storage cannot traverse a linked directory."));
            var root = Path.Combine(managedRoot, Guid.NewGuid().ToString("N"));
            effects.Phase = NativeSetupPhase.Initialization;
            effects.Before(NativeSetupEffectKind.ManagedWorkspace);
            Directory.CreateDirectory(managedRoot);
            using var reservation = new FileStream(root + ".lock", FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (Path.Exists(root)) throw new IOException("The new managed context already exists.");
            // A private Git anchor is a client-cache implementation detail, never a native source or Bench identity.
            var initialized = await new BoundedProcessRunner().RunAsync("git", ["init", "--quiet", "--template=", root],
                TimeSpan.FromSeconds(10), 4096, ct,
                ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY",
                    "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG", "GIT_CONFIG_COUNT", "GIT_TEMPLATE_DIR"]).ConfigureAwait(false);
            if (initialized.Outcome != BoundedProcessOutcome.Exited || initialized.ExitCode != 0)
                return effects.Fail<NativeManagedSetupResult>(new(ErrorCode.Unavailable,
                    "The private connection context could not be initialized. Possible local effects remain; setup was not retried."));
            effects.Confirm(NativeSetupEffectKind.ManagedWorkspace);
            var observed = await InitializeFreshAsync(new(root, target), effects, ct).ConfigureAwait(false);
            return observed.IsSuccess ? Result<NativeManagedSetupResult>.Ok(new(root, observed.Value))
                : Result<NativeManagedSetupResult>.Fail(observed.Error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return effects.Cancel<NativeManagedSetupResult>(); }
        catch (Exception ex) when (NativeSetupEffects.IsExpected(ex)) { return effects.Exception<NativeManagedSetupResult>(ex); }
    }

    public async Task<Result<NativeGitHubAccount[]>> ListGitHubAccountsAsync(CancellationToken ct)
    {
        const string host = "github.com";
        var status = await (processes ?? new BoundedProcessRunner()).RunAsync("gh",
            ["auth", "status", "--hostname", host, "--json", "hosts"],
            TimeSpan.FromSeconds(15), 64 * 1024, ct, GitHubTokenVariables).ConfigureAwait(false);
        if (status.Outcome == BoundedProcessOutcome.NotStarted)
            return Result<NativeGitHubAccount[]>.Fail(ErrorCode.Unavailable, "Install GitHub CLI on the Twig daemon host, then refresh.");
        if (status.Outcome != BoundedProcessOutcome.Exited || status.ExitCode != 0)
            return Result<NativeGitHubAccount[]>.Fail(ErrorCode.Unavailable,
                "GitHub CLI account discovery did not complete. Check that gh supports auth status --json, then refresh.");

        try
        {
            using var json = JsonDocument.Parse(status.StandardOutput);
            var hosts = json.RootElement.GetProperty("hosts");
            if (!hosts.TryGetProperty(host, out var accounts))
                return Result<NativeGitHubAccount[]>.Ok([]);
            var usable = new Dictionary<string, NativeGitHubAccount>(StringComparer.OrdinalIgnoreCase);
            foreach (var account in accounts.EnumerateArray())
            {
                if (account.GetProperty("state").GetString() != "success") continue;
                var login = account.GetProperty("login").GetString();
                if (login is null || !IsGitHubLogin(login)) continue;
                var active = account.GetProperty("active").GetBoolean();
                if (active || !usable.ContainsKey(login)) usable[login] = new(login, active);
            }
            return Result<NativeGitHubAccount[]>.Ok(usable.Values.OrderByDescending(account => account.Active)
                .ThenBy(account => account.Login, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Result<NativeGitHubAccount[]>.Fail(ErrorCode.Unavailable, "GitHub CLI returned an unreadable account list. Update gh, then refresh.");
        }
    }

    public static bool IsGitHubLogin(string login) =>
        Regex.IsMatch(login, @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}\z", RegexOptions.CultureInvariant);

    internal static async Task<Result<NativeGitHubSetupTarget>> ResolveManagedGitHubAccountAsync(
        NativeGitHubSetupTarget target, IBoundedProcessRunner runner, CancellationToken ct)
    {
        if (!GitHubWorkspaceScope.TryCreate(new GitHubConfig { Host = target.Host, Repository = target.Repository }, out _, out _)
            || target.Account is null || target.Account.StartsWith('-')
            || target.Account.Any(char.IsWhiteSpace) || target.Account.Any(char.IsControl))
            return Result<NativeGitHubSetupTarget>.Fail(ErrorCode.InvalidInput, "Choose valid GitHub coordinates and an optional named account.");
        if (target.Account.Length != 0) return Result<NativeGitHubSetupTarget>.Ok(target);
        var account = await runner.RunAsync("gh", ["api", "--hostname", target.Host, "user", "--jq", ".login"],
            TimeSpan.FromSeconds(10), 256, ct,
            GitHubTokenVariables).ConfigureAwait(false);
        if (account.Outcome != BoundedProcessOutcome.Exited || account.ExitCode != 0
            || !IsGitHubLogin(account.StandardOutput.Trim()))
            return Result<NativeGitHubSetupTarget>.Fail(ErrorCode.Unavailable,
                "The signed-in GitHub CLI account could not be verified. Sign in through gh and add the connection again; no account was switched.");
        return Result<NativeGitHubSetupTarget>.Ok(target with { Account = account.StandardOutput.Trim() });
    }

    private async Task<Result<NativeSourceObservation>> InitializeFreshAsync(NativeFreshSetupRequest request,
        NativeSetupEffects effects, CancellationToken ct)
    {
        IConnectionBindingService? resolved = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (request is null || request.Target is null)
                return effects.Fail<NativeSourceObservation>(new(ErrorCode.InvalidInput, "An explicit fresh workspace and provider target are required."));
            if (string.IsNullOrWhiteSpace(request.WorkspaceRoot) || !Path.IsPathFullyQualified(request.WorkspaceRoot)
                || !NativePathComparer.Instance.Equals(request.WorkspaceRoot, Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.WorkspaceRoot))))
                return effects.Fail<NativeSourceObservation>(new(ErrorCode.InvalidInput, "An explicit canonical absolute fresh Git worktree root is required."));
            NativeWorkspaceSetupArguments arguments;
            switch (request.Target)
            {
                case NativeAdoSetupTarget ado when ExplicitBinding(ado.BindingId):
                    arguments = new(request.WorkspaceRoot, ado.Organization, ado.Project, ado.Team, ado.GitProject,
                        "ado", null, null, ado.BindingId, request.StoreFile, null);
                    break;
                case NativeGitHubSetupTarget github:
                    arguments = new(request.WorkspaceRoot, "", "", null, null, "github",
                        github.Host, github.Repository, null, request.StoreFile, github.Account);
                    break;
                case NativeTwigSetupTarget native when NativeDatabaseName.IsValid(native.Database):
                    arguments = new(request.WorkspaceRoot, "", "", null, null, "twig",
                        null, null, null, request.StoreFile, null, Database: native.Database);
                    break;
                default:
                    return effects.Fail<NativeSourceObservation>(new(ErrorCode.InvalidInput, "Choose an explicit ADO binding, named GitHub account or named Twig database; no default was inferred."));
            }
            var initialized = await NativeWorkspaceSetup.InitializeAsync(arguments,
                () => resolved ??= bindings(effects), effects, ct, transport, processes).ConfigureAwait(false);
            if (!initialized.IsSuccess) return Result<NativeSourceObservation>.Fail(initialized.Error);
            effects.Phase = NativeSetupPhase.Readback;
            effects.Before(NativeSetupEffectKind.SourceReadback);
            void Configure(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
            {
                Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, resolved!);
                if (transport is not null)
                    Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, transport);
                if (processes is not null)
                    Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, processes);
            }
            var expected = initialized.Value.Source;
            var observed = await NativeSourceReader.ReadAsync(arguments.WorkspaceRoot, expected,
                observeSchema: true, Configure, ct).ConfigureAwait(false);
            if (!observed.IsSuccess) return effects.Fail<NativeSourceObservation>(observed.Error);
            if (observed.Value.Source != expected || observed.Value.Schema is not { IsSuccess: true }
                || observed.Value.Schema.Value.Connection != expected.Connection)
                return effects.Fail<NativeSourceObservation>(new(ErrorCode.Conflict, "Fresh setup readback did not match its exact creation evidence."));
            var online = await NativeSourceReader.ReadOnlineAsync(arguments.WorkspaceRoot, expected, Configure, ct).ConfigureAwait(false);
            if (!online.IsSuccess) return effects.Fail<NativeSourceObservation>(online.Error);
            effects.Confirm(NativeSetupEffectKind.SourceReadback);
            return observed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return effects.Cancel<NativeSourceObservation>();
        }
        catch (Exception ex) when (NativeSetupEffects.IsExpected(ex))
        {
            return effects.Exception<NativeSourceObservation>(ex);
        }
        finally
        {
            if (ownsBindings && resolved is IDisposable disposable) disposable.Dispose();
        }
    }

    private static bool ExplicitBinding(string? binding)
        => !string.IsNullOrWhiteSpace(binding) && binding == binding.Trim()
            && !binding.Any(char.IsWhiteSpace) && !binding.Any(char.IsControl);

    /// <summary>Decodes only native setup diagnostics. Foreign, malformed or unsupported evidence explicitly refuses.</summary>
    public static Result<NativeSetupFailureInfo> ReadFailure(Error error) => NativeSetupEffects.Read(error);
}
