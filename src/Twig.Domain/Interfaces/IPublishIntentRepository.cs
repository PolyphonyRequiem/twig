using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// The durable intent ledger for the seed publish path (wayfinder 0015, from 0001 §4).
/// <para>
/// The publish path creates remote state at step 7 and records it at step 10. A crash in that
/// window orphans a real ADO work item with no local trace, and every retry creates another
/// duplicate (PolyphonyRequiem/twig#270). #270 fixed the FK ordering *inside* step 10; the
/// window itself stayed open. This ledger closes it: write the intent, make the call, record
/// the outcome.
/// </para>
/// <para>
/// It is durable by 0005's test — ADO cannot rebuild it, because it is precisely the record of
/// a call whose outcome ADO may or may not hold. It therefore lives in <c>pending.db</c> (0013)
/// and is keyed on <see cref="StagedIdentity"/> (0014).
/// </para>
/// </summary>
public interface IPublishIntentRepository
{
    /// <summary>
    /// Durably records the intent to create an ADO item for <paramref name="identity"/>, and
    /// returns it. Re-recording an identity whose intent is still open returns the EXISTING
    /// record rather than replacing it, so a retry keeps the original <c>RecordedAt</c> — which
    /// is the lower bound the recovery query fences on. Re-stamping it would move the fence past
    /// the create it is meant to find.
    /// </summary>
    Task<PublishIntent> RecordIntentAsync(
        StagedIdentity identity,
        string title,
        string typeName,
        CancellationToken ct = default);

    /// <summary>Records the outcome of a create that is known to have landed as <paramref name="publishedId"/>.</summary>
    Task CompleteIntentAsync(StagedIdentity identity, int publishedId, CancellationToken ct = default);

    /// <summary>The intent for an identity, or null when none was ever recorded.</summary>
    Task<PublishIntent?> GetIntentAsync(StagedIdentity identity, CancellationToken ct = default);

    /// <summary>
    /// Every intent whose outcome was never recorded. These are the reconcilable ones: for each,
    /// twig must ask ADO whether the create landed before retrying it.
    /// </summary>
    Task<IReadOnlyList<PublishIntent>> GetOpenIntentsAsync(CancellationToken ct = default);
}
