namespace Twig.Domain.Interfaces;

/// <summary>
/// Generates unique negative sentinel IDs for seed work items.
/// Thread-safe implementations must ensure <see cref="Next"/> is atomic.
/// </summary>
public interface ISeedIdCounter
{
    /// <summary>
    /// Returns the next negative seed ID (atomically decrements the counter).
    /// </summary>
    int Next();

    /// <summary>
    /// Resets the counter so that subsequent <see cref="Next"/> calls produce IDs
    /// below <paramref name="minExistingId"/>. Thread-safe via <see cref="System.Threading.Interlocked.Exchange"/>.
    /// </summary>
    void Initialize(int minExistingId);
}
