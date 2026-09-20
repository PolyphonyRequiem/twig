using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Twig.Skills;

internal sealed class SkillManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Provider { get; set; } = "";
    public string PackageIdentity { get; set; } = "";
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Separately supplied packages, tracked for conflict-safe reinstall and preserved by base updates.</summary>
    public Dictionary<string, SkillExternal> Externals { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class SkillExternal
{
    public string SourceIdentity { get; set; } = "";
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Null for legacy or Windows installs, where Unix execution modes were not recorded.</summary>
    public Dictionary<string, int>? UnixExecuteModes { get; set; }
}

internal sealed class SkillSelections
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, Dictionary<string, string>> Selections { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SkillManifest))]
[JsonSerializable(typeof(SkillSelections))]
[JsonSerializable(typeof(SkillCommandOutput))]
[JsonSerializable(typeof(SkillCommandErrorOutput))]
internal partial class SkillJsonContext : JsonSerializerContext;

/// <summary>Machine-readable envelope for a successful skills-command execution.</summary>
internal sealed record SkillCommandOutput(
    string State,
    string Provider,
    string Target,
    string PackageIdentity,
    string? InstalledIdentity,
    Dictionary<string, string> Selections,
    List<string> Externals,
    List<string> Warnings,
    string DiscoveryScope,
    string ProviderHint);

/// <summary>Machine-readable envelope for a skills-command failure.</summary>
internal sealed record SkillCommandErrorOutput(string Error, string Recovery);

internal sealed record SkillLifecycleResult(string State, string Provider, string Target, string PackageIdentity,
    string? InstalledIdentity, IReadOnlyList<string> Warnings)
{
    internal IReadOnlyDictionary<string, string> Selections { get; init; } = new Dictionary<string, string>();
    internal IReadOnlyList<string> Externals { get; init; } = Array.Empty<string>();
    internal string DiscoveryScope => "Discovery check is bounded to direct child SKILL.md files with unambiguous YAML metadata in the target and optional --scan-root. Nested categories, other profiles, project ancestors, plugins, custom roots, trust, enablement and actual host precedence are not globally verified.";
}

/// <summary>
/// Owns only the canonical Twig skill directory's manifest-listed files and its own manifest.
/// Safely migrates the former twig-cli/twig-changes package when its tracked bytes are intact.
/// Refuses conflicts rather than adopting or forcing over user files. No provider config reads/writes.
/// </summary>
internal sealed class SkillLifecycle(SkillPackage package)
{
    internal const string ManifestPath = ".twig-skills-manifest.json";
    internal const string SelectionPath = "twig/references/user-selections.json";
    internal const string LegacySelectionPath = "twig-cli/references/user-selections.json";
    private const string LockPath = ".twig-skills.lock";
    private const UnixFileMode ExecuteBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
    private const UnixFileMode PrivilegedBits = UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit;
    private const UnixFileMode OrdinaryBits = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    internal SkillLifecycleResult Install(string provider, string target, string? scanRoot = null) =>
        Change(provider, target, update: false, scanRoot);

    internal SkillLifecycleResult Update(string provider, string target, string? scanRoot = null) =>
        Change(provider, target, update: true, scanRoot);

    private SkillLifecycleResult Change(string provider, string target, bool update, string? scanRoot)
    {
        provider = Provider(provider);
        target = SkillPaths.Target(target);
        ValidateScanRoot(scanRoot);
        // Validate existing state and every destination before creating even the lock file.
        var manifest = ReadManifest(target, provider);
        // Discovery failures (permissions, symlinks) and name aliases must not first appear after a write.
        var discovered = Discover(target, scanRoot);
        if (manifest is null || IsLegacyManifest(manifest))
        {
            foreach (var (name, path) in discovered.Where(s => SkillPaths.ReservedNames.Contains(s.Name, StringComparer.Ordinal)))
            {
                var ownedPath = manifest is not null && manifest.Files.ContainsKey(name + "/SKILL.md")
                    ? SkillPaths.Under(target, name + "/SKILL.md")
                    : null;
                if (ownedPath is null || !path.Equals(ownedPath, SkillPaths.PathComparison))
                    throw new SkillLifecycleException($"Name conflict for '{name}': {path}. Preserve/rename it before installing or migrating; Twig will not adopt or overwrite it.");
            }
        }
        if (manifest is null)
        {
            if (update) throw new SkillLifecycleException("Twig skills are not installed at this target. Run twig skills install with the same --provider and --target.");
            foreach (var name in SkillPaths.ReservedNames)
            {
                var directory = SkillPaths.Under(target, name);
                if (Directory.Exists(directory) || File.Exists(directory))
                    throw new SkillLifecycleException($"Name conflict: {directory} already exists without a Twig manifest. Preserve/rename it or choose another target; Twig will not adopt or overwrite it.");
            }
        }
        else
        {
            VerifyManaged(target, manifest);
            ReadSelections(target, manifest);
            if (manifest.PackageIdentity != package.Identity && !update)
                throw new SkillLifecycleException("Installed Twig guidance belongs to a different version/build. Run twig skills update with the same --provider and --target; install never silently upgrades.");
            if (manifest.PackageIdentity == package.Identity)
                return Result("current", provider, target, manifest, scanRoot);
        }
        if (manifest is not null && IsLegacyManifest(manifest))
        {
            var canonicalRoot = SkillPaths.Under(target, "twig");
            if (Directory.Exists(canonicalRoot) || File.Exists(canonicalRoot))
                throw new SkillLifecycleException($"Unmanaged canonical name conflict: {canonicalRoot}. Preserve/rename it before migrating; no force overwrite is available.");
        }

        var desired = package.GetFiles().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        desired[SelectionPath] = manifest is null
            ? JsonSerializer.SerializeToUtf8Bytes(new SkillSelections(), SkillJsonContext.Default.SkillSelections)
            : File.ReadAllBytes(SkillPaths.Under(target, SelectionPathFor(manifest)));
        foreach (var path in desired.Keys)
        {
            var destination = SkillPaths.Under(target, path);
            if (Directory.Exists(destination) || (File.Exists(destination) && (manifest is null || !manifest.Files.ContainsKey(path))))
                throw new SkillLifecycleException($"Unmanaged file conflict: {destination}. Preserve/rename this file before updating; no force overwrite is available.");
            CheckParentDirectories(destination, target);
        }

        Directory.CreateDirectory(target);
        using var operationLock = AcquireLock(target);
        // Re-check while holding the cooperating-process lock. No broad recursive deletion.
        var current = ReadManifest(target, provider);
        if (!SameManifest(manifest, current)) throw new SkillLifecycleException("Installation changed concurrently; retry after inspecting status.");
        if (current is not null) VerifyManaged(target, current);
        var next = new SkillManifest { Provider = provider, PackageIdentity = package.Identity };
        if (manifest is not null)
            foreach (var (name, external) in manifest.Externals)
                next.Externals[name] = external;
        foreach (var (path, bytes) in desired)
        {
            // Per-file atomic replacement avoids following hard links or truncating existing files.
            AtomicWrite(target, path, bytes, overwrite: manifest?.Files.ContainsKey(path) == true);
            next.Files.Add(path, SkillPackage.Hash(bytes));
        }
        if (manifest is not null)
        {
            foreach (var obsolete in manifest.Files.Keys.Except(desired.Keys, StringComparer.Ordinal))
                File.Delete(SkillPaths.Under(target, obsolete));
            DeleteEmptyLegacyDirectories(target);
        }
        // Manifest last: interruptions leave an honestly inconsistent install, never a false current verdict.
        AtomicWrite(target, ManifestPath, JsonSerializer.SerializeToUtf8Bytes(next, SkillJsonContext.Default.SkillManifest), overwrite: manifest is not null);
        return Result(manifest is null ? "installed" : "updated", provider, target, next, scanRoot);
    }

    internal SkillLifecycleResult Configure(string provider, string target, string scenario, string? companion = null,
        bool clear = false, string? scanRoot = null)
    {
        provider = Provider(provider);
        target = SkillPaths.Target(target);
        ValidateScanRoot(scanRoot);
        ValidateName(scenario, "scenario");
        if (clear == !string.IsNullOrWhiteSpace(companion))
            throw new SkillLifecycleException("Choose exactly one of --companion NAME or --clear.");
        if (companion is not null)
        {
            ValidateName(companion, "companion");
            if (SkillPaths.ReservedNames.Contains(companion, StringComparer.Ordinal))
                throw new SkillLifecycleException("A companion must have its own name, distinct from twig and its retired package names.");
        }
        var manifest = ReadManifest(target, provider)
            ?? throw new SkillLifecycleException("Twig skills are not installed at this target. Run twig skills install first.");
        VerifyManaged(target, manifest);
        if (manifest.PackageIdentity != package.Identity)
            throw new SkillLifecycleException("Twig guidance is stale; run twig skills update before configure.");
        var discovered = Discover(target, scanRoot);
        if (!clear)
        {
            var matches = discovered.Where(s => s.Name == companion).ToList();
            if (matches.Count == 0)
                throw new SkillLifecycleException($"Selected companion '{companion}' is missing from the bounded roots. Install your separately named skill there or provide --scan-root; nothing was changed.");
            if (matches.Count > 1)
                throw new SkillLifecycleException($"Companion '{companion}' has potential shadowing in the bounded roots. Resolve duplicate names before configuring.");
        }
        using var operationLock = AcquireLock(target);
        var current = ReadManifest(target, provider);
        if (!SameManifest(manifest, current)) throw new SkillLifecycleException("Installation changed concurrently; retry after inspecting status.");
        VerifyManaged(target, manifest);
        var selections = ReadSelections(target, manifest);
        if (!selections.Selections.TryGetValue(provider, out var scenarios))
            selections.Selections[provider] = scenarios = new(StringComparer.Ordinal);
        if (clear) scenarios.Remove(scenario);
        else scenarios[scenario] = companion!;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(selections, SkillJsonContext.Default.SkillSelections);
        AtomicWrite(target, SelectionPath, bytes, overwrite: true);
        manifest.Files[SelectionPath] = SkillPackage.Hash(bytes);
        AtomicWrite(target, ManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, SkillJsonContext.Default.SkillManifest), overwrite: true);
        return Result("configured", provider, target, manifest, scanRoot);
    }

    internal SkillLifecycleResult Status(string provider, string target, string? scanRoot = null)
    {
        provider = Provider(provider);
        target = SkillPaths.Target(target);
        ValidateScanRoot(scanRoot);
        var manifest = ReadManifest(target, provider);
        if (manifest is null) return Result("not-installed", provider, target, null, scanRoot);
        VerifyManaged(target, manifest);
        return Result(manifest.PackageIdentity == package.Identity ? "current" : "stale", provider, target, manifest, scanRoot);
    }

    /// <summary>Install a separately named integration/variant skill from an explicit local source directory.
    /// Idempotent when source bytes and executable modes are unchanged; refuses to silently overwrite a different revision.</summary>
    internal SkillLifecycleResult AddExternal(string provider, string target, string source, string? scanRoot = null)
    {
        provider = Provider(provider);
        target = SkillPaths.Target(target);
        ValidateScanRoot(scanRoot);
        if (string.IsNullOrWhiteSpace(source))
            throw new SkillLifecycleException("An explicit --source PATH is required. Point at a local directory containing SKILL.md; Twig never installs a bundled default as an external.");
        var sourceFull = Path.GetFullPath(source);
        SkillPaths.RejectLinks(sourceFull);
        if (!Directory.Exists(sourceFull))
            throw new SkillLifecycleException($"External source directory not found: {sourceFull}.");
        var manifest = ReadManifest(target, provider)
            ?? throw new SkillLifecycleException("Twig skills are not installed at this target. Run twig skills install first.");
        VerifyManaged(target, manifest);
        if (manifest.PackageIdentity != package.Identity)
            throw new SkillLifecycleException("Twig guidance is stale; run twig skills update before adding an external skill.");
        var discovered = Discover(target, scanRoot);

        var files = ReadExternalSource(sourceFull);
        var name = SkillFrontmatter.ReadName(new StringReader(Encoding.UTF8.GetString(files["SKILL.md"].Bytes)), requireDescription: true)
            ?? throw new SkillLifecycleException("Source SKILL.md has no `name:` frontmatter entry. External skills must be self-named; no default is inferred.");
        SkillPaths.ValidateExternalName(name);
        var sourceIdentity = "sha256:" + SkillPackage.Hash(
            Encoding.UTF8.GetBytes(string.Join("\n", files.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
                p.Key + ":" + SkillPackage.Hash(p.Value.Bytes) + (p.Value.Mode is { } mode ? ":x" + ((int)(mode & ExecuteBits)).ToString(System.Globalization.CultureInfo.InvariantCulture) : "")))));

        if (manifest.Externals.TryGetValue(name, out var existing))
        {
            if (existing.SourceIdentity == sourceIdentity)
            {
                // Re-add refuses both byte and executable-mode edits; base updates preserve them.
                VerifyExternalFiles(target, name, existing);
                return Result("current", provider, target, manifest, scanRoot);
            }
            throw new SkillLifecycleException($"External '{name}' is already installed at a different revision. Point --source at the tracked bytes, or install into a different --target; this command does not remove.");
        }
        if (discovered.Any(skill => skill.Name == name))
            throw new SkillLifecycleException($"External '{name}' already exists in the bounded discovery roots. Resolve this name collision before adding; no files changed.");

        // Refuse if a directory or file already occupies <target>/<name> — Twig never adopts unmanaged content.
        var externalRoot = SkillPaths.Under(target, name);
        if (Directory.Exists(externalRoot) || File.Exists(externalRoot))
            throw new SkillLifecycleException($"Name conflict: {externalRoot} already exists but is not tracked as a Twig external. Preserve/rename it or choose another name before adding.");

        // Pre-validate every destination path before touching the filesystem.
        foreach (var relative in files.Keys)
        {
            var destination = SkillPaths.Under(target, name + "/" + relative);
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new SkillLifecycleException($"Name conflict inside external: {destination} already exists.");
            CheckParentDirectories(destination, target);
        }

        using var operationLock = AcquireLock(target);
        var current = ReadManifest(target, provider);
        if (!SameManifest(manifest, current)) throw new SkillLifecycleException("Installation changed concurrently; retry after inspecting status.");
        VerifyManaged(target, manifest);
        var record = new SkillExternal { SourceIdentity = sourceIdentity, UnixExecuteModes = OperatingSystem.IsWindows() ? null : new(StringComparer.Ordinal) };
        foreach (var (relative, file) in files)
        {
            AtomicWrite(target, name + "/" + relative, file.Bytes, overwrite: false, file.Mode);
            record.Files.Add(relative, SkillPackage.Hash(file.Bytes));
            if (file.Mode is { } mode) record.UnixExecuteModes!.Add(relative, (int)(mode & ExecuteBits));
        }
        manifest.Externals[name] = record;
        AtomicWrite(target, ManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, SkillJsonContext.Default.SkillManifest), overwrite: true);
        return Result("added", provider, target, manifest, scanRoot);
    }


    private sealed record ExternalSourceFile(byte[] Bytes, UnixFileMode? Mode);

    private static Dictionary<string, ExternalSourceFile> ReadExternalSource(string sourceFull)
    {
        var files = new Dictionary<string, ExternalSourceFile>(StringComparer.Ordinal);
        var portablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<string>();
        directories.Push(sourceFull);
        while (directories.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                // Reject a directory link before enumeration can traverse it (including
                // empty links or cycles, which file-only checks would never encounter).
                SkillPaths.RejectLinks(path);
                var relative = Path.GetRelativePath(sourceFull, path).Replace(Path.DirectorySeparatorChar, '/');
                SkillPaths.ValidateExternalRelative("source", relative);
                if (!portablePaths.Add(relative))
                    throw new SkillLifecycleException($"External source contains a case-insensitive path alias: {relative}.");
                if (Directory.Exists(path)) directories.Push(path);
                else files.Add(relative, new(File.ReadAllBytes(path),
                    OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path) & OrdinaryBits));
            }
        }
        if (!files.ContainsKey("SKILL.md"))
            throw new SkillLifecycleException($"External source is missing a top-level SKILL.md: {sourceFull}.");
        return files;
    }



    private SkillLifecycleResult Result(string state, string provider, string target, SkillManifest? manifest, string? scanRoot)
    {
        var warnings = new List<string>();
        var discovered = Discover(target, scanRoot);
        var names = new HashSet<string>(SkillPaths.Family, StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> selected = new Dictionary<string, string>();
        var externals = manifest?.Externals.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList() ?? new List<string>();
        foreach (var name in externals) names.Add(name);
        if (manifest is not null)
        {
            foreach (var (name, external) in manifest.Externals)
                warnings.AddRange(ExternalModeWarnings(target, name, external));
            var selections = ReadSelections(target, manifest);
            if (selections.Selections.TryGetValue(provider, out var scenarios))
            {
                selected = scenarios;
                foreach (var (scenario, companion) in scenarios)
                {
                    names.Add(companion);
                    if (!discovered.Any(s => s.Name == companion))
                        warnings.Add($"Companion '{companion}' selected for {provider}/{scenario} is missing in bounded roots. Report a presentation fallback to base guidance; correctness and approval prerequisites remain mandatory. Other host roots were not checked.");
                }
            }
        }
        foreach (var name in names.Concat(SkillPaths.LegacyFamily).Distinct(StringComparer.Ordinal))
        {
            var matches = discovered.Where(s => s.Name == name).ToList();
            if (matches.Count > 1)
                warnings.Add($"Potential shadowing for '{name}': {string.Join(", ", matches.Select(s => s.Path))}. Provider precedence is not inferred; verify the actual loaded skill.");
            if (manifest is null && SkillPaths.ReservedNames.Contains(name, StringComparer.Ordinal) && matches.Count > 0)
                warnings.Add($"Unmanaged Twig name conflict for '{name}': {string.Join(", ", matches.Select(s => s.Path))}.");
        }
        return new SkillLifecycleResult(state, provider, target, package.Identity, manifest?.PackageIdentity, warnings) { Selections = selected, Externals = externals };
    }

    private static SkillManifest? ReadManifest(string target, string provider)
    {
        var path = SkillPaths.Under(target, ManifestPath);
        if (Directory.Exists(path)) throw new SkillLifecycleException("Manifest path is a directory; preserve it and choose another target.");
        if (!File.Exists(path)) return null;
        SkillManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(File.ReadAllBytes(path), SkillJsonContext.Default.SkillManifest)
                ?? throw new SkillLifecycleException("Twig manifest is empty.");
        }
        catch (JsonException e) { throw new SkillLifecycleException($"Invalid Twig manifest: {e.Message}. Preserve it and restore from a trusted backup."); }
        if (manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.PackageIdentity) || manifest.Files is null)
            throw new SkillLifecycleException("Unsupported or invalid Twig manifest; no files changed.");
        if (manifest.Provider != provider)
            throw new SkillLifecycleException($"Manifest provider is '{manifest.Provider}', not '{provider}'. Select its original --provider or use a separate target.");
        foreach (var (relative, hash) in manifest.Files)
        {
            SkillPaths.ValidateManagedPath(relative);
            if (hash is null || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
                throw new SkillLifecycleException($"Invalid managed hash for '{relative}'.");
        }
        var currentLayout = manifest.Files.ContainsKey("twig/SKILL.md") && manifest.Files.ContainsKey(SelectionPath)
            && !manifest.Files.Keys.Any(IsLegacyPath);
        var legacyLayout = manifest.Files.ContainsKey("twig-cli/SKILL.md")
            && manifest.Files.ContainsKey("twig-changes/SKILL.md")
            && manifest.Files.ContainsKey(LegacySelectionPath)
            && !manifest.Files.Keys.Any(path => path.StartsWith("twig/", StringComparison.Ordinal));
        if (currentLayout == legacyLayout)
            throw new SkillLifecycleException("Twig manifest has an incomplete or mixed skill-family layout; preserve it and restore from a trusted backup.");
        manifest.Externals ??= new(StringComparer.Ordinal);
        foreach (var (name, external) in manifest.Externals)
        {
            SkillPaths.ValidateExternalName(name);
            if (external is null || external.Files is null || string.IsNullOrWhiteSpace(external.SourceIdentity))
                throw new SkillLifecycleException($"Invalid external '{name}' record in Twig manifest.");
            if (external.SourceIdentity.Length < 7 || external.SourceIdentity[..7] != "sha256:")
                throw new SkillLifecycleException($"Invalid external '{name}' source identity.");
            foreach (var (relative, hash) in external.Files)
            {
                SkillPaths.ValidateExternalRelative(name, relative);
                if (hash is null || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
                    throw new SkillLifecycleException($"Invalid external hash for '{name}/{relative}'.");
            }
            if (!external.Files.ContainsKey("SKILL.md"))
                throw new SkillLifecycleException($"External '{name}' is missing SKILL.md.");
            if (external.UnixExecuteModes is { } modes
                && (modes.Count != external.Files.Count || modes.Any(p => !external.Files.ContainsKey(p.Key) || (p.Value & ~(int)ExecuteBits) != 0)))
                throw new SkillLifecycleException($"Invalid external '{name}' executable modes in Twig manifest.");
        }
        return manifest;
    }
    private void VerifyManaged(string target, SkillManifest manifest)
    {
        if (manifest.PackageIdentity == package.Identity)
        {
            var shipped = package.GetFiles();
            if (manifest.Files.Count != shipped.Count + 1 || shipped.Any(p => !manifest.Files.TryGetValue(p.Key, out var hash) || hash != SkillPackage.Hash(p.Value)))
                throw new SkillLifecycleException("Twig manifest does not match its claimed bundled package. Preserve it and restore from a trusted backup or install into a new target.");
        }
        foreach (var (relative, expected) in manifest.Files)
        {
            var path = SkillPaths.Under(target, relative);
            if (!File.Exists(path)) throw new SkillLifecycleException($"Managed file is missing: {relative}. Restore it from the matching package/backup or install into a new target; no files changed.");
            if (!string.Equals(SkillPackage.Hash(File.ReadAllBytes(path)), expected, StringComparison.OrdinalIgnoreCase))
                throw new SkillLifecycleException($"Managed file was edited: {relative}. Preserve the edit as a separately named companion, then restore the original bytes or use a new target. No files changed; no force overwrite is available.");
        }
        // Base operations preserve external edits. Result reports executable-mode drift
        // without blocking a family update; only re-add verifies external byte integrity.
    }

    private static void VerifyExternalFiles(string target, string name, SkillExternal record)
    {
        // Byte and mode integrity are separate: a byte-identical script can be unusable.
        foreach (var (relative, expected) in record.Files)
        {
            var path = SkillPaths.Under(target, name + "/" + relative);
            if (!File.Exists(path))
                throw new SkillLifecycleException($"External '{name}' file is missing: {relative}. Restore the tracked bytes from a trusted backup or choose a fresh target before re-adding; no files changed.");
            if (!string.Equals(SkillPackage.Hash(File.ReadAllBytes(path)), expected, StringComparison.OrdinalIgnoreCase))
                throw new SkillLifecycleException($"External '{name}' file was edited on disk: {relative}. Preserve your edit as a separate skill or restore the tracked bytes before re-adding; base install/update never touches externals, but re-add refuses to overwrite an edited external.");
        }
        var modeWarning = OperatingSystem.IsWindows() ? null : ExternalModeWarnings(target, name, record).FirstOrDefault();
        if (modeWarning is not null) throw new SkillLifecycleException(modeWarning);
    }

    private static IEnumerable<string> ExternalModeWarnings(string target, string name, SkillExternal record)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return $"External '{name}': Unix executable modes are not verified on Windows; script execution depends on the installed interpreter and Windows permissions.";
            yield break;
        }
        if (record.UnixExecuteModes is null)
        {
            yield return $"External '{name}' has no recorded Unix executable modes (legacy/Windows install). Preserve it and add the trusted source into a fresh target to establish mode tracking.";
            yield break;
        }
        foreach (var (relative, expected) in record.UnixExecuteModes)
        {
            var path = SkillPaths.Under(target, name + "/" + relative);
            if (!File.Exists(path) || (File.GetUnixFileMode(path) & (ExecuteBits | PrivilegedBits)) != (UnixFileMode)expected)
                yield return $"External '{name}' executable mode changed or file is missing: {relative}. Restore the trusted executable mode and remove privileged bits before re-adding; base updates preserve external files.";
        }
    }

    private static SkillSelections ReadSelections(string target, SkillManifest manifest)
    {
        try
        {
            var value = JsonSerializer.Deserialize(File.ReadAllBytes(SkillPaths.Under(target, SelectionPathFor(manifest))), SkillJsonContext.Default.SkillSelections);
            if (value is null || value.SchemaVersion != 1 || value.Selections is null)
                throw new SkillLifecycleException("Invalid Twig selection reference; restore a trusted backup.");
            foreach (var (provider, scenarios) in value.Selections)
            {
                Provider(provider);
                if (scenarios is null) throw new SkillLifecycleException("Invalid scenario selections.");
                foreach (var (scenario, companion) in scenarios)
                {
                    ValidateName(scenario, "scenario");
                    ValidateName(companion, "companion");
                }
            }
            return value;
        }
        catch (JsonException e) { throw new SkillLifecycleException($"Invalid Twig selection reference: {e.Message}"); }
    }

    private static string SelectionPathFor(SkillManifest manifest) =>
        manifest.Files.ContainsKey(SelectionPath) ? SelectionPath : LegacySelectionPath;

    private static bool IsLegacyManifest(SkillManifest manifest) =>
        manifest.Files.ContainsKey(LegacySelectionPath);

    private static bool IsLegacyPath(string path) => SkillPaths.LegacyFamily.Any(
        name => path.StartsWith(name + "/", StringComparison.Ordinal));

    private static void DeleteEmptyLegacyDirectories(string target)
    {
        foreach (var name in SkillPaths.LegacyFamily)
        {
            var root = SkillPaths.Under(target, name);
            var references = SkillPaths.Under(root, "references");
            if (Directory.Exists(references) && !Directory.EnumerateFileSystemEntries(references).Any())
                Directory.Delete(references);
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
    }

    private static List<(string Name, string Path)> Discover(string target, string? scanRoot)
    {
        var result = new List<(string, string)>();
        var roots = new[] { target, scanRoot is null ? target : SkillPaths.Target(scanRoot) };
        foreach (var root in roots.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                // Never follow discovery links, even for user companions.
                SkillPaths.RejectLinks(directory);
                var path = SkillPaths.Under(directory, "SKILL.md");
                if (!File.Exists(path)) continue;
                using var reader = File.OpenText(path);
                var name = SkillFrontmatter.ReadName(reader);
                if (name is not null)
                {
                    ValidateName(name, "discovered skill");
                    result.Add((name, path));
                }
            }
        }
        return result;
    }

    internal static string Provider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider) || provider is not ("hermes" or "omp" or "copilot"))
            throw new SkillLifecycleException("Select --provider hermes|omp|copilot and an explicit --target skills root. The provider need not be installed when preparing a custom target; configure its discovery separately.");
        return provider;
    }

    internal static string ProviderHint(string provider) => Provider(provider) switch
    {
        "hermes" => "Hermes: choose the intended profile's skills directory explicitly, or register this root via skills.external_dirs. Project skills require trust. Reload skills/start a new session; Twig does not edit config.yaml.",
        "omp" => "OMP: flat sibling skills are prepared for a native skills root or skills.customDirectories. Register custom targets and reload/start a new session yourself; Twig does not edit provider settings.",
        _ => "Copilot CLI: choose an explicit personal/repository skills root or register a custom skill root. Reload/start a new session and verify discovery; Twig does not edit provider settings or permissions."
    };

    private static void ValidateScanRoot(string? scanRoot) { if (scanRoot is not null) SkillPaths.Target(scanRoot); }
    private static bool IsName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 64
        && name[0] != '-' && name[^1] != '-' && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    private static void ValidateName(string? name, string kind)
    {
        if (!IsName(name)) throw new SkillLifecycleException($"Invalid {kind} name. Use 1-64 lowercase letters, digits or internal hyphens.");
    }

    private static bool SameManifest(SkillManifest? a, SkillManifest? b) => a is null ? b is null
        : b is not null && a.PackageIdentity == b.PackageIdentity && a.Provider == b.Provider
        && a.Files.Count == b.Files.Count && a.Files.All(p => b.Files.TryGetValue(p.Key, out var hash) && p.Value == hash)
        && a.Externals.Count == b.Externals.Count
        && a.Externals.All(p => b.Externals.TryGetValue(p.Key, out var external)
            && p.Value.SourceIdentity == external.SourceIdentity
            && p.Value.Files.Count == external.Files.Count
            && p.Value.Files.All(file => external.Files.TryGetValue(file.Key, out var hash) && file.Value == hash)
            && (p.Value.UnixExecuteModes is null ? external.UnixExecuteModes is null
                : external.UnixExecuteModes is { } modes && p.Value.UnixExecuteModes.Count == modes.Count
                    && p.Value.UnixExecuteModes.All(file => modes.TryGetValue(file.Key, out var mode) && file.Value == mode)));

    private static void CheckParentDirectories(string path, string target)
    {
        for (var parent = Path.GetDirectoryName(path); parent is not null && !parent.Equals(target, SkillPaths.PathComparison); parent = Path.GetDirectoryName(parent))
            if (File.Exists(parent)) throw new SkillLifecycleException($"A file blocks the managed directory: {parent}.");
    }

    private static FileStream AcquireLock(string target)
    {
        var path = SkillPaths.Under(target, LockPath);
        try { return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException) { throw new SkillLifecycleException($"Twig skills lock exists or cannot be acquired at {path}. Wait for the other operation; if interrupted, inspect status and remove only that stale lock before retrying."); }
    }

    private static void AtomicWrite(string root, string relative, byte[] bytes, bool overwrite, UnixFileMode? mode = null)
    {
        var path = SkillPaths.Under(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".twig-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows() && mode is { } unixMode)
                File.SetUnixFileMode(temporary, unixMode & OrdinaryBits);
            SkillPaths.RejectLinks(path);
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
