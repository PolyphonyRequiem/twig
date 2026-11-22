using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Providers.Native;

namespace Twig.Infrastructure.Services;

internal static class NativeSetupPreflight
{
    internal static string? ManagedTargetProblem(NativeSetupTarget target) => target switch
    {
        NativeAdoSetupTarget ado => NativeSetupEnrollment.AdoProblem(ado.Organization, ado.Project)
            ?? (ExplicitBinding(ado.BindingId) ? null : "Choose an exact native ADO binding; no default was inferred."),
        NativeTwigSetupTarget native when !NativeDatabaseName.IsValid(native.Database)
            => "Choose an explicit native database name: 1-64 lowercase letters, digits or internal hyphens.",
        NativeGitHubAppSetupTarget app when !ValidGitHubApp(app)
            => "Choose a github.com repository and an explicit GitHub App identity and client ID.",
        _ => null,
    };

    internal static bool ValidManagedGitHubTarget(NativeGitHubSetupTarget target)
        => ValidGitHubTarget(target, allowDiscovery: true);

    internal static NativeWorkspaceSetupArguments? ExplicitWorkspaceArguments(NativeFreshSetupRequest request,
        Connection? expectedNativeAuthority) => request.Target switch
    {
        NativeAdoSetupTarget ado when ExplicitBinding(ado.BindingId)
            => new(request.WorkspaceRoot, ado.Organization, ado.Project, ado.Team, ado.GitProject,
                "ado", null, null, ado.BindingId, request.StoreFile, null),
        NativeGitHubSetupTarget github when ValidGitHubTarget(github, allowDiscovery: false)
            => new(request.WorkspaceRoot, "", "", null, null, "github", github.Host, github.Repository,
                null, request.StoreFile, github.Account),
        NativeGitHubAppSetupTarget app when ValidGitHubApp(app)
            => new(request.WorkspaceRoot, "", "", null, null, "github", app.Host, app.Repository,
                null, request.StoreFile, null, GitHubApp: app),
        NativeTwigSetupTarget native when NativeDatabaseName.IsValid(native.Database)
            => new(request.WorkspaceRoot, "", "", null, null, "twig", null, null, null,
                request.StoreFile, null, Database: native.Database, ExpectedNativeAuthority: expectedNativeAuthority),
        _ => null,
    };

    internal static Result<string> ConfigureProvider(NativeWorkspaceSetupArguments args, TwigConfiguration config, bool hasManifest)
    {
        var kind = args.Provider is null ? config.ResolveWorkItemProviderKind() : WorkItemProviderSetting.Normalize(args.Provider);
        if (kind is not ("ado" or "github" or "twig"))
            return Invalid("provider-unknown: use --provider ado, github or twig.");
        if (hasManifest && kind != config.ResolveWorkItemProviderKind())
            return Invalid("Provider conflicts with the existing twig.json declaration; no configuration was changed.");
        config.Provider = kind;
        if (args.GitHubApp is { } app && (kind != "github" || args.Account is not null
            || args.Host != app.Host || args.Repository != app.Repository || !ValidGitHubApp(app)))
            return Invalid("Choose one named gh account or one explicit github.com App identity and client ID.");
        if (kind != "twig" && args.Database is not null)
            return Invalid("--database requires --provider twig. --core-store selects the separate client store.");

        if (kind == "github")
        {
            if (args.Organization.Length != 0 || args.Project.Length != 0 || args.Team is not null
                || args.GitProject is not null || args.Binding is not null)
                return Invalid("GitHub init uses --host, --repository and --account, not ADO coordinates/team/git-project/binding.");
            if (args.Account is not null && !ValidGitHubAccount(args.Account))
                return Invalid("--account must name an explicit gh account; the active gh account and ambient tokens are never inferred.");
            if (hasManifest && (args.Host is not null && args.Host != config.GitHub?.Host
                || args.Repository is not null && args.Repository != config.GitHub?.Repository))
                return Invalid("GitHub coordinates conflict with the existing twig.json declaration.");
            if (!hasManifest) config.GitHub = new GitHubConfig { Host = args.Host, Repository = args.Repository };
            if (!GitHubWorkspaceScope.TryCreate(config.GitHub, out _, out var scopeProblem)) return Invalid(scopeProblem);
            if (args.Account is not null)
            {
                config.GitHubAccount = args.Account;
                config.GitHubCredentialSource = "gh";
                config.GitHubOAuthIdentity = null;
                config.GitHubAppClientId = null;
            }
            else if (args.GitHubApp is { } selected)
            {
                config.GitHubCredentialSource = "github-app";
                config.GitHubOAuthIdentity = selected.Identity;
                config.GitHubAppClientId = selected.ClientId;
                config.GitHubAccount = null;
            }
        }
        else if (kind == "twig")
        {
            if (args.Organization.Length != 0 || args.Project.Length != 0 || args.Team is not null
                || args.GitProject is not null || args.Binding is not null || args.Host is not null
                || args.Repository is not null || args.Account is not null)
                return Invalid("To initialize the Twig provider, use --database <name>, not ADO/GitHub coordinates or credentials.");
            if (hasManifest && args.Database is not null && args.Database != config.NativeDatabase)
                return Invalid("The native database conflicts with the existing twig.json declaration; no configuration was changed.");
            var name = args.Database ?? config.NativeDatabase;
            if (!NativeDatabaseName.IsValid(name))
                return Invalid("Choose --database <name> (or nativeDatabase in twig.json): 1-64 lowercase letters, digits or internal hyphens. No default database is inferred.");
            config.NativeDatabase = name;
        }
        else
        {
            if (args.Host is not null || args.Repository is not null || args.Account is not null)
                return Invalid("--host, --repository and --account require --provider github.");
            if (hasManifest && args.Organization.Length == 0 && args.Project.Length == 0)
                args = args with { Organization = config.Organization, Project = config.Project };
            if (NativeSetupEnrollment.AdoProblem(args.Organization, args.Project) is { } adoProblem) return Invalid(adoProblem);
            if (hasManifest && (args.Organization != config.Organization || args.Project != config.Project
                || args.Team is not null && args.Team != config.Team || args.GitProject is not null && args.GitProject != config.Git.Project))
                return Invalid("ADO coordinates conflict with the existing twig.json declaration.");
            config.Organization = args.Organization;
            config.Project = args.Project;
            if (args.Team is not null) config.Team = args.Team;
            if (args.GitProject is not null) config.Git.Project = args.GitProject;
            if (args.Binding is not null && string.IsNullOrWhiteSpace(args.Binding))
                return Invalid("--binding requires an exact registered native binding ID.");
        }
        return Result<string>.Ok(kind);

        static Result<string> Invalid(string message) => Result<string>.Fail(ErrorCode.InvalidInput, message);
    }

    private static bool ExplicitBinding(string? binding)
        => !string.IsNullOrWhiteSpace(binding) && binding == binding.Trim()
            && !binding.Any(char.IsWhiteSpace) && !binding.Any(char.IsControl);

    private static bool ValidGitHubTarget(NativeGitHubSetupTarget target, bool allowDiscovery)
        => GitHubWorkspaceScope.TryCreate(new GitHubConfig { Host = target.Host, Repository = target.Repository }, out _, out _)
            && (allowDiscovery && target.Account == "" || ValidGitHubAccount(target.Account));

    private static bool ValidGitHubAccount(string? account)
        => !string.IsNullOrWhiteSpace(account) && account.Length <= 256 && !account.StartsWith('-')
            && !account.Any(char.IsWhiteSpace) && !account.Any(char.IsControl);

    private static bool ValidGitHubApp(NativeGitHubAppSetupTarget target)
    {
        if (target.Host != "github.com" || GitHubWorkspaceScope.NormalizeRepository(target.Repository) is null
            || string.IsNullOrWhiteSpace(target.ClientId)) return false;
        try { GitHubAppCredentialStore.ValidateSelection(target.ClientId, target.Identity); return true; }
        catch (GitHubDeviceFlowException) { return false; }
    }
}
