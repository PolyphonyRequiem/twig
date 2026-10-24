using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// The provider mutation port for a proposal's revision-bound relationship and delete
/// operations (Feature #1178 AC (b)). Proposal apply dispatches add-link, remove-link and
/// delete through this port rather than a concrete provider client.
/// </summary>
/// <remarks>
/// <para>
/// Every member is bound to an opaque <see cref="ProviderRevisionPrecondition"/>. An
/// implementation must refuse a missing precondition, one minted by another provider, or a
/// non-canonical token with <see cref="ProviderRevisionPreconditionRefusedException"/>
/// <b>before any transport call</b>.
/// </para>
/// <para>
/// Relations use the proposal's closed neutral vocabulary (<see cref="ProposalRelation"/>);
/// only the adapter maps them to native link types. #1170's canonical relationship identity
/// replaces that vocabulary behind this port.
/// </para>
/// <para>
/// Item ids are still the local integer id. The neutral store's item identity (#1168)
/// replaces them before any non-ADO writer is registered (GitHub authoring, #1183). Until then
/// only ADO implements this port, and composition refuses every other provider with
/// <c>provider-unavailable</c> before any provider client is constructed.
/// </para>
/// </remarks>
public interface IProviderRevisionBoundWriter
{
    /// <summary>
    /// Adds <paramref name="relation"/> from <paramref name="sourceId"/> to
    /// <paramref name="targetId"/> if the source is still at <paramref name="expected"/>,
    /// returning the provider's revision after the write.
    /// </summary>
    /// <exception cref="ProviderRevisionPreconditionRefusedException">
    /// The precondition is missing, foreign or non-canonical. Raised before any transport call.
    /// </exception>
    Task<ProviderRevisionToken> AddRelationshipAsync(
        int sourceId, ProposalRelation relation, int targetId, ProviderRevisionPrecondition expected, CancellationToken ct = default);

    /// <summary>
    /// Removes <paramref name="relation"/> from <paramref name="sourceId"/> to
    /// <paramref name="targetId"/> if the source is still at <paramref name="expected"/>,
    /// returning the provider's revision after the write.
    /// </summary>
    /// <exception cref="ProviderRevisionPreconditionRefusedException">
    /// The precondition is missing, foreign or non-canonical. Raised before any transport call.
    /// </exception>
    Task<ProviderRevisionToken> RemoveRelationshipAsync(
        int sourceId, ProposalRelation relation, int targetId, ProviderRevisionPrecondition expected, CancellationToken ct = default);

    /// <summary>Deletes <paramref name="id"/> if it is still at <paramref name="expected"/>.</summary>
    /// <exception cref="ProviderRevisionPreconditionRefusedException">
    /// The precondition is missing, foreign or non-canonical. Raised before any transport call.
    /// </exception>
    Task DeleteAsync(int id, ProviderRevisionPrecondition expected, CancellationToken ct = default);
}
