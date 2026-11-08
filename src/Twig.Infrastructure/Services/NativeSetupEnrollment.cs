using Twig.Core;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Auth.InteractiveAuth;

namespace Twig.Infrastructure.Services;

internal static class NativeSetupEnrollment
{
    internal const string ClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    internal static string? AliasProblem(string? alias)
        => string.IsNullOrWhiteSpace(alias) ? "Named enrollment requires --identity <alias>; no attached or ambient identity was inferred."
            : alias.Trim().Length > 64 || alias.Any(char.IsControl) ? "An explicit identity alias of at most 64 characters without control characters is required."
            : null;

    internal static string? AdoProblem(string? organization, string? project)
        => string.IsNullOrWhiteSpace(organization) || organization != organization.Trim() || organization.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(project) || project != project.Trim() || project.Any(char.IsControl)
            || !Uri.TryCreate(AdoEndpoint.NormalizeOrgUrl(organization).TrimEnd('/') + "/", UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != "https" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            ? "Supply an explicit secure ADO organization and non-empty project: twig init <org> <project>." : null;

    internal static async Task<Result<AuthenticationIdentity>> EnrollAsync(string? alias, string? tenant,
        Func<string, CancellationToken, Task<InteractiveAuthResult>> flow, Func<IConnectionBindingService> bindings,
        bool ownsBindings, NativeSetupEffects effects, CancellationToken ct, bool hasReporter = true)
    {
        IConnectionBindingService? resolved = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (AliasProblem(alias) is { } problem) return effects.Fail<AuthenticationIdentity>(new(ErrorCode.InvalidInput, problem));
            if (!hasReporter) return effects.Fail<AuthenticationIdentity>(new(ErrorCode.InvalidInput, "An authorization URL callback is required."));
            var authority = string.IsNullOrWhiteSpace(tenant) ? AuthorizeRequestBuilder.DefaultTenant : tenant;
            if (authority != authority.Trim() || authority.Any(char.IsControl)
                || authority.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '.')))
                return effects.Fail<AuthenticationIdentity>(new(ErrorCode.InvalidInput, "Choose a tenant ID, domain or native multi-tenant alias, not an authority URL."));
            effects.Phase = NativeSetupPhase.Enrollment;
            effects.Before(NativeSetupEffectKind.InteractiveSignIn);
            var result = await flow(authority, ct).ConfigureAwait(false);
            if (result.ErrorKind == InteractiveAuthErrorKind.Cancelled)
                return effects.Cancel<AuthenticationIdentity>();
            if (!result.Succeeded || result.Entry is null)
                return effects.Fail<AuthenticationIdentity>(new(ErrorCode.Unavailable, "Sign-in failed: " + result.ErrorMessage));
            effects.Confirm(NativeSetupEffectKind.InteractiveSignIn);
            ct.ThrowIfCancellationRequested();
            effects.Before(NativeSetupEffectKind.NativeHome);
            resolved = bindings();
            return Result<AuthenticationIdentity>.Ok(await resolved.RegisterAadIdentityAsync(alias!.Trim(), result.Entry, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return effects.Cancel<AuthenticationIdentity>(); }
        catch (Exception ex) when (NativeSetupEffects.IsExpected(ex)) { return effects.Exception<AuthenticationIdentity>(ex); }
        finally { if (ownsBindings && resolved is IDisposable disposable) disposable.Dispose(); }
    }

    internal static async Task<Result<IdentityBinding>> BindAsync(string? organization, string? project, string? alias,
        bool makeDefault, Func<IConnectionBindingService> bindings, bool ownsBindings, NativeSetupEffects effects, CancellationToken ct)
    {
        IConnectionBindingService? resolved = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (AdoProblem(organization, project) is { } problem) return effects.Fail<IdentityBinding>(new(ErrorCode.InvalidInput, problem));
            if (AliasProblem(alias) is { } aliasProblem) return effects.Fail<IdentityBinding>(new(ErrorCode.InvalidInput, aliasProblem));
            effects.Phase = NativeSetupPhase.Binding;
            effects.Before(NativeSetupEffectKind.NativeHome);
            resolved = bindings();
            var binding = await resolved.CreateBindingAsync(organization!, project!, alias!.Trim(), makeDefault, ct).ConfigureAwait(false);
            effects.Confirm(NativeSetupEffectKind.ConnectionBinding);
            return Result<IdentityBinding>.Ok(binding);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return effects.Cancel<IdentityBinding>(); }
        catch (Exception ex) when (NativeSetupEffects.IsExpected(ex)) { return effects.Exception<IdentityBinding>(ex); }
        finally { if (ownsBindings && resolved is IDisposable disposable) disposable.Dispose(); }
    }
}
