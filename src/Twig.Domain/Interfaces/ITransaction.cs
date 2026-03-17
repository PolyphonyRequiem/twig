namespace Twig.Domain.Interfaces;

/// <summary>
/// Opaque transaction token for the unit-of-work pattern.
/// This interface has no methods by design — it is used purely as a handle
/// whose lifetime is managed by <see cref="IUnitOfWork"/>. Callers pass the
/// token back to <see cref="IUnitOfWork.CommitAsync"/> or
/// <see cref="IUnitOfWork.RollbackAsync"/>; they never interact with it directly.
/// </summary>
public interface ITransaction : IAsyncDisposable
{
}
