using System.Text.Json.Serialization;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Dtos;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;

namespace Twig.Infrastructure.Serialization;

/// <summary>
/// Source-generated JSON serialization context for all Twig DTOs.
/// Enables AOT-compatible serialization with no runtime reflection
/// (<c>JsonSerializerIsReflectionEnabledByDefault=false</c> in Twig.csproj).
/// Add <c>[JsonSerializable]</c> attributes here for every DTO type as they are introduced.
/// </summary>
[JsonSerializable(typeof(TwigConfiguration))]
[JsonSerializable(typeof(AuthConfig))]
[JsonSerializable(typeof(DefaultsConfig))]
[JsonSerializable(typeof(SeedConfig))]
[JsonSerializable(typeof(DisplayConfig))]
[JsonSerializable(typeof(DisplayColumnsConfig))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
// ADO REST DTOs (EPIC-006)
[JsonSerializable(typeof(AdoWorkItemResponse))]
[JsonSerializable(typeof(AdoBatchWorkItemResponse))]
[JsonSerializable(typeof(AdoWiqlResponse))]
[JsonSerializable(typeof(AdoWiqlRequest))]
[JsonSerializable(typeof(AdoCommentRequest))]
[JsonSerializable(typeof(AdoPatchOperation))]
[JsonSerializable(typeof(List<AdoPatchOperation>))]
[JsonSerializable(typeof(AdoIterationListResponse))]
[JsonSerializable(typeof(AdoIterationResponse))]
[JsonSerializable(typeof(AdoWorkItemTypeListResponse))]
[JsonSerializable(typeof(AdoWorkItemTypeResponse))]
[JsonSerializable(typeof(AdoWorkItemTypeIconResponse))]
[JsonSerializable(typeof(AdoWorkItemStateColor))]
[JsonSerializable(typeof(AdoErrorResponse))]
[JsonSerializable(typeof(AdoTeamFieldValuesResponse))]
[JsonSerializable(typeof(AdoConnectionDataResponse))]
[JsonSerializable(typeof(AdoProfileResponse))]
[JsonSerializable(typeof(TypeAppearanceConfig))]
[JsonSerializable(typeof(List<TypeAppearanceConfig>))]
[JsonSerializable(typeof(AreaPathEntry))]
[JsonSerializable(typeof(List<AreaPathEntry>))]
[JsonSerializable(typeof(UserConfig))]
[JsonSerializable(typeof(GitConfig))]
[JsonSerializable(typeof(HooksConfig))]
[JsonSerializable(typeof(FlowConfig))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
// Dynamic process configuration DTOs (EPIC-2/4)
[JsonSerializable(typeof(AdoProcessConfigurationResponse))]
[JsonSerializable(typeof(AdoCategoryConfiguration))]
[JsonSerializable(typeof(AdoWorkItemTypeRef))]
[JsonSerializable(typeof(List<AdoCategoryConfiguration>))]
[JsonSerializable(typeof(List<string>))]
// ProcessConfigurationData caching (EPIC-0)
[JsonSerializable(typeof(ProcessConfigurationData))]
[JsonSerializable(typeof(BacklogLevelConfiguration))]
[JsonSerializable(typeof(List<BacklogLevelConfiguration>))]
// PR value objects (EPIC-005)
[JsonSerializable(typeof(PullRequestInfo))]
[JsonSerializable(typeof(PullRequestCreate))]
// ADO Git REST DTOs (EPIC-001)
[JsonSerializable(typeof(AdoPullRequestResponse))]
[JsonSerializable(typeof(AdoPullRequestListResponse))]
[JsonSerializable(typeof(AdoRepositoryResponse))]
[JsonSerializable(typeof(AdoProjectResponse))]
[JsonSerializable(typeof(AdoCreatePullRequestRequest))]
[JsonSerializable(typeof(AdoWorkItemRef))]
[JsonSerializable(typeof(List<AdoWorkItemRef>))]
[JsonSerializable(typeof(AdoArtifactLinkRelation))]
[JsonSerializable(typeof(AdoArtifactLinkAttributes))]
// State category types (EPIC-2)
[JsonSerializable(typeof(StateEntry))]
[JsonSerializable(typeof(List<StateEntry>))]
[JsonSerializable(typeof(StateCategory))]
// Field definition DTOs (EPIC-004)
[JsonSerializable(typeof(AdoFieldListResponse))]
[JsonSerializable(typeof(AdoFieldResponse))]
// GitHub Release DTOs (EPIC-005 — self-update)
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(List<GitHubRelease>))]
// Seed publish rules (Epic 1 — publish rules configuration)
[JsonSerializable(typeof(SeedPublishRules))]
// Global process profile metadata
[JsonSerializable(typeof(ProfileMetadata))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class TwigJsonContext : JsonSerializerContext { }
