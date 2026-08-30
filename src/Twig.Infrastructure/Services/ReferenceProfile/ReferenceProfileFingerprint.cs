using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Services.ReferenceProfile;

/// <summary>
/// The T1 (AB#732) §7.3 canonical structural fingerprints:
/// <list type="bullet">
///   <item><b>Embedded blob fingerprint (load-time).</b> SHA-256 of the profile
///   JSON with <c>fingerprint.bytes</c> blanked so the hash can live inside
///   the file it hashes. Enforces release-time integrity.</item>
///   <item><b>Structural fingerprint (command-time).</b> SHA-256 of a
///   canonical role-driven serialization of a process's shape. Applied twice
///   at command-time — once to the profile's declared shape, once to the live
///   process — and the two are compared for drift detection.</item>
/// </list>
/// Both are lowercase hex.
/// </summary>
internal static class ReferenceProfileFingerprint
{
    /// <summary>
    /// Computes the embedded blob fingerprint per T1 §7.3. Algorithm: parse
    /// the profile JSON, blank the <c>fingerprint.bytes</c> field, re-serialize
    /// under the source-generated context, SHA-256, lowercase hex.
    /// </summary>
    public static string ComputeEmbeddedFingerprint(byte[] rawJson)
    {
        var dto = JsonSerializer.Deserialize(rawJson, TwigJsonContext.Default.ReferenceProfileDto)
            ?? throw new InvalidOperationException("profile.json failed to deserialize during fingerprint computation.");

        // The fingerprint field is inside the file it hashes; blank it before hashing so
        // the hash can live in-band without recursively depending on itself.
        dto.Fingerprint ??= new FingerprintDto();
        dto.Fingerprint.Bytes = string.Empty;

        var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(dto, TwigJsonContext.Default.ReferenceProfileDto);
        var hash = SHA256.HashData(canonicalBytes);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Computes the T1 §7.3 structural fingerprint of the PROFILE's own
    /// declared shape. Uses profile-declared type names, backlog behaviour
    /// refs, backlog roles, and the profile's own <see cref="StateEntry"/>
    /// list — no live data reached. Deterministic per loaded profile.
    /// </summary>
    public static string ComputeProfileStructuralFingerprint(Twig.Domain.ValueObjects.ReferenceProfile profile) =>
        Hash(BuildCanonical(
            profile,
            baseProcessRef: profile.BaseProcess.ParentRef,
            resolveStates: role => profile.TypeByRole(role).States));

    /// <summary>
    /// Computes the T1 §7.3 structural fingerprint of the LIVE process, using
    /// the profile's role order and declared type-name bindings to look up
    /// live state entries. Any live divergence along an axis the enumerated
    /// §6.2–§6.4 checks miss shifts this hash — that's the drift backstop.
    /// </summary>
    public static string ComputeLiveStructuralFingerprint(
        Twig.Domain.ValueObjects.ReferenceProfile profile,
        IProcessConfigurationProvider liveProcess,
        string liveBaseProcessRef)
    {
        var live = liveProcess.GetConfiguration();
        return Hash(BuildCanonical(profile, baseProcessRef: liveBaseProcessRef, resolveStates: role =>
        {
            var typeKey = WorkItemType.Parse(profile.TypeByRole(role).TypeName);
            if (typeKey.IsSuccess && live.TypeConfigs.TryGetValue(typeKey.Value, out var typeConfig))
                return typeConfig.StateEntries;
            return Array.Empty<StateEntry>();
        }));
    }

    /// <summary>The canonical role order used by every §7.3 fingerprint pass.</summary>
    internal static readonly Role[] CanonicalRoleOrder =
    [
        Role.Initiative,
        Role.Investigation,
        Role.Feature,
        Role.Bug,
        Role.Task,
    ];

    private static string BuildCanonical(
        Twig.Domain.ValueObjects.ReferenceProfile profile,
        string baseProcessRef,
        Func<Role, IReadOnlyList<StateEntry>> resolveStates)
    {
        var sb = new StringBuilder();

        // T1 §7.3 component 1. Supplied by the caller rather than read off the
        // profile so the LIVE pass contributes the live value: reading it from
        // the profile on both passes put identical bytes on both sides, which
        // made the backstop blind to exactly the drift §6.2 names.
        sb.Append(baseProcessRef);
        sb.Append('\n');

        foreach (var role in CanonicalRoleOrder)
        {
            var declared = profile.TypeByRole(role);
            var states = resolveStates(role);

            sb.Append(declared.TypeName.ToLowerInvariant());
            sb.Append('|');
            sb.Append(declared.BacklogBehaviorRef);
            sb.Append('|');
            sb.Append(declared.BacklogRole);
            sb.Append('|');

            for (var i = 0; i < states.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(states[i].Name.ToLowerInvariant());
                sb.Append('|');
                sb.Append(states[i].Category);
            }
            sb.Append('\n');
        }

        foreach (var linkKind in profile.LinkKinds)
        {
            sb.Append(linkKind.Kind);
            sb.Append('|');
            sb.Append(linkKind.ForwardRel ?? string.Empty);
            sb.Append('|');
            sb.Append(linkKind.ReverseRel ?? string.Empty);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string Hash(string canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}
