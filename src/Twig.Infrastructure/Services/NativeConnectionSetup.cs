using System.Collections.Immutable;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Auth.InteractiveAuth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence;

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
public sealed record NativeFreshSetupRequest(string WorkspaceRoot, NativeSetupTarget Target, string? StoreFile = null);

public enum NativeSetupPhase { Validation, Enrollment, Binding, Initialization, Readback, Cancelled }
public enum NativeSetupEffectState { Possible, Confirmed }
public enum NativeSetupEffectKind
{
    NativeHome, CredentialsDirectory, Registry, CredentialWrite, IdentityRegistration, ConnectionBinding,
    ConnectionRegistration, WorkspaceDirectory, InitializationLock, IgnoreFile, Configuration,
    Attachment, WorktreeRegistration, ProviderTarget, CoreStore, WorkspacePin, WorkspaceMarker,
    CoreRegistration, SourceReadback,
    InteractiveSignIn,
}
public sealed record NativeSetupEffect(NativeSetupEffectKind Kind, NativeSetupEffectState State);
public sealed record NativeSetupFailureInfo(NativeSetupPhase Phase, bool Cancelled, ImmutableArray<NativeSetupEffect> Effects);

/// <summary>
/// Explicit human-host setup. Construction is inert; each operation owns its native composition.
/// Failures remain failures, retain possible local effects, and confer no retry or replay authority.
/// </summary>
public sealed class NativeConnectionSetup
{
    /// <summary>Resolves the native metadata home without creating directories, opening storage or selecting an account.</summary>
    public static string ResolveUserHome() => ConnectionBindingService.ResolveUserHome();

    private readonly Func<NativeSetupEffects, IConnectionBindingService> bindings;
    private readonly bool ownsBindings;
    private readonly Func<LoopbackPkceFlow> pkce;
    private readonly CoreTransportOverride? transport;
    private readonly IBoundedProcessRunner? processes;

    public NativeConnectionSetup(string metadataHome)
    {
        if (string.IsNullOrWhiteSpace(metadataHome) || !Path.IsPathFullyQualified(metadataHome))
            throw new ArgumentException("An explicit absolute native metadata home is required.", nameof(metadataHome));
        var home = Path.GetFullPath(metadataHome);
        bindings = effects => new ConnectionBindingService(home, setupEffects: effects);
        ownsBindings = true;
        pkce = () => new LoopbackPkceFlow();
    }

    internal NativeConnectionSetup(Func<NativeSetupEffects, IConnectionBindingService> bindings,
        Func<LoopbackPkceFlow>? pkce = null, CoreTransportOverride? transport = null,
        IBoundedProcessRunner? processes = null, bool ownsBindings = false)
    {
        this.bindings = bindings;
        this.pkce = pkce ?? (() => new LoopbackPkceFlow());
        this.transport = transport;
        this.processes = processes;
        this.ownsBindings = ownsBindings;
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
    {
        var effects = new NativeSetupEffects();
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
                case NativeAdoSetupTarget ado when !string.IsNullOrWhiteSpace(ado.BindingId)
                    && ado.BindingId == ado.BindingId.Trim() && !ado.BindingId.Any(char.IsWhiteSpace) && !ado.BindingId.Any(char.IsControl):
                    arguments = new(request.WorkspaceRoot, ado.Organization, ado.Project, ado.Team, ado.GitProject,
                        "ado", null, null, ado.BindingId, request.StoreFile, null);
                    break;
                case NativeGitHubSetupTarget github:
                    arguments = new(request.WorkspaceRoot, "", "", null, null, "github",
                        github.Host, github.Repository, null, request.StoreFile, github.Account);
                    break;
                default:
                    return effects.Fail<NativeSourceObservation>(new(ErrorCode.InvalidInput, "Choose an explicit ADO binding or named GitHub account; no default was inferred."));
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

    /// <summary>Decodes only native setup diagnostics. Foreign, malformed or unsupported evidence explicitly refuses.</summary>
    public static Result<NativeSetupFailureInfo> ReadFailure(Error error) => NativeSetupEffects.Read(error);
}
