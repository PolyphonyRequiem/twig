using System.Text.Json.Serialization;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// Stable identity for one item in one admitted checkout and binding. Revision is
/// intentionally observation data, not part of the key.
/// </summary>
internal sealed record QualifiedWorkItemKey
{
    [JsonConstructor]
    public QualifiedWorkItemKey(string checkoutRoot, string connectionId, string bindingId, int workItemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingId);
        if (workItemId == 0)
            throw new ArgumentOutOfRangeException(nameof(workItemId), "A work-item ID cannot be zero.");

        CheckoutRoot = checkoutRoot;
        ConnectionId = connectionId;
        BindingId = bindingId;
        WorkItemId = workItemId;
    }

    public string CheckoutRoot { get; }
    public string ConnectionId { get; }
    public string BindingId { get; }
    public int WorkItemId { get; }
}
