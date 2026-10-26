using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;

namespace Twig.Infrastructure.Tests.GitHub;

internal sealed class GitHubTargetFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "twig-github-target-" + Guid.NewGuid().ToString("N"));
    public TwigPaths Paths { get; }
    public string SystemDbPath => Path.Combine(Root, "system.db");
    public SqliteSystemWorktreeRegistry Registry { get; }
    public WorktreeLocalProviderTargetAttachmentStore Attachment { get; }
    public static GitHubRepositoryTarget Target { get; } = new("O_owner", "R_repo");

    public GitHubTargetFixture()
    {
        Directory.CreateDirectory(Root);
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        start.ArgumentList.Add("init");
        start.ArgumentList.Add("--quiet");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git fixture did not start.");
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000).ShouldBeTrue();
            throw new TimeoutException("Git fixture initialization timed out.");
        }
        process.ExitCode.ShouldBe(0);
        var twig = Path.Combine(Root, ".twig");
        Paths = new(twig, Path.Combine(twig, "config"), Path.Combine(twig, "twig.db"), Root);
        Registry = new(SystemDbPath, TimeProvider.System);
        Attachment = new(Paths, Registry, TimeProvider.System);
    }

    public async Task BindAsync()
    {
        var result = await new ProviderTargetAttachmentService(Registry, Attachment).AttachAsync(
            Target.ToIdentity(new("https://api.github.com/graphql")), "original/repo", "Original", 0);
        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    public void Register(IServiceCollection services)
    {
        services.AddSingleton(Paths);
        services.AddSingleton<IProviderTargetBindingStore>(Registry);
        services.AddSingleton<IProviderTargetAttachmentStore>(Attachment);
    }

    public void Dispose()
    {
        Attachment.Dispose();
        Registry.Dispose();
        using var pool = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = SystemDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString());
        SqliteConnection.ClearPool(pool);
        Directory.Delete(Root, recursive: true);
    }
}
