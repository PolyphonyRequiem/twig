namespace Twig.Domain.ValueObjects;

/// <summary>
/// Named error identifiers surfaced by <see cref="Twig.Domain.Interfaces.IReferenceProfileProvider"/>.
/// Each identifier is defined by the T1 note (AB#732) §7.1 (load-time) or §7.2
/// (command-time). Identifiers are byte-stable strings; callers may match on them
/// directly and telemetry may surface them (they carry no ADO-specific content).
/// </summary>
public static class ReferenceProfileErrors
{
    // ---- Load-time (T1 §7.1) --------------------------------------------------

    /// <summary>Embedded profile resource is missing from the loaded assembly.</summary>
    public const string ProfileBlobNotFound = "profile-blob-not-found";

    /// <summary>Embedded profile's canonical structural fingerprint (T1 §7.3) does not match.</summary>
    public const string ProfileFingerprintMismatch = "profile-fingerprint-mismatch";

    /// <summary>Embedded JSON did not deserialize under the source-generated context (missing required field, wrong type, unknown role).</summary>
    public const string ProfileSchemaInvalid = "profile-schema-invalid";

    /// <summary>The declared hierarchy block does not match the locked vocabulary (T1 §3.2).</summary>
    public const string HierarchyLockedVocabularyViolation = "hierarchy-locked-vocabulary-violation";

    /// <summary>types[*].role set does not equal the five vocabulary roles.</summary>
    public const string RoleSetNotCanonical = "role-set-not-canonical";

    /// <summary>linkKinds[*] does not match the exact §3.5 table.</summary>
    public const string LinkKindsNotCanonical = "link-kinds-not-canonical";

    /// <summary>primaryScope.eligibleRoles is empty.</summary>
    public const string PrimaryScopeEmptyAllowSet = "primary-scope-empty-allow-set";

    /// <summary>primaryScope.eligibleRoles contains an unknown role.</summary>
    public const string PrimaryScopeUnknownRole = "primary-scope-unknown-role";

    // ---- Command-time (T1 §7.2) ---------------------------------------------

    /// <summary>Live process's base-process parent reference disagrees with the profile.</summary>
    public const string BaseProcessParentMismatch = "base-process-parent-mismatch";

    /// <summary>A profile-declared type name is missing on the live process.</summary>
    public const string TypeNameMissing = "type-name-missing";

    /// <summary>A live type's declared state name set differs from the profile's (live has extra states).</summary>
    public const string LiveHasExtraState = "live-has-extra-state";

    /// <summary>A live type's declared state name set differs from the profile's (profile has extra states).</summary>
    public const string ProfileHasExtraState = "profile-has-extra-state";

    /// <summary>A live state's category disagrees with the profile.</summary>
    public const string StateCategoryMismatch = "state-category-mismatch";

    /// <summary>The live state ordering does not match the profile's declared ordering.</summary>
    public const string StateOrderMismatch = "state-order-mismatch";

    /// <summary>The live structural fingerprint (T1 §7.3) deviates from the profile's embedded copy.</summary>
    public const string LiveFingerprintMismatch = "live-fingerprint-mismatch";
}
