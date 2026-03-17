using Twig.Infrastructure.Config;

/// <summary>
/// Migrates the legacy flat <c>.twig/twig.db</c> to the multi-context path
/// <c>.twig/{org}/{project}/twig.db</c> on startup.
/// </summary>
internal static class LegacyDbMigrator
{
    /// <summary>
    /// If a legacy <c>.twig/twig.db</c> exists and no nested DB exists for the
    /// current config context, moves it (and WAL/SHM journals) to the context path.
    /// Failures are non-fatal — a warning is written to stderr and the tool continues.
    /// </summary>
    public static void MigrateIfNeeded(string twigDir, TwigConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.Organization) || string.IsNullOrWhiteSpace(config.Project))
            return;

        var legacyDbPath = TwigPaths.GetLegacyDbPath(twigDir);
        if (!File.Exists(legacyDbPath))
            return;

        var contextDbPath = TwigPaths.GetContextDbPath(twigDir, config.Organization, config.Project);
        if (File.Exists(contextDbPath))
            return;

        try
        {
            var contextDir = Path.GetDirectoryName(contextDbPath)!;
            Directory.CreateDirectory(contextDir);

            File.Move(legacyDbPath, contextDbPath);

            // Move WAL/SHM journal files if present
            MoveJournalFile(legacyDbPath + "-wal", contextDbPath + "-wal");
            MoveJournalFile(legacyDbPath + "-shm", contextDbPath + "-shm");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: legacy DB migration failed: {ex.Message}. Run 'twig init --force' to reinitialize.");
        }
    }

    private static void MoveJournalFile(string source, string destination)
    {
        if (File.Exists(source))
            File.Move(source, destination);
    }
}
