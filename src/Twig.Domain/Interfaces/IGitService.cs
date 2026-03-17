namespace Twig.Domain.Interfaces;

/// <summary>
/// Abstracts local git operations. Implemented in Infrastructure (git CLI).
/// Registered as a singleton in DI. If git is not installed,
/// methods throw <c>GitOperationException</c> with a clear diagnostic message.
/// Commands that accept <c>IGitService?</c> via <c>GetService</c> should
/// handle <c>GitOperationException</c> gracefully when git is unavailable.
/// </summary>
public interface IGitService
{
    Task<string> GetCurrentBranchAsync(CancellationToken ct = default);
    Task<string> GetRepositoryRootAsync(CancellationToken ct = default);
    Task<bool> IsInsideWorkTreeAsync(CancellationToken ct = default);
    Task CreateBranchAsync(string branchName, CancellationToken ct = default);
    Task CheckoutAsync(string branchName, CancellationToken ct = default);
    Task<string> CommitAsync(string message, bool allowEmpty = false, CancellationToken ct = default);
    Task<string> GetRemoteUrlAsync(string remote = "origin", CancellationToken ct = default);
    Task<string?> GetConfigValueAsync(string key, CancellationToken ct = default);
    Task<string> GetHeadCommitHashAsync(CancellationToken ct = default);
    Task StashAsync(string? message = null, CancellationToken ct = default);
    Task StashPopAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetLogAsync(int count = 20, string? format = null, CancellationToken ct = default);
    Task<string?> GetWorktreeRootAsync(CancellationToken ct = default);
    Task<bool> HasUncommittedChangesAsync(CancellationToken ct = default);
    Task<bool> BranchExistsAsync(string branchName, CancellationToken ct = default);
    Task DeleteBranchAsync(string branchName, CancellationToken ct = default);
    Task<bool> IsAheadOfAsync(string targetBranch, CancellationToken ct = default);
    Task<bool> IsDetachedHeadAsync(CancellationToken ct = default);
}
