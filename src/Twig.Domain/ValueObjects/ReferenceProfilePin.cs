namespace Twig.Domain.ValueObjects;

/// <summary>
/// The three-field reference-profile pin a repository checks in, per T1
/// (AB#732) §2 and §5.1. Read from <c>twig.json</c>'s <c>profile</c> block and
/// exact-matched against the embedded profile at load time (T1 §6.1).
/// </summary>
/// <remarks>
/// Every field is opaque to Twig core — compared byte-equal, never parsed.
/// That is what lets the pin couple a repository to one released profile
/// without Twig core acquiring an opinion about version syntax or process
/// naming.
/// </remarks>
/// <param name="Identity">Matched against the embedded <c>identity</c>.</param>
/// <param name="ProfileVersion">Matched against the embedded <c>profileVersion</c>.</param>
/// <param name="BaseProcessVersion">Matched against the embedded <c>baseProcess.tailoringVersion</c>.</param>
public sealed record ReferenceProfilePin(
    string Identity,
    string ProfileVersion,
    string BaseProcessVersion);
