using Twig.Domain.Services.Attachment;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// Preserves the native storage failure code when binding resolution cannot finish.
/// Existing InvalidOperationException consumers keep their message/catch behavior;
/// browser consumers can distinguish transient native contention without parsing prose.
/// </summary>
internal sealed class ConnectionBindingResolutionException(string failureCode, string message)
    : InvalidOperationException(message)
{
    internal string FailureCode { get; } = failureCode;
    internal bool IsUnavailable => FailureCode == AttachmentStorageFailure.SystemStoreLocked;
}
