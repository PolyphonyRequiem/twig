namespace Twig.Core;

/// <summary>The negotiated owner of one original update. Open commits dispatch permission;
/// The provider's native admission ledger owns the response independently of local reconciliation.</summary>
internal interface IOriginalUpdateBarrier
{
    Task OpenAsync(Plan plan, Guid attempt, OriginalDispatch request, CancellationToken ct);
    Task WitnessCreateAsync(Plan plan, Guid attempt, OriginalResponse response, CancellationToken ct);
}

internal sealed record OriginalDispatch(string Method, string Target, string PayloadHash, string Actor, string Context,
    string? ResponseHash = null);
internal sealed record OriginalResponse(int Status, string Body)
{
    internal string Fingerprint() => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(FormattableString.Invariant($"{Status}\n{Body}"))));
}

internal interface IOriginalResponseSource
{
    Task<Result<OriginalResponse>> ObserveOriginalAsync(Plan plan, Guid attempt, OriginalDispatch? request, CancellationToken ct);
}

internal interface IOriginalUpdateProvider
{
    Task<Result<bool>> CheckClosedAsync(Plan plan, Guid attempt, CancellationToken ct);
    Task<Result<Receipt>> ApplyOriginalAsync(Plan plan, int operation, Guid attempt, IOriginalUpdateBarrier? barrier, CancellationToken ct);
    Task<Result<Receipt>> InspectOriginalAsync(Plan plan, Guid attempt, OriginalDispatch? request, CancellationToken ct);
    Task<Result<Receipt>> VerifyOriginalAsync(Plan plan, Receipt acknowledgement, CancellationToken ct);
}
