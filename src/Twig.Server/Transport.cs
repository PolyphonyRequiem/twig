using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Bench.Application;
using Bench.Infrastructure;
using Twig.Core;
using Twig.Infrastructure.Services;

namespace Twig.Server;

public sealed record EmptyBody;
public sealed record PairResult(Guid ViewId, string ContentCapability, bool NativeContentBlocked);
public sealed record CanvasRegistration(Guid ViewId, string PresentationCapability);
public sealed record Ack(bool Acknowledged = true);
public sealed record Failure(string Code, string Message);
public sealed record ServiceStatus(string Protocol, string DataMode, bool CanvasDefaultDeny,
    Guid ServerId = default, string? ServerVersion = null, string[]? Capabilities = null);
public sealed record LayoutBody(string Layout);
public sealed record ViewBody(long ExpectedRevision, Guid? BenchId, string Layout, Guid? GroupSource, string? GroupField);
public sealed record ViewPreferences(long Revision, Guid? BenchId, string Layout, Guid? GroupSource, string? GroupField);
public sealed record FieldValueDto(string Kind, JsonElement Value, string? DataType);
public sealed record FieldDto(string Reference, FieldValueDto Value);
public sealed record ChoiceDto(string Type, FieldValueDto Value, bool Conditional);
public sealed record GroupFieldDto(Guid SourceId, string Reference, ChoiceDto[] Choices, bool CompositeValues);
public sealed record ItemDto(string Id, string Locator, string Type, string? Title, string? StateField,
    bool BenchMember, bool HasDraft, bool RelationshipsComplete, FieldDto[] CachedFields, FieldDto[] ProposedFields, string? TitleField,
    string? WebUrl = null, bool IsLocal = false, bool IsExcluded = false)
{
    public bool NormalSeed { get; init; }
    public string? Pin { get; init; }
}
public sealed record TypeDto(string Reference, TypePresentation? Presentation, FieldDef[] Fields, State[] States)
{
    public string[] Gaps { get; init; } = [];
}
public sealed record EdgeDto(string From, string To, string Relation, bool Deleted, bool DraftIntent, string Origin)
{
    public long? LocalVersion { get; init; }
}
public sealed record UnresolvedReferenceDto(string Id, string? Provider, string? ResourceScope, string? NativeId,
    Guid? LocalId, Connection[] Connections, string Reason);
public sealed record SourceViewDto(Guid SourceId, string Label, string? ErrorCode, string[] Limitations,
    string? Mode, DateTimeOffset? CacheReadAt, DateTimeOffset? SchemaObservedAt, long? CacheVersion,
    ItemDto[] Items, GroupFieldDto[] Fields, bool TreeAvailable, string? Hierarchy, EdgeDto[] Edges, string[] TreeLimitations,
    string? Provider = null, TypeDto[]? Types = null)
{
    public int NormalDraftCount { get; init; }
    public string? SchemaFingerprint { get; init; }
    public Connection? Connection { get; init; }
    public UnresolvedReferenceDto[] UnresolvedReferences { get; init; } = [];
}
public sealed record BenchViewDto(Guid CatalogId, long CatalogRevision, Guid Id, string Name, SourceViewDto[] Sources);
public sealed record OwnerConnectBody(Guid? ViewId, Guid ProfileId);
public sealed record OwnerAgentBody(Guid ViewId);
public sealed record NativeBenchDto(Guid Id, string Name, long Revision, string Selectors);
public sealed record WorkspaceDto(string Root, string Provider, string DisplayName, bool LabelAvailable)
{
    public Connection? Connection { get; init; }
    public bool Available { get; init; } = true;
    public NativeConnectionAccount? Account { get; init; }
}
public sealed record NativePanelSummary(Guid StoreId, long Revision, NativeBenchDto[] Benches, WorkspaceDto[] Workspaces,
    string? ContextRoot, string? PopulationRoot, WorkspaceDto? ContextConnection = null, WorkspaceDto? PopulationConnection = null)
{
    public string? RepositoryRoot { get; init; }
    public UserRepositoryBindings? RepositoryBindings { get; init; }
    public CanvasRuntimeContext? RuntimeContext { get; init; }
}
public sealed record NativeSessionInfo(Guid StoreId, bool NativeContentBlocked, ViewPreferences Preferences);
public sealed record NativeBenchViewDto(Guid StoreId, long Revision, Guid Id, string Name, SourceViewDto[] Sources)
{
    public PlanStatusDto? Plan { get; init; }
}
public sealed record ContextBody(string Root);
public sealed record IdentityProfilesBody(Guid SourceId, string[] Identities);
public sealed record PopulationContextBody(string? Root);
public sealed record SelectionBody(Guid? ExpectedBenchId, Guid? BenchId);
public sealed record AgentPermissionsBody(Guid BenchId, bool Read, bool Draft, bool Membership, bool Rules = false);
public sealed record AgentPermissionsDto(bool Read, bool Draft, bool Membership, Guid? BenchId, DateTimeOffset? ExpiresAt, bool Rules = false);
public sealed record AgentSelectionBody(Guid BenchId, Guid? SourceId, string? ItemId);
public sealed record AgentSnapshotBody(Guid SnapshotId);
public sealed record AgentConnectionRequestBody(Guid SnapshotId, string Provider, string? Repository = null, string? Database = null);
public sealed record ConnectionTargetBody(Guid BenchId, long BenchRevision, string Provider, string? Repository = null, string? Database = null);
public sealed record ConnectionRequestBody(Guid RequestId);
public sealed record ConnectionApprovalBody(Guid RequestId, string? Account = null);
public sealed record ConnectionAdmissionBody(Guid RequestId, Guid OperationId, bool IncludeAll);
public sealed record ConnectionRequestDto(Guid RequestId, Guid BenchId, long BenchRevision, string BenchName,
    string Provider, string? Repository, string State, DateTimeOffset ExpiresAt, string? Database = null);
public sealed record BenchConnectionsDto(ConnectionRequestDto? Request, SetupOperationDto? Operation);
public sealed record ConnectionAddedDto(ConnectionRequestDto Request, string Root, bool Changed, bool RemoteWrites = false);
public sealed record AgentPageBody(Guid SnapshotId, int Offset = 0, int Limit = 50);
public sealed record AgentItemBody(Guid SnapshotId, Guid SourceId, string ItemId);
public sealed record McpRequestBody(string Jsonrpc, string Method, JsonElement? Id = null, JsonObject? Params = null);
public sealed record McpFieldDto(string Reference, string Name, bool Observed, FieldValueDto? Value, FieldValueDto? ProposedValue);
public sealed record McpTypePresentationDto(string Name, string? Color, string? IconId, string? IconSvg);
public sealed record McpDetailsDto(Guid SnapshotId, Guid SourceId, string ItemId, string Locator, string Type,
    string? Title, McpFieldDto? State, McpFieldDto? AssignedTo, string? Revision, bool IsLocal,
    bool HasDraft, Connection? Connection, string SourceLabel, string? Mode, DateTimeOffset? CacheReadAt,
    string[] Limitations, McpFieldDto[] Fields, UnresolvedReferenceDto[] UnresolvedReferences,
    string? FocusField, string? Note, bool ReadOnly = true)
{
    public McpTypePresentationDto? TypePresentation { get; init; }
    public State? StatePresentation { get; init; }
    public IdentityProfile[] AssignedProfiles { get; init; } = [];
    public string? WebUrl { get; init; }
}
public sealed record AgentPinBody(Guid SnapshotId, Guid SourceId, string Locator);
public sealed record AgentQueryRuleBody(Guid SnapshotId, Guid SourceId, int RuleIndex, string Selector);
public sealed record AgentRuleDto(int RuleIndex, Guid SourceId, string Kind, string Selector);
public sealed record AgentRulesDto(Guid SnapshotId, AgentRuleDto[] Rules, int Total, int? NextOffset, int UnadmittedRuleCount);
public sealed record AgentStageBody(Guid SnapshotId, Guid SourceId, string ItemId, FieldDto[] Fields);
public sealed record AgentConnectionDto(Guid SourceId, string Provider, string Label, bool LabelAvailable,
    string WorkspaceRoot, string PrincipalId, bool Viewing, bool Population, string State,
    DateTimeOffset? CacheReadAt, DateTimeOffset? SchemaObservedAt, string? ErrorCode, string[] Limitations);
public sealed record AgentContextDto(Guid SnapshotId, DateTimeOffset ExpiresAt, Guid BenchId, string Name,
    long BenchRevision, long CatalogueRevision, Guid? SelectedSourceId, string? SelectedItemId,
    AgentPermissionsDto Permissions, PlanStatusDto Plan, AgentConnectionDto[] Connections, string Notice)
{
    public CanvasRuntimeContext? RuntimeContext { get; init; }
    public string? RepositoryRoot { get; init; }
    public long? RepositoryRevision { get; init; }
}
public sealed record AgentConnectionsDto(Guid SnapshotId, AgentConnectionDto[] Connections);
public sealed record AgentItemSummaryDto(Guid SourceId, string ItemId, string Provider, string Locator,
    string Type, string? Title, FieldValueDto? State, FieldValueDto? AssignedTo, bool HasDraft, bool DirectPin);
public sealed record AgentBenchPageDto(Guid SnapshotId, AgentItemSummaryDto[] Items, int Total, int? NextOffset,
    Guid[] UnavailableSources);
public sealed record AgentItemDto(Guid SnapshotId, Guid SourceId, ItemDto Item, TypeDto? Type);
public sealed record AgentChangedDto(bool Changed, bool RemoteWrites, bool RefreshRequired, string Notice);
public sealed record AgentReviewDto(Guid RequestId, Guid SnapshotId, Guid BenchId, long BenchRevision,
    long PlanRevision, string State, string? Digest = null, string? Message = null);
public sealed record AgentReviewResultBody(Guid RequestId, string State, string? Digest, string? Message);
public sealed record AgentUiDto(AgentPermissionsDto Permissions, AgentReviewDto? Review);
public sealed record AgentDisplayBody(string Screen, string Surface, bool Loading, bool ConnectionSelected,
    bool BenchSelected, int VisibleItems, bool HasError, string? ErrorCode);
public sealed record AgentDisplayDto(AgentDisplayBody State, DateTimeOffset ReportedAt);
public sealed record AgentUiStateDto(Guid ViewId, bool SignedIn, bool ConnectionSelected, bool BenchSelected,
    bool AutomaticAccess, bool Read, bool Draft, bool Membership, string AccessState, string? ErrorCode,
    AgentDisplayDto? Display, DateTimeOffset ObservedAt);
public sealed record SaveNativeBenchBody(string Name, long ExpectedRevision, string Selectors);
public sealed record AddNativeBenchBody(string Selectors);
public sealed record DeleteNativeBenchBody(long ExpectedRevision);
public sealed record PickerPinDto(string ItemId, string Pin, string? Subtree);
public sealed record NativePickerDto(SourceViewDto Source, PickerPinDto[] Pins);
public sealed record NativePlanningDto(PlanningMetadata? Metadata, bool CanFilterOwnership);
public sealed record NativeCatalogueDto(PlanningSources? Sources, DateTimeOffset? ObservedAt);
public sealed record PlanningContextBody(string? Team = null);
public sealed record PlanningRuleBody(string? Area, bool IncludeChildren, string? Iteration, bool AssignedToMe, string? Team = null);
public sealed record PlanningRuleDto(string Selectors);
public sealed record PlanningBrowseBody(PlanningSourceKind Kind, string Id, string? Team);
public sealed record NativePlanningResultDto(NativePickerDto Picker, PlanningSource Definition, string? Team,
    string Selectors, DateTimeOffset MembershipObservedAt);
public sealed record EditorOpenBody(Guid SourceId, string ItemId, Guid BenchId, bool PlanMode = false);
public sealed record EditorChangeBody(Guid Ticket, FieldDto[] Fields, EditorInputReference? Inputs = null);
public sealed record EditorChoiceBody(Guid Ticket, string Field, string Query);
public sealed record EditorCloseBody(Guid Ticket);
public sealed record RelationshipOpenBody(Guid Ticket, string EndpointId);
public sealed record RelationshipChangeBody(Guid Ticket, FieldDto[] Fields, RelationshipIntentDto[] Intent, EditorInputReference? Inputs = null);
public sealed record RelationshipIntentDto(string EndpointId, string Relation, bool Outgoing, bool Remove);
public sealed record RelationshipKindDto(string Reference, string Shape, bool Directed);
public sealed record RelationshipEndpointDto(string ItemId, string Locator, string Type, string Revision, FieldDto[] Fields);
public sealed record RelationshipFormDto(EditorFormDto Fields, RelationshipEndpointDto Target, RelationshipEndpointDto Endpoint,
    RelationshipKindDto[] Relations, RelationshipIntentDto[] Choices, RelationshipIntentDto[] Intent, EdgeDto[] Incident)
{
    public bool LocalOnly { get; init; }
    public string? Verification { get; init; }
}
public sealed record LocalRelationshipReadBody(Guid SourceId, Guid BenchId, string ItemId);
public sealed record LocalRelationshipReviewBody(Guid Ticket, bool Adding, EditorInputReference? Inputs = null);
public sealed record LocalRelationshipReferenceBody(Guid Ticket, Guid CaptureId, string Digest, int Operation, Guid Attempt);
public sealed record LocalRelationshipEffectDto(string Kind, RelationshipEndpointDto Target, RelationshipEndpointDto Endpoint,
    string Relation, long? ExpectedLocalVersion, long ExpectedStoreVersion, string SchemaFingerprint);
public sealed record LocalRelationshipReceiptDto(string Digest, int Operation, Guid Attempt, string Status, string[] Evidence, Failure? Error);
public sealed record LocalRelationshipOutcomeDto(string Digest, int Operation, Guid Attempt, string Status, bool Settled,
    LocalRelationshipReceiptDto[] Receipts, Failure? Error);
public sealed record LocalRelationshipReviewDto(Guid Ticket, Guid CaptureId, string Digest, string CanonicalProposal,
    LocalRelationshipEffectDto Effect, EditorBindingDto Binding, string Authority, long EditorEpoch,
    bool CanApply, bool CanContinue, LocalRelationshipOutcomeDto Outcome, string Verification);
public sealed record EditorFieldDto(string Reference, string Kind, string Name, bool ReadOnly, bool Required,
    bool Constrained, FieldValueDto[] Allowed, FieldValueDto Value);
public sealed record EditorFormDto(Guid Ticket, string Authority, string Type, string StateField, EditorFieldDto[] Fields,
    string[] States, string[] Messages)
{
    public EditorInputRecoveryDto? Inputs { get; init; }
    public string InputBaseline { get; init; } = "";
    public EditorBindingDto? Binding { get; init; }
    public string? ItemId { get; init; }
    public string? Notice { get; init; }
    public bool RelationshipsReadOnly { get; init; }
    public RelationshipIntentDto[] RelationshipIntent { get; init; } = [];
}
public sealed record EditorBindingDto(bool PlanMode, Guid PlanId, long PlanRevision, Guid ScopeId, Guid StoreInstance);
public sealed record PlanBenchBody(Guid BenchId);
public sealed record PlanModeBody(Guid BenchId, Guid StoreId, long BenchRevision, long PlanRevision, bool Enabled);
public sealed record PlanSeedBody(Guid BenchId, Guid SourceId, string Type);
public sealed record PlanScopeDto(Guid ScopeId, Guid? SourceId, string Label, string State);
public sealed record PlanStatusDto(Guid StoreId, Guid BenchId, long BenchRevision, Guid PlanId, long Revision,
    bool Enabled, PlanScopeDto[] Sources, string? LastReviewDigest);
public sealed record PlanEffectDto(Guid SourceId, string Label, string ItemId, string Locator, bool IsLocal,
    string? Type, FieldDto[] Fields, string[] Relationships);
public sealed record PlanReviewDto(Guid Ticket, Guid BenchId, string Digest, PlanEffectDto[] Effects, string[] CoreDigests,
    string Notice, PlanOutcomeDto? Outcome, bool CanApply);
public sealed record PlanApplyBody(Guid BenchId, Guid Ticket, string Digest, bool DeliveryOnly = false);
public sealed record PlanObserveBody(Guid BenchId, string Digest);
public sealed record PlanOperationDto(Guid OperationId, string Status, PlanReviewDto? Review, PlanOutcomeDto? Outcome,
    Failure? Error);
public sealed record PlanOutcomeDto(string Status, int VerifiedScopes, int DeliveredScopes, int RemainingScopes,
    int VerifiedOperations, int RemainingOperations, string? Message);
public sealed record EditorChoiceDto(FieldValueDto Value, string Label);
public sealed record EditorSavedDto(bool Verified, bool DraftRetained, string? Message)
{
    public SeedPublicationDto? SeedPublication { get; init; }
}
public sealed record NormalSeedCreateBody(Guid SourceId, Guid BenchId, string Type);
public sealed record SeedReferenceBody(Guid SourceId, Guid BenchId, Guid CaptureId);
public sealed record SeedDeliveryBody(Guid SourceId, Guid BenchId, Guid CaptureId, string Digest);
public sealed record SeedTypeDto(string Reference, string Name);
public sealed record SeedPublicationDto(Guid CaptureId, string ItemId, string LocalId, string Digest, string Status, Failure? Error);
public sealed record OwnedSeedDto(string ItemId, string LocalId, Guid CaptureId, bool Ready, bool HasDraft, SeedPublicationDto? Publication);
public sealed record NormalSeedViewDto(SourceViewDto Source, SeedTypeDto[] Types, OwnedSeedDto[] Seeds, Guid StoreInstance);
public sealed record SetupEnrollmentBody(string Alias, string? Tenant = null, long Epoch = 0);
public sealed record SetupIdentityBody(NativeAadEnrollment Identity, long Epoch = 0);
public sealed record SetupDiscoveryBody(Guid EnrollmentOperationId, string Kind, string? Organization = null,
    string? Project = null, int Limit = 50, string? Continuation = null, long Epoch = 0);
public sealed record SetupBindingBody(string Organization, string Project, string IdentityAlias, long Epoch = 0);
public sealed record SetupAdoBody(string WorkspaceRoot, string Organization, string Project, string BindingId,
    string? Team = null, string? GitProject = null, long Epoch = 0);
public sealed record SetupManagedAdoBody(string Organization, string Project, string BindingId,
    string? Team = null, string? GitProject = null, long Epoch = 0);
public sealed record SetupGitHubBody(string WorkspaceRoot, string Host, string Repository, string Account, long Epoch = 0);
public sealed record SetupManagedGitHubBody(string Repository, string Account, long Epoch = 0);
public sealed record SetupTwigBody(string WorkspaceRoot, string Database, long Epoch = 0);
public sealed record SetupCancelBody(long Epoch);
public sealed record SetupEffectDto(string Kind, string State);
public sealed record SetupFailureDto(string Phase, bool Cancelled, SetupEffectDto[] Effects);
public sealed record SetupSourceDto(string WorkspaceRoot, NativeSourceStamp Source, Connection SchemaConnection, DateTimeOffset? SchemaObservedAt);
public sealed record SetupOperationDto(Guid OperationId, string Status, string Phase, string? AuthorizationUrl = null,
    NativeAadEnrollment? Enrollment = null, NativeAdoBinding? Binding = null, SetupSourceDto? Source = null,
    Failure? Error = null, SetupFailureDto? NativeFailure = null, Failure? DiagnosticsError = null, bool Pending = false, long Epoch = 0,
    Guid? ConnectionRequestId = null, NativeAdoDiscoveryRequest? DiscoveryRequest = null,
    NativeAdoDiscoveryPage? Discovery = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, MaxDepth = 32,
    GenerationMode = JsonSourceGenerationMode.Metadata, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(EmptyBody))]
[JsonSerializable(typeof(AgentPermissionsBody))]
[JsonSerializable(typeof(AgentPermissionsDto))]
[JsonSerializable(typeof(AgentSelectionBody))]
[JsonSerializable(typeof(AgentSnapshotBody))]
[JsonSerializable(typeof(AgentConnectionRequestBody))]
[JsonSerializable(typeof(ConnectionTargetBody))]
[JsonSerializable(typeof(ConnectionRequestBody))]
[JsonSerializable(typeof(ConnectionApprovalBody))]
[JsonSerializable(typeof(ConnectionAdmissionBody))]
[JsonSerializable(typeof(ConnectionRequestDto))]
[JsonSerializable(typeof(BenchConnectionsDto))]
[JsonSerializable(typeof(ConnectionAddedDto))]
[JsonSerializable(typeof(AgentPageBody))]
[JsonSerializable(typeof(AgentItemBody))]
[JsonSerializable(typeof(McpRequestBody))]
[JsonSerializable(typeof(McpDetailsDto))]
[JsonSerializable(typeof(AgentPinBody))]
[JsonSerializable(typeof(AgentQueryRuleBody))]
[JsonSerializable(typeof(AgentRulesDto))]
[JsonSerializable(typeof(AgentStageBody))]
[JsonSerializable(typeof(AgentContextDto))]
[JsonSerializable(typeof(AgentConnectionsDto))]
[JsonSerializable(typeof(AgentBenchPageDto))]
[JsonSerializable(typeof(AgentItemDto))]
[JsonSerializable(typeof(AgentChangedDto))]
[JsonSerializable(typeof(AgentReviewDto))]
[JsonSerializable(typeof(AgentReviewResultBody))]
[JsonSerializable(typeof(AgentUiDto))]
[JsonSerializable(typeof(AgentDisplayBody))]
[JsonSerializable(typeof(AgentUiStateDto))]
[JsonSerializable(typeof(SetupEnrollmentBody))]
[JsonSerializable(typeof(SetupIdentityBody))]
[JsonSerializable(typeof(NativeAadEnrollment[]))]
[JsonSerializable(typeof(SetupDiscoveryBody))]
[JsonSerializable(typeof(SetupBindingBody))]
[JsonSerializable(typeof(SetupAdoBody))]
[JsonSerializable(typeof(SetupManagedAdoBody))]
[JsonSerializable(typeof(SetupGitHubBody))]
[JsonSerializable(typeof(SetupManagedGitHubBody))]
[JsonSerializable(typeof(NativeGitHubAccount[]))]
[JsonSerializable(typeof(SetupTwigBody))]
[JsonSerializable(typeof(SetupCancelBody))]
[JsonSerializable(typeof(SetupOperationDto))]
[JsonSerializable(typeof(EditorOpenBody))]
[JsonSerializable(typeof(EditorChangeBody))]
[JsonSerializable(typeof(EditorChoiceBody))]
[JsonSerializable(typeof(EditorCloseBody))]
[JsonSerializable(typeof(RelationshipOpenBody))]
[JsonSerializable(typeof(RelationshipChangeBody))]
[JsonSerializable(typeof(RelationshipFormDto))]
[JsonSerializable(typeof(LocalRelationshipReadBody))]
[JsonSerializable(typeof(LocalRelationshipReviewBody))]
[JsonSerializable(typeof(LocalRelationshipReferenceBody))]
[JsonSerializable(typeof(LocalRelationshipReviewDto))]
[JsonSerializable(typeof(LocalRelationshipReviewDto[]))]
[JsonSerializable(typeof(EditorFormDto))]
[JsonSerializable(typeof(EditorBindingDto))]
[JsonSerializable(typeof(PlanBenchBody))]
[JsonSerializable(typeof(PlanModeBody))]
[JsonSerializable(typeof(PlanSeedBody))]
[JsonSerializable(typeof(PlanStatusDto))]
[JsonSerializable(typeof(PlanReviewDto))]
[JsonSerializable(typeof(PlanApplyBody))]
[JsonSerializable(typeof(PlanObserveBody))]
[JsonSerializable(typeof(PlanOperationDto))]
[JsonSerializable(typeof(PlanOutcomeDto))]
[JsonSerializable(typeof(EditorChoiceDto[]))]
[JsonSerializable(typeof(EditorSavedDto))]
[JsonSerializable(typeof(NormalSeedCreateBody))]
[JsonSerializable(typeof(SeedReferenceBody))]
[JsonSerializable(typeof(SeedDeliveryBody))]
[JsonSerializable(typeof(SeedPublicationDto))]
[JsonSerializable(typeof(NormalSeedViewDto))]
[JsonSerializable(typeof(OwnedSeedDto[]))]
[JsonSerializable(typeof(SeedTypeDto[]))]
[JsonSerializable(typeof(PairResult))]
[JsonSerializable(typeof(CanvasRegistration))]
[JsonSerializable(typeof(Ack))]
[JsonSerializable(typeof(Failure))]
[JsonSerializable(typeof(ServiceStatus))]
[JsonSerializable(typeof(LayoutBody))]
[JsonSerializable(typeof(ViewBody))]
[JsonSerializable(typeof(ViewPreferences))]
[JsonSerializable(typeof(BenchViewDto))]
[JsonSerializable(typeof(OwnerConnectBody))]
[JsonSerializable(typeof(OwnerAgentBody))]
[JsonSerializable(typeof(NativePanelSummary))]
[JsonSerializable(typeof(NativeSessionInfo))]
[JsonSerializable(typeof(NativeBenchViewDto))]
[JsonSerializable(typeof(NativeBenchDto))]
[JsonSerializable(typeof(ContextBody))]
[JsonSerializable(typeof(IdentityProfilesBody))]
[JsonSerializable(typeof(IdentityProfile[]))]
[JsonSerializable(typeof(PopulationContextBody))]
[JsonSerializable(typeof(SelectionBody))]
[JsonSerializable(typeof(SaveNativeBenchBody))]
[JsonSerializable(typeof(AddNativeBenchBody))]
[JsonSerializable(typeof(DeleteNativeBenchBody))]
[JsonSerializable(typeof(NativePickerDto))]
[JsonSerializable(typeof(NativePlanningDto))]
[JsonSerializable(typeof(NativeCatalogueDto))]
[JsonSerializable(typeof(PlanningContextBody))]
[JsonSerializable(typeof(PlanningTeam[]))]
[JsonSerializable(typeof(PlanningRuleBody))]
[JsonSerializable(typeof(PlanningRuleDto))]
[JsonSerializable(typeof(PlanningSources))]
[JsonSerializable(typeof(PlanningBrowseBody))]
[JsonSerializable(typeof(NativePlanningResultDto))]
internal sealed partial class TransportJson : JsonSerializerContext;
