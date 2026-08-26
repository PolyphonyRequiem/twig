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

    private readonly Assembly _assembly;
    private readonly object _sync = new();
    private Result<Domain.ValueObjects.ReferenceProfile>? _cached;

    public EmbeddedReferenceProfileProvider()
        : this(typeof(EmbeddedReferenceProfileProvider).Assembly)
    {
    }

    internal EmbeddedReferenceProfileProvider(Assembly assembly)
    {
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

    /// <inheritdoc />
    public Result ValidateAgainstLiveProcess(IProcessConfigurationProvider liveProcess)
    {
        var loaded = Load();
        if (!loaded.IsSuccess) return Result.Fail(loaded.Error);

        var profile = loaded.Value;
        var live = liveProcess.GetConfiguration();

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
        var liveFingerprint = ReferenceProfileFingerprint.ComputeLiveStructuralFingerprint(profile, liveProcess);
        var profileFingerprint = ReferenceProfileFingerprint.ComputeProfileStructuralFingerprint(profile);
        if (!string.Equals(liveFingerprint, profileFingerprint, StringComparison.Ordinal))
            return Result.Fail(ReferenceProfileErrors.LiveFingerprintMismatch);

        return Result.Ok();
    }

    /// <inheritdoc />
    public string ComputeLiveFingerprint(IProcessConfigurationProvider liveProcess)
    {
        var loaded = Load();
        if (!loaded.IsSuccess)
            throw new InvalidOperationException(
                $"Cannot compute live fingerprint: profile did not load ({loaded.Error}).");
        return ReferenceProfileFingerprint.ComputeLiveStructuralFingerprint(loaded.Value, liveProcess);
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

        // Locked hierarchy vocabulary (T1 §3.2).
        if (!EqualsInOrder(dto.Hierarchy.Apex, [Role.Initiative])
            || !EqualsInOrder(dto.Hierarchy.Requirement, [Role.Investigation, Role.Feature, Role.Bug])
            || !EqualsInOrder(dto.Hierarchy.Leaf, [Role.Task]))
        {
            return Result.Fail<Domain.ValueObjects.ReferenceProfile>(
                ReferenceProfileErrors.HierarchyLockedVocabularyViolation);
        }

        // Role set canonicality (T1 §3.3).
        var declaredRoles = new HashSet<Role>();
        foreach (var t in dto.Types)
        {
            if (t.Role is not { } role
                || string.IsNullOrWhiteSpace(t.TypeName)
                || string.IsNullOrWhiteSpace(t.BacklogRole)
                || string.IsNullOrWhiteSpace(t.BacklogBehaviorRef)
                || t.States is null || t.States.Count == 0)
            {
                return Result.Fail<Domain.ValueObjects.ReferenceProfile>(ReferenceProfileErrors.ProfileSchemaInvalid);
            }
            declaredRoles.Add(role);
        }
        if (declaredRoles.Count != 5
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
        foreach (var r in dto.PrimaryScope.EligibleRoles)
        {
            if (!Enum.IsDefined(r))
                return Result.Fail<Domain.ValueObjects.ReferenceProfile>(ReferenceProfileErrors.PrimaryScopeUnknownRole);
        }

        var types = dto.Types
            .Select(t => new ReferenceProfileType(
                t.Role!.Value,
                t.TypeName!,
                t.BacklogRole!,
                t.BacklogBehaviorRef!,
                t.States!.Select(s => new StateEntry(
                    s.Name ?? string.Empty,
                    s.Category ?? StateCategory.Unknown,
                    Color: null)).ToArray()))
            .ToArray();

        var linkKinds = dto.LinkKinds
            .Select(lk => new ReferenceProfileLinkKind(
                lk.Kind!.Value,
                lk.Meaning ?? string.Empty,
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
            new ReferenceProfilePrimaryScope(dto.PrimaryScope.Kind!, dto.PrimaryScope.EligibleRoles),
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
        // Exact §3.5 table. Any mutation of ADO reference names or their pairing
        // fails load. This is the ONE place in Twig core that names these strings;
        // downstream code speaks in LinkKind values.
        return rows.Count == 4
            && Match(rows[0], LinkKind.ParentChild,
                "System.LinkTypes.Hierarchy-Forward", "System.LinkTypes.Hierarchy-Reverse")
            && Match(rows[1], LinkKind.PredecessorSuccessor,
                "System.LinkTypes.Dependency-Forward", "System.LinkTypes.Dependency-Reverse")
            && Match(rows[2], LinkKind.Related,
                "System.LinkTypes.Related", null)
            && Match(rows[3], LinkKind.Artifact,
                null, null);

        static bool Match(LinkKindDto row, LinkKind kind, string? forward, string? reverse) =>
            row.Kind == kind
            && string.Equals(row.ForwardRel, forward, StringComparison.Ordinal)
            && string.Equals(row.ReverseRel, reverse, StringComparison.Ordinal);
    }
}
