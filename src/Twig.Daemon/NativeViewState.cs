using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Bench.Sessions.Tests")]

namespace Bench.Server;

public sealed record NativeViewChoices(string? ViewingRoot, string? AddRoot, string[] DisplayRoots,
    string? TeamId, string Mode, string? QueryId, string? BacklogId, Guid? BenchId, string Layout,
    Guid? GroupSource, string? GroupField, string? RepositoryRoot = null)
{
    public static NativeViewChoices Empty => new(null, null, [], null, "items", null, null, null, "list", null, null);
}

public sealed record NativeViewStateBody(long ExpectedRevision, NativeViewChoices Choices);
public sealed record OwnerProfileBody(Guid? StoreId, Guid? ProfileId, Guid? ViewId = null,
    bool RecoverMissingDefault = false);
public sealed record OwnerProfileInfo(Guid StoreId, Guid ProfileId);
public sealed record OwnerProfileStoreInfo(Guid StoreId);
public sealed record OwnerRenewBody(Guid? ViewId, Guid ProfileId, Guid? StoreId = null, bool InitialView = false);
// Restoration metadata is not cached content authority; every native read still requires its own admission.
public sealed record NativeSourceRecovery(string Root, string Status, string? ErrorCode);
// CurrentBenchId belongs to this view; source admission never selects a Bench.
public sealed record NativeViewStateInfo(Guid StoreId, Guid ProfileId, long Revision, NativeViewChoices Choices,
    NativeSourceRecovery[] Sources, Guid? CurrentBenchId, DateTimeOffset ExpiresAt, DateTimeOffset RenewAfter)
{
    public CanvasRuntimeContext? RuntimeContext { get; init; }
}
public sealed record NativeOwnerSession(Guid ViewId, string ContentCapability, bool Recovered, NativeViewStateInfo ViewState)
{
    public NativeInitialView? InitialView { get; init; }
}
public sealed record NativeInitialView(NativePanelSummary Summary, NativeSessionInfo Session, NativeBenchViewDto? View);
public sealed record NativeWorkspaceDto(NativeViewStateInfo ViewState, NativePanelSummary Summary,
    NativeSessionInfo Session, NativeBenchViewDto? View, int ReconcileAfterSeconds = 30)
{
    public Guid? PlanOperationId { get; init; }
    public Failure? SelectionError { get; init; }
}

internal sealed record NativeSourceReference(string Root, string Binding);
internal sealed record NativeViewDocument(int Version, Guid StoreId, Guid ProfileId, long Revision, Guid Generation,
    bool SignedOut, NativeViewChoices Choices, NativeSourceReference[] Sources);

[JsonSerializable(typeof(NativeViewChoices))]
[JsonSerializable(typeof(NativeViewStateBody))]
[JsonSerializable(typeof(OwnerRenewBody))]
[JsonSerializable(typeof(OwnerProfileBody))]
[JsonSerializable(typeof(OwnerProfileInfo))]
[JsonSerializable(typeof(OwnerProfileStoreInfo))]
[JsonSerializable(typeof(NativeViewStateInfo))]
[JsonSerializable(typeof(NativeOwnerSession))]
[JsonSerializable(typeof(NativeInitialView))]
[JsonSerializable(typeof(NativeWorkspaceDto))]
[JsonSerializable(typeof(NativeViewDocument))]
internal sealed partial class TransportJson;
