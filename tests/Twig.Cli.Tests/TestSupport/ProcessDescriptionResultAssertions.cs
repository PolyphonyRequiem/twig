using Shouldly;
using Twig.Domain.Services.Process;
using Twig.Domain.ValueObjects;

namespace Twig.Cli.Tests.TestSupport;

/// <summary>
/// Unwrapping helpers for <see cref="ProcessDescriptionResult"/> in tests.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 These do NOT weaken the union back into the null-plus-exception shape AB#244 removed.
/// The distinction is who is entitled to assume: production code must handle every arm, and
/// does (both callers switch exhaustively with an <c>UnreachableException</c> default). A test
/// that scripted a WORKING source and is asserting something about the DOCUMENT has already
/// stated which arm it expects, and <see cref="ShouldBeAssembled"/> makes that expectation an
/// assertion rather than an assumption — it FAILS naming the arm it actually got, where a bare
/// cast would throw an opaque <c>InvalidCastException</c> and the old <c>!</c> on a nullable
/// would have thrown a bare <c>NullReferenceException</c> further down.
/// </para>
/// <para>
/// 🔴 <c>ShouldBeOfType&lt;T&gt;()</c> does not work here: a C# <c>union</c> is a WRAPPER, so
/// the runtime type of the result is <see cref="ProcessDescriptionResult"/> and never the case
/// type. Pattern-match the case — the same trap <c>MergeResult</c> already sets in this repo.
/// </para>
/// </remarks>
internal static class ProcessDescriptionResultAssertions
{
    /// <summary>Asserts the result is the success arm and returns the document.</summary>
    internal static ProcessDescription ShouldBeAssembled(this ProcessDescriptionResult result)
    {
        if (result is ProcessDescriptionAssembled assembled)
            return assembled.Description;

        throw new ShouldAssertException(
            $"Expected ProcessDescriptionAssembled but got {result.Value?.GetType().Name}.");
    }

    /// <summary>Asserts the result is the not-found arm and returns it.</summary>
    internal static ProcessDescriptionTypeNotFound ShouldBeTypeNotFound(
        this ProcessDescriptionResult result)
    {
        if (result is ProcessDescriptionTypeNotFound notFound)
            return notFound;

        throw new ShouldAssertException(
            $"Expected ProcessDescriptionTypeNotFound but got {result.Value?.GetType().Name}.");
    }
}
