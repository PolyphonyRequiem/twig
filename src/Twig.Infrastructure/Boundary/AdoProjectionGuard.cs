using System;
using System.Runtime.CompilerServices;

namespace Twig.Infrastructure.Boundary;

/// <summary>
/// Runtime backstop for contract §8.3 rail 3 — the ADO client
/// boundary. Together with rail 1 (compile-time namespace seam) and
/// rail 2 (the outbound call-graph architecture test), this class
/// closes off the scalar-through-a-string leak: a caller reading a
/// transport <c>adapterId</c>, <c>hostAttachmentId</c>, or status
/// name and passing it through a generic ADO field / link / comment
/// API cannot land the value in ADO without tripping the failure
/// identifier §8.3 pins.
///
/// <para>Namespace choice: this class lives in a neutral
/// <c>Twig.Infrastructure.Boundary</c> namespace so both the
/// transport read-boundary (where strings are marked on
/// construction) and the ADO write-boundary (where they are checked
/// before serialization) can reference it without the compile-time
/// namespace seam of §8.3 rail 1 catching the reference — the ADO
/// namespace only forbids reaching TRANSPORT types, not this
/// direction-neutral guard.</para>
///
/// <para>Mechanism: a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> tags string
/// instances originating from a transport read as transport-origin.
/// The tag survives copy-by-reference passing through arbitrary
/// generic sinks. Every ADO payload-serialization entry point calls
/// <see cref="AssertNoTransportOrigin(string?, string)"/> on the
/// text it is about to project, and a tagged string raises
/// <see cref="AdoProjectionForbiddenException"/> whose
/// <see cref="Exception.Message"/> carries the stable
/// <c>transport-ado-projection-forbidden</c> failure identifier from
/// §11.</para>
///
/// <para>False-positive contract: a literal shared with an
/// unrelated non-transport read path may be marked when interned;
/// this is acceptable because the check is a defence-in-depth
/// backstop for rails 1 and 2. False negatives — a transport string
/// that reaches ADO undetected — would be catastrophic; false
/// positives are easy to diagnose (the exception carries the sink
/// context) and repair. Adapters that legitimately need to pass a
/// non-transport string through the guard can bypass it by
/// constructing a fresh string instance.</para>
/// </summary>
internal static class AdoProjectionGuard
{
    private static readonly ConditionalWeakTable<string, object> _transportOrigin = new();
    private static readonly object _marker = new();

    /// <summary>
    /// Tag <paramref name="value"/> as originating from a transport
    /// read. Called at every transport read-boundary that materializes
    /// a scalar string: <see cref="Twig.Infrastructure.Persistence.Transport.TransportAdapterTarget"/>
    /// construction, <see cref="Twig.Infrastructure.Persistence.Transport.AdapterDescription"/>
    /// construction, etc. No-op for null / empty / interned-empty
    /// strings — <see cref="ConditionalWeakTable{TKey,TValue}"/>
    /// cannot bind those.
    /// </summary>
    public static void MarkTransportOrigin(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        // AddOrUpdate is idempotent; multiple marks on the same
        // instance are harmless.
        _transportOrigin.AddOrUpdate(value, _marker);
    }

    public static bool IsTransportOrigin(string? value) =>
        !string.IsNullOrEmpty(value) && _transportOrigin.TryGetValue(value, out _);

    /// <summary>
    /// Assert <paramref name="value"/> is not tagged as
    /// transport-origin. Throws
    /// <see cref="AdoProjectionForbiddenException"/> with the §11
    /// failure identifier <c>transport-ado-projection-forbidden</c>
    /// when it is.
    /// </summary>
    public static void AssertNoTransportOrigin(string? value, string sinkContext)
    {
        if (IsTransportOrigin(value))
            throw new AdoProjectionForbiddenException(sinkContext);
    }
}

/// <summary>
/// §11 <c>transport-ado-projection-forbidden</c> runtime backstop.
/// Raised by <see cref="AdoProjectionGuard.AssertNoTransportOrigin(string?, string)"/>
/// when a transport-origin string reaches an ADO field / link /
/// comment sink. The <see cref="Exception.Message"/> starts with the
/// stable identifier so downstream classifiers can route on the
/// literal without parsing prose (matching the
/// <see cref="Twig.Infrastructure.Persistence.Transport.TransportAttachmentFailure"/>
/// convention).
/// </summary>
internal sealed class AdoProjectionForbiddenException : Exception
{
    public const string FailureIdentifier = "transport-ado-projection-forbidden";

    public AdoProjectionForbiddenException(string sinkContext)
        : base($"{FailureIdentifier}: {sinkContext}") { }
}
