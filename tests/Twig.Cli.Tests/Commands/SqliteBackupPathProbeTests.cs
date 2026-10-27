using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Cli.Tests.TestSupport;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>Opt-in host diagnostic; expected native failures are not portable regression guarantees.</summary>
[Trait("Category", "Integration")]
public sealed class SqliteBackupPathProbeTests
{
    [Fact]
    public void DefaultNativeOpenDiffersOnlyByBackupDigestDirectoryLength()
    {
        SQLitePCL.Batteries.Init();
        var root = CanonicalTempRoot.Create("twig-browser-read-");
        var workspace = Path.Combine(root, "checkout", ".twig");
        var backups = Path.Combine(workspace, "backups");
        var snapshot = "snapshot-" + Guid.NewGuid().ToString("N");
        var longDirectory = Path.Combine(backups, "preservation-" + new string('a', 64), snapshot);
        var shortDirectory = Path.Combine(backups, "preservation-a", snapshot);
        Directory.CreateDirectory(longDirectory);
        Directory.CreateDirectory(shortDirectory);
        var longPath = Path.Combine(longDirectory, "mirror.db");
        var shortPath = Path.Combine(shortDirectory, "mirror.db");
        _ = ValidateLogicalPath(workspace, longPath);
        _ = ValidateLogicalPath(workspace, shortPath);
        var evidence = new List<string>
        {
            $"root={root}",
            $"provider={typeof(SqliteConnection).Assembly.Location}",
            $"long-directory-exists={Directory.Exists(longDirectory)}",
            $"short-directory-exists={Directory.Exists(shortDirectory)}",
            "mode=ReadWriteCreate;pooling=false;vfs=default;uri-override=none",
        };

        var shortResult = Open(shortPath, evidence);
        var longResult = Open(longPath, evidence);
        evidence.Add($"short-file-exists={File.Exists(shortPath)}");
        evidence.Add($"long-file-exists={File.Exists(longPath)}");
        var report = Path.Combine(root, "sqlite-open-evidence.txt");
        File.WriteAllLines(report, evidence);
        foreach (var line in evidence) Console.WriteLine(line);
        Console.WriteLine($"retained-evidence={report}");

        shortResult.Error.ShouldBe(0);
        File.Exists(shortPath).ShouldBeTrue();
        if (OperatingSystem.IsWindows() && longPath.Length >= 260 && shortPath.Length < 260)
            longResult.Error.ShouldBe(14, "This diagnostic must actually reproduce SQLite14, not merely fail elsewhere.");
        else
            longResult.Error.ShouldBe(0, "The path-length hypothesis was not reproduced on this host/layout.");

        if (OperatingSystem.IsWindows() && longPath.Length >= 260 && shortPath.Length < 260)
        {
            try
            {
                QualifyWindowsLongPathVfs(workspace, longPath, evidence);
                evidence.Add("windows-longpath-vfs-qualified=true");
            }
            finally
            {
                File.WriteAllLines(report, evidence);
            }
        }
        else
        {
            evidence.Add("windows-longpath-vfs-qualified=not-tested;target-path-length-condition-not-reproduced");
            File.WriteAllLines(report, evidence);
            Console.WriteLine(evidence[^1]);
        }
    }

    private static void QualifyWindowsLongPathVfs(string workspace, string sourcePath, List<string> evidence)
    {
        var plain = Open(sourcePath, evidence, "win32-longpath");
        plain.Error.ShouldBe(14, "The plain-path control must reproduce the independently observed failure.");
        using var source = OpenLongPath(workspace, sourcePath, SqliteOpenMode.ReadWriteCreate, evidence);
        File.Exists(sourcePath).ShouldBeTrue("The native spelling must address the original logical path.");
        using (var seed = source.CreateCommand())
        {
            seed.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE retained(id INTEGER PRIMARY KEY,value TEXT);
                INSERT INTO retained(id,value) VALUES(1,NULL),(2,''),(3,'retained');
                """;
            seed.ExecuteNonQuery();
        }
        var backupDirectory = Path.Combine(Path.GetDirectoryName(sourcePath)!, "restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, "mirror.db");
        using (var backup = OpenLongPath(workspace, backupPath, SqliteOpenMode.ReadWriteCreate, evidence))
            source.BackupDatabase(backup);
        using var readback = OpenLongPath(workspace, backupPath, SqliteOpenMode.ReadOnly, evidence);
        VerifyValues(readback);

        var restoredPath = Path.Combine(Path.GetDirectoryName(sourcePath)!, "again_.db");
        using (var restored = OpenLongPath(workspace, restoredPath, SqliteOpenMode.ReadWriteCreate, evidence))
            readback.BackupDatabase(restored);
        using var restoredReadback = OpenLongPath(workspace, restoredPath, SqliteOpenMode.ReadOnly, evidence);
        VerifyValues(restoredReadback);
        evidence.Add($"native-backup-restore=passed;source-length={sourcePath.Length};backup-length={backupPath.Length};restore-length={restoredPath.Length}");
    }

    private static SqliteConnection OpenLongPath(
        string workspace, string path, SqliteOpenMode mode, List<string> evidence)
    {
        var absolute = ValidateLogicalPath(workspace, path);
        var native = absolute.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? absolute
            : absolute.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + absolute[2..]
                : @"\\?\" + absolute;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = native, Mode = mode, Pooling = false, Vfs = "win32-longpath",
        }.ToString());
        evidence.Add($"logical-path={path};native-path={native};uri-override=none");
        try
        {
            connection.Open();
            evidence.Add($"vfs=win32-longpath;mode={mode};length={path.Length};opened=true;path={path}");
            return connection;
        }

        catch (SqliteException error)
        {
            evidence.Add($"vfs=win32-longpath;mode={mode};length={path.Length};opened=false;error={error.SqliteErrorCode};extended={error.SqliteExtendedErrorCode}");
            connection.Dispose();
            throw;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static string ValidateLogicalPath(string workspace, string path)
    {
        var root = Path.GetFullPath(workspace);
        var absolute = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, absolute);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("The diagnostic path must remain in its owned logical workspace.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var current = absolute; ; current = Path.GetDirectoryName(current)
            ?? throw new InvalidOperationException("The diagnostic workspace containment could not be verified."))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A diagnostic path cannot traverse a symbolic link or junction.");
            if (string.Equals(current, root, comparison)) break;
        }
        return absolute;
    }

    private static void VerifyValues(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM retained ORDER BY id;";
        using var reader = command.ExecuteReader();
        reader.Read().ShouldBeTrue();
        reader.IsDBNull(0).ShouldBeTrue();
        reader.Read().ShouldBeTrue();
        reader.GetString(0).ShouldBe("");
        reader.Read().ShouldBeTrue();
        reader.GetString(0).ShouldBe("retained");
        reader.Read().ShouldBeFalse();
    }

    private static (int Error, int Extended) Open(string path, List<string> evidence, string? vfs = null)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        if (vfs is not null) builder.Vfs = vfs;
        using var connection = new SqliteConnection(builder.ToString());
        evidence.Add($"requested={path};resolved-data-source={connection.DataSource}");
        try
        {
            connection.Open();
            using var version = connection.CreateCommand();
            version.CommandText = "SELECT sqlite_version();";
            evidence.Add($"path={path};length={path.Length};opened=true;error=0;sqlite={version.ExecuteScalar()}");
            return (0, 0);
        }
        catch (SqliteException error)
        {
            evidence.Add($"path={path};length={path.Length};opened=false;error={error.SqliteErrorCode};extended={error.SqliteExtendedErrorCode};message={error.Message}");
            return (error.SqliteErrorCode, error.SqliteExtendedErrorCode);
        }
    }
}
