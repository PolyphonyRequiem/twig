using Twig.Domain.Interfaces;

namespace Twig.Domain.Services;

/// <summary>
/// Thread-safe seed ID counter using <see cref="Interlocked"/> operations.
/// Each call to <see cref="Next"/> produces a unique negative sentinel ID.
/// </summary>
public sealed class SeedIdCounter : ISeedIdCounter
{
    private int _counter;

    /// <inheritdoc />
    public int Next() => Interlocked.Decrement(ref _counter);

    /// <inheritdoc />
    public void Initialize(int minExistingId) =>
        Interlocked.Exchange(ref _counter, Math.Min(minExistingId, 0));
}
