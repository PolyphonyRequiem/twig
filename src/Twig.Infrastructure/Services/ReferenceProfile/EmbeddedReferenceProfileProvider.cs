using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;
using Twig.Domain.Common;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Services.ReferenceProfile;

/// <summary>
/// The T3 seam (AB#734) implementation. Loads the reference profile from an
/// embedded assembly resource, validates it against T1 §7.1 / §6.1 / §6.5 /
/// §6.6, and caches the immutable
/// <see cref="Twig.Domain.ValueObjects.ReferenceProfile"/> for the process
/// lifetime.
/// </summary>
/// <remarks>
/// Single load per process; matches the <c>DynamicProcessConfigProvider</c>
/// pattern (T1 §8.1). The service is registered as a singleton in
/// <c>TwigServiceRegistration</c>.
/// </remarks>
internal sealed class EmbeddedReferenceProfileProvider : IReferenceProfileProvider
{
    /// <summary>The embedded-resource logical name for the profile JSON blob.</summary>
    /// <remarks>
    /// The build system maps <c>Resources/ReferenceProfile/profile.json</c> to
    /// <c>Twig.Infrastructure.Resources.ReferenceProfile.profile.json</c> via the
    /// default C# resource-name algorithm; a change to the file path REQUIRES a
    /// coordinated change here.
    /// </remarks>
    internal const string ProfileResourceName =
        "Twig.Infrastructure.Resources.ReferenceProfile.profile.json";

    /// <summary>
    /// The embedded-resource logical name for the byte-exact checksum sidecar
    /// T1 §1 ships beside the profile.
    /// </summary>
    /// <remarks>
    /// 🔴 Not redundant with the in-band <c>fingerprint.bytes</c>. That value is
    /// computed by deserializing the profile and RE-SERIALIZING it through the
    /// source-generated context (see
    /// <see cref="ReferenceProfileFingerprint.ComputeEmbeddedFingerprint"/>), so
    /// it hashes a normalized form and is structurally blind to raw-byte edits —
    /// role casing, key order, whitespace. The sidecar hashes the shipped bytes
    /// themselves, which is the only guard that sees them. AB#735 shipped a
    /// profile whose role spellings had diverged from the normative T1 §3 schema
    /// precisely because nothing compared raw bytes.
    /// </remarks>
    internal const string ProfileChecksumResourceName =
        "Twig.Infrastructure.Resources.ReferenceProfile.profile.json.sha256";

    /// <summary>The <c>$schema</c> literal T1 §3 fixes for this profile format.</summary>
    internal const string ProfileSchemaVersion = "twig-reference-profile/v1";

    /// <summary>The <c>fingerprint.algorithm</c> literal T1 §7.3 fixes.</summary>
    internal const string FingerprintAlgorithm = "twig-profile-fp/v1";

    private readonly Assembly _assembly;
    private readonly IReferenceProfilePinSource _pinSource;
    private readonly object _sync = new();
    private Result<Domain.ValueObjects.ReferenceProfile>? _cached;

    public EmbeddedReferenceProfileProvider(IReferenceProfilePinSource pinSource)
        : this(pinSource, typeof(EmbeddedReferenceProfileProvider).Assembly)
    {
    }

    internal EmbeddedReferenceProfileProvider(IReferenceProfilePinSource pinSource, Assembly assembly)
    {
        _pinSource = pinSource;
        _assembly = assembly;
    }

    /// <inheritdoc />
    public Result<Domain.ValueObjects.ReferenceProfile> Load()
    {
        if (_cached is { } cached)
            return cached;

        lock (_sync)
        {
            if (_cached is { } inner) return inner;
            _cached = LoadCore();
            return _cached.Value;
        }
    }

    public Result ValidatePin()
    {
        // Presence is checked BEFORE the blob is touched. Whether a repository
        // declared the reference process is a fact about its config alone, and a
        // caller distinguishing "never declared" from "declared but
        // unsatisfiable" must not have that answer contaminated by the state of
        // the shipped blob — otherwise a corrupt install makes every repository
        // look like it declared nothing.
        var pin = _pinSource.GetPin();
        if (pin is null)
            return Result.Fail(ReferenceProfileErrors.TwigJsonProfileBlockMissing);

        var loaded = Load();
        if (!loaded.IsSuccess) return Result.Fail(loaded.Error);

        var profile = loaded.Value;

        // T1 §6.1. All three are byte-equal (Ordinal) comparisons: the values are
        // opaque to Twig core, so a case-insensitive or trimmed match would be
        // inventing an equivalence the note does not grant. "Any subset match is
        // rejected" — so each field gets its own identifier and the first
        // disagreement wins, which is what makes the failure actionable.
        if (!string.Equals(pin.Identity, profile.Identity, StringComparison.Ordinal))
            return Result.Fail(ReferenceProfileErrors.ProfileIdentityUnknown);

        if (!string.Equals(pin.ProfileVersion, profile.ProfileVersion, StringComparison.Ordinal))
            return Result.Fail(ReferenceProfileErrors.ProfileVersionMismatch);

        if (!string.Equals(pin.BaseProcessVersion, profile.BaseProcess.TailoringVersion, StringComparison.Ordinal))
            return Result.Fail(ReferenceProfileErrors.BaseProcessVersionMismatch);

        return Result.Ok();
    }

    /// <inheritdoc />
    public Result ValidateAgainstLiveProcess(IProcessConfigurationProvider liveProcess, string liveBaseProcessRef)
    {
        var loaded = Load();
        if (!loaded.IsSuccess) return Result.Fail(loaded.Error);

        var profile = loaded.Value;
        var live = liveProcess.GetConfiguration();

        // T1 §6.2 — the first command-time check, and the one that was declared
        // but unreachable until AB#735 gave the method the live value to compare
        // against. Byte-equal: the reference is an opaque process id.
        if (!string.Equals(liveBaseProcessRef, profile.BaseProcess.ParentRef, StringComparison.Ordinal))
            return Result.Fail(ReferenceProfileErrors.BaseProcessParentMismatch);

        foreach (var declared in profile.Types)
        {
            var typeKey = WorkItemType.Parse(declared.TypeName);
            if (!typeKey.IsSuccess || !live.TypeConfigs.TryGetValue(typeKey.Value, out var typeConfig))
                return Result.Fail(ReferenceProfileErrors.TypeNameMissing);

            var liveStates = typeConfig.StateEntries;
            var profileStates = declared.States;

            var liveNames = new HashSet<string>(liveStates.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            var profileNames = new HashSet<string>(profileStates.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

            if (liveNames.Except(profileNames, StringComparer.OrdinalIgnoreCase).Any())
                return Result.Fail(ReferenceProfileErrors.LiveHasExtraState);
            if (profileNames.Except(liveNames, StringComparer.OrdinalIgnoreCase).Any())
                return Result.Fail(ReferenceProfileErrors.ProfileHasExtraState);

            if (liveStates.Count != profileStates.Count)
                return Result.Fail(ReferenceProfileErrors.StateOrderMismatch);

            for (var i = 0; i < profileStates.Count; i++)
            {
                if (!string.Equals(profileStates[i].Name, liveStates[i].Name, StringComparison.OrdinalIgnoreCase))
                    return Result.Fail(ReferenceProfileErrors.StateOrderMismatch);
                if (profileStates[i].Category != liveStates[i].Category)
                    return Result.Fail(ReferenceProfileErrors.StateCategoryMismatch);
            }
        }

        // §7.3 backstop: fingerprint the LIVE process against the profile's declared shape.
        // If any earlier enumerated check missed a divergence, this catches it.
        var liveFingerprint = ReferenceProfileFingerprint.ComputeLiveStructuralFingerprint(
            profile, liveProcess, liveBaseProcessRef);
        var profileFingerprint = ReferenceProfileFingerprint.ComputeProfileStructuralFingerprint(profile);
        if (!string.Equals(liveFingerprint, profileFingerprint, StringComparison.Ordinal))
            return Result.Fail(ReferenceProfileErrors.LiveFingerprintMismatch);

        return Result.Ok();
    }

    /// <inheritdoc />
    public string ComputeLiveFingerprint(IProcessConfigurationProvider liveProcess, string liveBaseProcessRef)
    {
        var loaded = Load();
        if (!loaded.IsSuccess)
            throw new InvalidOperationException(
                $"Cannot compute live fingerprint: profile did not load ({loaded.Error}).");
        return ReferenceProfileFingerprint.ComputeLiveStructuralFingerprint(
            loaded.Value, liveProcess, liveBaseProcessRef);
    }

    private Result<Domain.ValueObjects.ReferenceProfile> LoadCore()
    {
        byte[] rawJson;
        using (var stream = _assembly.GetManifestResourceStream(ProfileResourceName))
        {
            if (stream is null)
                return Result.Fail<Domain.ValueObjects.ReferenceProfile>(ReferenceProfileErrors.ProfileBlobNotFound);
            using var ms = new MemoryStream(capacity: (int)stream.Length);
            stream.CopyTo(ms);
            rawJson = ms.ToArray();
        }

        // T1 §1 byte-exact sidecar. Checked BEFORE parsing: if the shipped bytes
        // are not the bytes that were reviewed and released, nothing downstream
        // is worth interpreting. This is the check that sees raw-byte drift the
        // in-band fingerprint cannot — the AB#735 role-casing divergence was
        // exactly that shape.
        var checksumResult = VerifyEmbeddedChecksum(rawJson);
        if (!checksumResult.IsSuccess)
            return Result.Fail<Domain.ValueObjects.ReferenceProfile>(checksumResult.Error);

        ReferenceProfileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(rawJson, TwigJsonContext.Default.ReferenceProfileDto);
        }
        catch (JsonException)
        {
            return Result.Fail<Domain.ValueObjects.ReferenceProfile>(ReferenceProfileErrors.ProfileSchemaInvalid);
        }

        if (dto is null)
            return Result.Fail<Domain.ValueObjects.ReferenceProfile>(ReferenceProfileErrors.ProfileSchemaInvalid);

        var built = TryBuild(dto);
        if (!built.IsSuccess) return built;

        var recomputed = ReferenceProfileFingerprint.ComputeEmbeddedFingerprint(rawJson);
        if (!string.Equals(recomputed, built.Value.EmbeddedFingerprint, StringComparison.Ordinal))
            return Result.Fail<Domain.ValueObjects.ReferenceProfile>(ReferenceProfileErrors.ProfileFingerprintMismatch);

        return built;
    }

    /// <summary>
    /// Verifies the shipped <c>profile.json</c> bytes against the embedded
    /// <c>profile.json.sha256</c> sidecar (T1 §1).
    /// </summary>
    /// <remarks>
    /// A missing sidecar is <c>profile-blob-not-found</c>, not a pass: an
    /// integrity guard that disappears when its evidence disappears guards
    /// nothing. A mismatch is <c>profile-fingerprint-mismatch</c>, per T1 §1's
    /// statement that the sidecar is "covered by §7.3" — the sidecar is a second
    /// instrument reading the same property, so it reports under the same
    /// identifier rather than inventing one the note never declared.
    /// </remarks>
    private Result VerifyEmbeddedChecksum(byte[] rawJson)
    {
        string sidecar;
        using (var stream = _assembly.GetManifestResourceStream(ProfileChecksumResourceName))
        {
            if (stream is null)
                return Result.Fail(ReferenceProfileErrors.ProfileBlobNotFound);
            using var reader = new StreamReader(stream);
            sidecar = reader.ReadToEnd();
        }

        // The sidecar is a build artifact written by whatever tool the platform
        // provides, so tolerate surrounding whitespace and hex casing. The HASH
        // itself is still compared exactly — this normalizes the container, not
        // the claim.
        var expected = sidecar.Trim();
        var actual = Convert.ToHexStringLower(SHA256.HashData(rawJson));

        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
            ? Result.Ok()
            : Result.Fail(ReferenceProfileErrors.ProfileFingerprintMismatch);
    }

    private static Result<Domain.ValueObjects.ReferenceProfile> TryBuild(ReferenceProfileDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Identity)
            || string.IsNullOrWhiteSpace(dto.ProfileVersion)
            || dto.BaseProcess is null
            || string.IsNullOrWhiteSpace(dto.BaseProcess.ParentRef)
            || string.IsNullOrWhiteSpace(dto.BaseProcess.TailoringVersion)
            || dto.Hierarchy is null
            || dto.Types is null
            || dto.LinkKinds is null
            || dto.PrimaryScope is null
            || dto.Fingerprint is null
            || string.IsNullOrWhiteSpace(dto.Fingerprint.Bytes))
        {
            return Result.Fail<Domain.ValueObjects.ReferenceProfile>(ReferenceProfileErrors.ProfileSchemaInvalid);
        }

        // T1 §3 fixes both of these as literals. They are the document's
        // self-description: a profile that does not say which schema it is
        // written to, or which algorithm its fingerprint uses, cannot be
        // validated against anything — the reader would be assuming the very
        // facts it is supposed to check.
        if (!string.Equals(dto.Schema, ProfileSchemaVersion, StringComparison.Ordinal)
            || !string.Equals(dto.Fingerprint.Algorithm, FingerprintAlgorithm, StringComparison.Ordinal))
        {
            return Result.Fail<Domain.ValueObjects.ReferenceProfile>(ReferenceProfileErrors.ProfileSchemaInvalid);
        }

        // Locked hierarchy vocabulary (T1 §3.2).
        if (!EqualsInOrder(dto.Hierarchy.Apex, [Role.Initiative])
            || !EqualsInOrder(dto.Hierarchy.Requirement, [Role.Investigation, Role.Feature, Role.Bug])
            || !EqualsInOrder(dto.Hierarchy.Leaf, [Role.Task]))
        {
            return Result.Fail<Domain.ValueObjects.ReferenceProfile>(
                ReferenceProfileErrors.HierarchyLockedVocabularyViolation);
        }

        // Role set canonicality (T1 §3.3): "exactly five entries — one per role".
        var declaredRoles = new HashSet<Role>();
        foreach (var t in dto.Types)
        {
            if (t is null
                || t.Role is not { } role
                || string.IsNullOrWhiteSpace(t.TypeName)
                || string.IsNullOrWhiteSpace(t.BacklogRole)
                || string.IsNullOrWhiteSpace(t.BacklogBehaviorRef)
                || t.States is null || t.States.Count == 0)
            {
                return Result.Fail<Domain.ValueObjects.ReferenceProfile>(ReferenceProfileErrors.ProfileSchemaInvalid);
            }

            // Every state must declare both halves of its pair. Coercing a null
            // name to "" or a null category to Unknown would let a truncated
            // profile load and then silently fail every live state comparison,
            // reporting a drift that does not exist.
            foreach (var s in t.States)
            {
                if (s is null || string.IsNullOrWhiteSpace(s.Name) || s.Category is null)
                    return Result.Fail<Domain.ValueObjects.ReferenceProfile>(ReferenceProfileErrors.ProfileSchemaInvalid);
            }

            declaredRoles.Add(role);
        }

        // Count the ENTRIES, not just the distinct roles. A profile declaring six
        // types with one role repeated has a five-element role set and would pass
        // a set-only check, then throw ArgumentException from the aggregate's
        // role dictionary — a crash where a named error belongs.
        if (dto.Types.Count != 5
            || declaredRoles.Count != 5
            || !declaredRoles.SetEquals(ReferenceProfileFingerprint.CanonicalRoleOrder))
        {
            return Result.Fail<Domain.ValueObjects.ReferenceProfile>(ReferenceProfileErrors.RoleSetNotCanonical);
        }

        // Link-kind canonical table (T1 §3.5). Twig core does not carry any other spelling.
        if (!LinkKindsAreCanonical(dto.LinkKinds))
        {
            return Result.Fail<Domain.ValueObjects.ReferenceProfile>(ReferenceProfileErrors.LinkKindsNotCanonical);
        }

        // Primary-scope allow-set (T1 §6.6).
        if (string.IsNullOrWhiteSpace(dto.PrimaryScope.Kind)
            || dto.PrimaryScope.EligibleRoles is null)
        {
            return Result.Fail<Domain.ValueObjects.ReferenceProfile>(ReferenceProfileErrors.ProfileSchemaInvalid);
        }
        if (dto.PrimaryScope.EligibleRoles.Count == 0)
        {
            return Result.Fail<Domain.ValueObjects.ReferenceProfile>(ReferenceProfileErrors.PrimaryScopeEmptyAllowSet);
        }

        // Resolved from raw tokens so an unknown role reports T1 §6.6's own
        // identifier. A strongly-typed list could not: the canonical converter
        // rejects the token during deserialization, and the loader can only
        // report that as profile-schema-invalid — which would make
        // primary-scope-unknown-role dead on arrival.
        var eligibleRoles = new List<Role>(dto.PrimaryScope.EligibleRoles.Count);
        foreach (var token in dto.PrimaryScope.EligibleRoles)
        {
            if (!RoleTokens.TryResolve(token, out var resolved))
                return Result.Fail<Domain.ValueObjects.ReferenceProfile>(ReferenceProfileErrors.PrimaryScopeUnknownRole);
            eligibleRoles.Add(resolved);
        }

        var types = dto.Types
            .Select(t => new ReferenceProfileType(
                t.Role!.Value,
                t.TypeName!,
                t.BacklogRole!,
                t.BacklogBehaviorRef!,
                // Both halves were validated non-null above, so these are reads
                // rather than fallbacks; a coercion here would resurrect exactly
                // the silent-truncation case that guard exists to name.
                t.States!.Select(s => new StateEntry(s.Name!, s.Category!.Value, Color: null)).ToArray()))
            .ToArray();

        var linkKinds = dto.LinkKinds
            .Select(lk => new ReferenceProfileLinkKind(
                lk.Kind!.Value,
                lk.Meaning!,
                lk.ForwardRel,
                lk.ReverseRel))
            .ToArray();

        var profile = new Domain.ValueObjects.ReferenceProfile(
            dto.Identity!,
            dto.ProfileVersion!,
            new ReferenceProfileBaseProcess(dto.BaseProcess.ParentRef!, dto.BaseProcess.TailoringVersion!),
            new ReferenceProfileHierarchy(dto.Hierarchy.Apex!, dto.Hierarchy.Requirement!, dto.Hierarchy.Leaf!),
            types,
            linkKinds,
            new ReferenceProfilePrimaryScope(dto.PrimaryScope.Kind!, eligibleRoles),
            dto.Fingerprint.Bytes!);

        return Result.Ok(profile);
    }

    private static bool EqualsInOrder(IReadOnlyList<Role>? actual, Role[] expected)
    {
        if (actual is null || actual.Count != expected.Length) return false;
        for (var i = 0; i < expected.Length; i++)
            if (actual[i] != expected[i]) return false;
        return true;
    }

    private static bool LinkKindsAreCanonical(IReadOnlyList<LinkKindDto> rows)
    {
        // Exact §3.5 table. Any mutation of ADO reference names, their pairing,
        // or the declared MEANING fails load. This is the ONE place in Twig core
        // that names these strings; downstream code speaks in LinkKind values.
        //
        // The meaning is compared because it is what the table actually fixes:
        // the relation refs are ADO's own well-known names, whereas
        // "predecessor-successor means blocking-sequencing" is the profile's
        // semantic claim and the thing a reviewer reads the row for. Leaving it
        // unchecked let a document assert any meaning it liked.
        return rows.Count == 4
            && Match(rows[0], LinkKind.ParentChild, "decomposition",
                "System.LinkTypes.Hierarchy-Forward", "System.LinkTypes.Hierarchy-Reverse")
            && Match(rows[1], LinkKind.PredecessorSuccessor, "blocking-sequencing",
                "System.LinkTypes.Dependency-Forward", "System.LinkTypes.Dependency-Reverse")
            && Match(rows[2], LinkKind.Related, "informs",
                "System.LinkTypes.Related", null)
            && Match(rows[3], LinkKind.Artifact, "evidence",
                null, null);

        static bool Match(LinkKindDto row, LinkKind kind, string meaning, string? forward, string? reverse) =>
            row.Kind == kind
            && string.Equals(row.Meaning, meaning, StringComparison.Ordinal)
            && string.Equals(row.ForwardRel, forward, StringComparison.Ordinal)
            && string.Equals(row.ReverseRel, reverse, StringComparison.Ordinal);
    }
}
