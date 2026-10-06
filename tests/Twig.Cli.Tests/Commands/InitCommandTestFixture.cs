using System.Diagnostics;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services.ReferenceProfile;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Shared fixture helpers for managed <c>twig init</c> tests. The command
/// validates the git worktree root and requires a system-store registry.
/// Explicit profile selections use the real released-profile registry.
/// </summary>
internal static class InitCommandTestFixture
{

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
            ?? new ReferenceProfileRegistrySource(new EmbeddedReferenceProfileProvider(
                new TwigJsonReferenceProfilePinSource(new TwigConfiguration())));
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

