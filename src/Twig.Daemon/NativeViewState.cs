using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Bench.Sessions.Tests")]

namespace Bench.Server;

public sealed record NativeViewChoices(string? ViewingRoot, string? AddRoot, string[] DisplayRoots,
    string? TeamId, string Mode, string? QueryId, string? BacklogId, Guid? BenchId, string Layout,
    Guid? GroupSource, string? GroupField)
{
    public static NativeViewChoices Empty => new(null, null, [], null, "items", null, null, null, "list", null, null);
}

public sealed record NativeViewStateBody(long ExpectedRevision, NativeViewChoices Choices);
public sealed record OwnerRenewBody(Guid? ViewId, Guid ProfileId, Guid? StoreId = null, bool InitialView = false);
// Restoration metadata is not cached content authority; every native read still requires its own admission.
public sealed record NativeSourceRecovery(string Root, string Status, string? ErrorCode);
// CurrentBenchId is observed on restoration only. The normal session endpoint reads the current CLI selection.
public sealed record NativeViewStateInfo(Guid StoreId, Guid ProfileId, long Revision, NativeViewChoices Choices,
    NativeSourceRecovery[] Sources, Guid? CurrentBenchId, DateTimeOffset ExpiresAt, DateTimeOffset RenewAfter);
public sealed record NativeOwnerSession(Guid ViewId, string ContentCapability, bool Recovered, NativeViewStateInfo ViewState)
{
    public NativeInitialView? InitialView { get; init; }
}
public sealed record NativeInitialView(NativePanelSummary Summary, NativeSessionInfo Session, NativeBenchViewDto? View);

internal sealed record NativeSourceReference(string Root, string Binding)
{
    // Older profiles contain this field; it no longer controls display behavior.
    public bool AllowCachedDisplay { get; init; }
}
internal sealed record NativeViewDocument(int Version, Guid StoreId, Guid ProfileId, long Revision, Guid Generation,
    bool SignedOut, NativeViewChoices Choices, NativeSourceReference[] Sources);

[JsonSerializable(typeof(NativeViewChoices))]
[JsonSerializable(typeof(NativeViewStateBody))]
[JsonSerializable(typeof(OwnerRenewBody))]
[JsonSerializable(typeof(NativeViewStateInfo))]
[JsonSerializable(typeof(NativeOwnerSession))]
[JsonSerializable(typeof(NativeInitialView))]
[JsonSerializable(typeof(NativeViewDocument))]
internal sealed partial class TransportJson;
