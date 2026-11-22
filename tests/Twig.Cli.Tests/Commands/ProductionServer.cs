using System.Diagnostics;
using System.Text.Json;

namespace Twig.Cli.Tests.Commands;

/// <summary>Give a subprocess test its own server, with the same fictional home and proxy as the CLI.</summary>
internal sealed class ProductionServer(string root, Process process) : IAsyncDisposable
{
    internal static async Task<ProductionServer> StartAsync(ProcessStartInfo cli)
    {
        var fixtureParent = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : Path.GetTempPath();
        var root = Path.Combine(fixtureParent, $"twig-cli-server-test-{Guid.NewGuid():N}");
        var data = Path.Combine(root, "data");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
        else
        {
            const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(root, owner);
        }
        Process? process = null;
        try
        {
            await using (var file = File.Create(Path.Combine(root, "current.json")))
            using (var writer = new Utf8JsonWriter(file))
            {
                writer.WriteStartObject();
                writer.WriteString("privateDataDir", data);
                writer.WriteEndObject();
            }
            cli.Environment["TWIG_SERVER_INSTALL_ROOT"] = root;
            if (!cli.Environment.ContainsKey("TWIG_USER_HOME")) cli.Environment["TWIG_USER_HOME"] = Path.Combine(root, "home");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = root, UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            start.Environment.Clear();
            foreach (var (key, value) in cli.Environment) start.Environment[key] = value;
            var output = new DirectoryInfo(AppContext.BaseDirectory);
            var repo = output;
            while (repo != null && !File.Exists(Path.Combine(repo.FullName, "Twig.slnx"))) repo = repo.Parent;
            if (repo == null) throw new InvalidOperationException("Cannot locate built server for the CLI test.");
            start.ArgumentList.Add(Path.Combine(repo.FullName, "src", "Twig.Server", "bin", output.Parent!.Name, output.Name, "Twig.Server.dll"));
            start.ArgumentList.Add("--commands-only");
            start.ArgumentList.Add("--data-dir");
            start.ArgumentList.Add(data);
            process = Process.Start(start) ?? throw new InvalidOperationException("Fixture server did not start.");
            if (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) != "COMMANDS_READY")
                throw new InvalidOperationException("Fixture server did not confirm readiness.");
            return new ProductionServer(root, process);
        }
        catch
        {
            if (process != null)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                process.Dispose();
            }
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("stop");
                await process.StandardInput.FlushAsync();
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (process.ExitCode != 0) throw new InvalidOperationException(await process.StandardError.ReadToEndAsync());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
