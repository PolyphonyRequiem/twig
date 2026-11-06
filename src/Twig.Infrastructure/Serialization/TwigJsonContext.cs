using System.Text.Json.Serialization;
using Twig.Infrastructure.Ado.Dtos;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Auth.InteractiveAuth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Infrastructure.Telemetry;

namespace Twig.Infrastructure.Serialization;

[JsonSerializable(typeof(TwigConfiguration))]
[JsonSerializable(typeof(TwigRepoConfig))]
[JsonSerializable(typeof(TwigUserConfig))]
[JsonSerializable(typeof(GlobalDisplayPreferences))]
[JsonSerializable(typeof(GitHubIssueGraphQlRequest))]
[JsonSerializable(typeof(GitHubAppActor))]
[JsonSerializable(typeof(GitHubAppCredential))]
[JsonSerializable(typeof(Twig.Infrastructure.GitHub.GitHubDeviceAuthorizationResponse))]
[JsonSerializable(typeof(Twig.Infrastructure.GitHub.GitHubDeviceTokenResponse))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(System.Collections.Immutable.ImmutableArray<Twig.Core.PlanningTeam>))]
[JsonSerializable(typeof(System.Collections.Immutable.ImmutableArray<Twig.Core.PlanningMetadata>))]
[JsonSerializable(typeof(GitHubIssueGraphQlResponse))]
[JsonSerializable(typeof(AdoConnectionDataResponse))]
[JsonSerializable(typeof(PatCredentialEntry))]
[JsonSerializable(typeof(ConnectionMigrationRecord))]
[JsonSerializable(typeof(MirrorAdmissionDocument))]
[JsonSerializable(typeof(ConnectionBindingTransitionRecord))]
[JsonSerializable(typeof(CoreWriteCorrelation))]
[JsonSerializable(typeof(ConnectionRemoteWriteResponse))]
[JsonSerializable(typeof(ConnectionRemoteWriteIntent))]
[JsonSerializable(typeof(ConnectionRemoteWriteObservation))]
[JsonSerializable(typeof(ConnectionRemoteWriteReceipt))]
[JsonSerializable(typeof(ConnectionRemoteWriteInspection))]
[JsonSerializable(typeof(ConnectionRemoteWriteInspection[]))]
[JsonSerializable(typeof(ConnectionDefaultTransitionRecord))]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(List<GitHubRelease>))]
[JsonSerializable(typeof(AppInsightsEnvelope))]
[JsonSerializable(typeof(MsalTokenCache))]
[JsonSerializable(typeof(TokenRefreshResponse))]
[JsonSerializable(typeof(JwtAccessTokenPayload))]
[JsonSerializable(typeof(TwigRefreshTokenStoreEntry))]
[JsonSerializable(typeof(AuthCodeTokenResponse))]
[JsonSerializable(typeof(DeviceCodeResponse))]
[JsonSerializable(typeof(Twig.Infrastructure.Persistence.LayoutMarkerDocument))]
[JsonSerializable(typeof(Twig.Infrastructure.Persistence.WorktreeFingerprintDocument))]
[JsonSerializable(typeof(Twig.Infrastructure.Persistence.AttachmentDocument))]
[JsonSerializable(typeof(Twig.Infrastructure.Persistence.ProviderTargets.ProviderTargetAttachmentDocument))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class TwigJsonContext : JsonSerializerContext { }

/// <summary>
/// AB#3296: parallel source-generated context with <c>WriteIndented = true</c>,
/// used only for emitting the committed <c>twig.json</c> manifest so it stays
/// human-reviewable. Compact JSON is fine for the gitignored user-prefs file
/// (matches today's behavior); the indented form is for the file humans read in PRs.
/// </summary>
[JsonSerializable(typeof(TwigRepoConfig))]
[JsonSerializable(typeof(DefaultsConfig))]
[JsonSerializable(typeof(SeedConfig))]
[JsonSerializable(typeof(GitConfig))]
[JsonSerializable(typeof(GitHubConfig))]
[JsonSerializable(typeof(WorkspaceConfig))]
[JsonSerializable(typeof(AreasConfig))]
[JsonSerializable(typeof(AreaPathEntry))]
[JsonSerializable(typeof(List<AreaPathEntry>))]
[JsonSerializable(typeof(SprintEntry))]
[JsonSerializable(typeof(List<SprintEntry>))]
[JsonSerializable(typeof(Twig.Infrastructure.Config.PolicyConfig))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Twig.Infrastructure.Config.SelectedProfileBinding))]
[JsonSerializable(typeof(Twig.Infrastructure.Config.ProfilePinConfig))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
internal sealed partial class TwigRepoJsonContext : JsonSerializerContext { }
