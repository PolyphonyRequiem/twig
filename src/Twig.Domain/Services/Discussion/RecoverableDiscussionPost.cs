using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Discussion;

internal enum DiscussionPostFormat
{
    PlainText,
    Markdown,
    Html,
}

internal enum DiscussionPostState
{
    Prepared,
    Dispatching,
    Indeterminate,
    Confirmed,
}

internal enum DiscussionPostConfirmationSource
{
    PostResponse,
    Readback,
}

internal sealed record DiscussionPostReceipt
{
    [JsonConstructor]
    public DiscussionPostReceipt(
        string commentId,
        DateTimeOffset confirmedAt,
        DiscussionPostConfirmationSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commentId);
        if (!Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(source));

        CommentId = commentId;
        ConfirmedAt = confirmedAt;
        Source = source;
    }

    public string CommentId { get; }
    public DateTimeOffset ConfirmedAt { get; }
    public DiscussionPostConfirmationSource Source { get; }
}

/// <summary>
/// Durable native intent for one exact discussion post. An operation that has entered
/// dispatch can never return to Prepared, so an unknown outcome cannot be blindly replayed.
/// </summary>
internal sealed class RecoverableDiscussionPost
{
    public const int CurrentVersion = 1;

    [JsonConstructor]
    public RecoverableDiscussionPost(
        int version,
        Guid operationId,
        QualifiedWorkItemKey itemKey,
        DiscussionPostFormat format,
        string text,
        string payloadDigest,
        long generation,
        DiscussionPostState state,
        DiscussionPostReceipt? receipt,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(itemKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (version != CurrentVersion)
            throw new InvalidDataException($"Unsupported discussion post version {version}.");
        if (operationId == Guid.Empty)
            throw new ArgumentException("A discussion post operation requires an ID.", nameof(operationId));
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format));
        if (generation < 0)
            throw new ArgumentOutOfRangeException(nameof(generation));
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        if ((state == DiscussionPostState.Confirmed) != (receipt is not null))
            throw new ArgumentException("Only a confirmed discussion post may carry a receipt.", nameof(receipt));

        var expectedDigest = ComputePayloadDigest(version, itemKey, format, text);
        if (!string.Equals(payloadDigest, expectedDigest, StringComparison.Ordinal))
            throw new ArgumentException("The discussion post digest does not bind its exact payload.", nameof(payloadDigest));

        Version = version;
        OperationId = operationId;
        ItemKey = itemKey;
        Format = format;
        Text = text;
        PayloadDigest = payloadDigest;
        Generation = generation;
        State = state;
        Receipt = receipt;
        CreatedAt = createdAt;
    }

    public int Version { get; }
    public Guid OperationId { get; }
    public QualifiedWorkItemKey ItemKey { get; }
    public DiscussionPostFormat Format { get; }
    public string Text { get; }
    public string PayloadDigest { get; }
    public long Generation { get; }
    public DiscussionPostState State { get; }
    public DiscussionPostReceipt? Receipt { get; }
    public DateTimeOffset CreatedAt { get; }
    public bool RequiresReconciliation
        => State is DiscussionPostState.Dispatching or DiscussionPostState.Indeterminate;

    public static RecoverableDiscussionPost Create(
        Guid operationId,
        QualifiedWorkItemKey itemKey,
        DiscussionPostFormat format,
        string text,
        DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(itemKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new RecoverableDiscussionPost(
            CurrentVersion, operationId, itemKey, format, text,
            ComputePayloadDigest(CurrentVersion, itemKey, format, text),
            generation: 0, DiscussionPostState.Prepared, receipt: null,
            createdAt ?? DateTimeOffset.UtcNow);
    }

    public RecoverableDiscussionPost BeginDispatch()
        => Transition(DiscussionPostState.Prepared, DiscussionPostState.Dispatching);

    public RecoverableDiscussionPost MarkIndeterminate()
        => Transition(DiscussionPostState.Dispatching, DiscussionPostState.Indeterminate);

    public RecoverableDiscussionPost Confirm(DiscussionPostReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (State is not (DiscussionPostState.Dispatching or DiscussionPostState.Indeterminate))
            throw new InvalidOperationException($"A {State} discussion post cannot be confirmed.");
        return Copy(checked(Generation + 1), DiscussionPostState.Confirmed, receipt);
    }

    private RecoverableDiscussionPost Transition(
        DiscussionPostState requiredState,
        DiscussionPostState nextState)
    {
        if (State != requiredState)
            throw new InvalidOperationException($"A {State} discussion post cannot transition to {nextState}.");
        return Copy(checked(Generation + 1), nextState, receipt: null);
    }

    private RecoverableDiscussionPost Copy(
        long generation,
        DiscussionPostState state,
        DiscussionPostReceipt? receipt)
        => new(Version, OperationId, ItemKey, Format, Text, PayloadDigest,
            generation, state, receipt, CreatedAt);

    private static string ComputePayloadDigest(
        int version,
        QualifiedWorkItemKey itemKey,
        DiscussionPostFormat format,
        string text)
    {
        using var content = new MemoryStream();
        using (var writer = new BinaryWriter(content, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(version);
            writer.Write(itemKey.CheckoutRoot);
            writer.Write(itemKey.ConnectionId);
            writer.Write(itemKey.BindingId);
            writer.Write(itemKey.WorkItemId);
            writer.Write((int)format);
            writer.Write(text);
        }
        return Convert.ToHexStringLower(SHA256.HashData(content.ToArray()));
    }
}
