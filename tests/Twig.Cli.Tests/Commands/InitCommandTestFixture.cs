using System.Diagnostics;
using Twig.Commands;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Shared fixture helpers for AB#728 <c>twig init</c> tests. Post-#728 the
/// command validates the git worktree root and requires a system-store
/// registry plus a deterministic profile-registry source; every fixture
/// wants exactly the same wiring, so the helpers live here rather than
/// being copied per suite.
/// </summary>
internal static class InitCommandTestFixture
{
    public const string TestProfileIdentity = "Test.SelectedProfile";
    public const string TestProfileVersion = "1.0";
    public static readonly IReadOnlyList<string> TestPrimaryScopeTypes = new[] { "Bug", "Task" };

    public static bool InitTempWorktree(string workDir)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("git", "init -q")
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return false;
            proc.WaitForExit(5_000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static (SqliteSystemWorktreeRegistry Registry, IProfileRegistrySource ProfileRegistry) CreateSeams(
        string tempRoot,
        IProfileRegistrySource? profileRegistryOverride = null)
    {
        var systemDbPath = Path.Combine(tempRoot, "system.db");
        var registry = new SqliteSystemWorktreeRegistry(systemDbPath, TimeProvider.System);
        var profileRegistry = profileRegistryOverride
            ?? new StaticProfileRegistrySource(TestProfileIdentity, TestProfileVersion, TestPrimaryScopeTypes);
        return (registry, profileRegistry);
    }

    /// <summary>
    /// Constructs an <see cref="InitCommand"/> for tests, injecting the
    /// AB#728 §6.3 managed-init seams (system registry + deterministic
    /// profile registry). The trailing (optional) parameters mirror the
    /// pre-#728 test constructor so a fixture-level positional call
    /// forwards through unchanged.
    /// </summary>
    public static InitCommand CreateInitCommand(
        ISystemWorktreeRegistry systemRegistry,
        IProfileRegistrySource profileRegistry,
        IIterationService iterationService,
        TwigPaths paths,
        OutputFormatterFactory formatterFactory,
        HintEngine hintEngine,
        IGlobalProfileStore? globalProfileStore = null,
        IConsoleInput? consoleInput = null,
        ITelemetryClient? telemetryClient = null)
        => new InitCommand(iterationService, paths, formatterFactory, hintEngine,
            globalProfileStore, consoleInput, telemetryClient,
            systemRegistry, profileRegistry);
}

/// <summary>
/// Deterministic <see cref="IProfileRegistrySource"/> for tests: yields a
/// fixed profile identity/version with an explicit non-empty
/// primary-scope allow-set. AB#728 §6.3 requires the initializer to
/// receive a materialized policy rather than synthesizing one, so tests
/// bind this explicitly rather than relying on a checked-in twig.json.
/// </summary>
internal sealed class StaticProfileRegistrySource : IProfileRegistrySource
{
    private readonly string _identity;
    private readonly string _version;
    private readonly IReadOnlyList<string> _primaryScopeTypes;

    public StaticProfileRegistrySource(string identity, string version, IReadOnlyList<string> primaryScopeTypes)
    {
        _identity = identity;
        _version = version;
        _primaryScopeTypes = primaryScopeTypes;
    }

    public Result<SelectedProfileMaterialization> Resolve(string processTemplate)
    {
        _ = processTemplate;
        return Result.Ok(new SelectedProfileMaterialization(_identity, _version, _primaryScopeTypes));
    }
}
