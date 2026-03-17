namespace Twig.Domain.Interfaces;

/// <summary>
/// Unit of work pattern for transactional consistency across repository operations.
/// Returns an <see cref="ITransaction"/> token that acts as an opaque handle;
/// its lifetime is managed by <see cref="CommitAsync"/> / <see cref="RollbackAsync"/>.
/// See docs/projects/twig-epics-1-3.design.md § IUnitOfWork for the design rationale.
/// </summary>
public interface IUnitOfWork
{
    Task<ITransaction> BeginAsync(CancellationToken ct = default);
    Task CommitAsync(ITransaction tx, CancellationToken ct = default);
    Task RollbackAsync(ITransaction tx, CancellationToken ct = default);
}
