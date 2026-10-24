namespace Twig.Domain.ValueObjects;

/// <summary>Why a provider adapter refused a revision precondition.</summary>
public enum ProviderRevisionPreconditionRefusal
{
    /// <summary>No precondition, or no authoritative revision to build one from.</summary>
    Missing,

    /// <summary>The precondition was minted by a different provider.</summary>
    ForeignProvider,

    /// <summary>The token is not in the adapter's canonical encoding.</summary>
    MalformedToken,
}

/// <summary>
/// Thrown by a provider adapter that refuses a revision precondition before any transport call.
/// </summary>
/// <remarks>
/// <para>
/// Raised only before the request is sent, so nothing was written: callers may treat it as a
/// determinate failure of this attempt. It is never a concurrency conflict and must never
/// trigger a refetch or retry.
/// </para>
/// <para>
/// The message names the reason and the provider kinds only. It never carries the token
/// value or any work item content.
/// </para>
/// </remarks>
public sealed class ProviderRevisionPreconditionRefusedException : Exception
{
    /// <summary>Creates the refusal for <paramref name="reason"/> raised by <paramref name="adapterKind"/>.</summary>
    public ProviderRevisionPreconditionRefusedException(ProviderRevisionPreconditionRefusal reason, string adapterKind)
        : base(Describe(reason, adapterKind))
    {
        Reason = reason;
        AdapterKind = adapterKind;
    }

    /// <summary>Creates the refusal with an explicit message.</summary>
    public ProviderRevisionPreconditionRefusedException(string message)
        : base(message)
    {
        Reason = ProviderRevisionPreconditionRefusal.Missing;
        AdapterKind = string.Empty;
    }

    /// <summary>Creates the refusal with an explicit message and inner cause.</summary>
    public ProviderRevisionPreconditionRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = ProviderRevisionPreconditionRefusal.Missing;
        AdapterKind = string.Empty;
    }

    /// <summary>Parameterless constructor required by the exception design guideline.</summary>
    public ProviderRevisionPreconditionRefusedException()
        : this(ProviderRevisionPreconditionRefusal.Missing, string.Empty)
    {
    }

    /// <summary>The reason the precondition was refused.</summary>
    public ProviderRevisionPreconditionRefusal Reason { get; }

    /// <summary>The provider kind of the adapter that refused it.</summary>
    public string AdapterKind { get; }

    private static string Describe(ProviderRevisionPreconditionRefusal reason, string adapterKind)
    {
        var adapter = string.IsNullOrEmpty(adapterKind) ? "provider" : $"'{adapterKind}' provider";
        return reason switch
        {
            ProviderRevisionPreconditionRefusal.ForeignProvider =>
                $"revision-precondition-refused: the {adapter} adapter refused a revision precondition minted by a different provider; nothing was sent.",
            ProviderRevisionPreconditionRefusal.MalformedToken =>
                $"revision-precondition-refused: the {adapter} adapter refused a non-canonical revision token; nothing was sent.",
            _ =>
                $"revision-precondition-refused: the {adapter} adapter requires an authoritative revision precondition and none was supplied; nothing was sent.",
        };
    }
}
