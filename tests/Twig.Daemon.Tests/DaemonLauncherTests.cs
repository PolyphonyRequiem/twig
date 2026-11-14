using Bench.Server;
using Shouldly;
using Xunit;

namespace Twig.Daemon.Tests;

public sealed class DaemonLauncherTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-launch-" + Guid.NewGuid().ToString("N"));
    public DaemonLauncherTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Healthy_daemon_is_reused_without_lock_or_process_changes()
    {
        await DaemonLauncher.EnsureWithProbeAsync(root, () => Task.FromResult(true),
            () => throw new InvalidOperationException("Must not start"), default);
        Directory.GetFiles(root).ShouldBeEmpty();
    }

    [Fact]
    public async Task Concurrent_clients_launch_only_once_and_recheck_after_the_lease()
    {
        var running = false;
        var starts = 0;
        async Task<bool> Probe() { await Task.Yield(); return Volatile.Read(ref running); }
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            DaemonLauncher.EnsureWithProbeAsync(root, Probe, () =>
            {
                Interlocked.Increment(ref starts);
                Thread.Sleep(40);
                Volatile.Write(ref running, true);
            }, default))));
        starts.ShouldBe(1);
    }

    [Fact]
    public async Task Unavailable_but_locked_daemon_is_not_restarted()
    {
        using var instance = new FileStream(Path.Combine(root, "daemon.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var starts = 0;
        await Should.ThrowAsync<InvalidOperationException>(() => DaemonLauncher.EnsureWithProbeAsync(
            root, () => Task.FromResult(false), () => starts++, default));
        starts.ShouldBe(0);
    }

    [Fact]
    public async Task Identity_authentication_or_TLS_refusal_never_falls_back_to_start()
    {
        var starts = 0;
        await Should.ThrowAsync<InvalidOperationException>(() => DaemonLauncher.EnsureWithProbeAsync(root,
            () => throw new InvalidOperationException("Wrong owner, certificate or daemon identity"),
            () => starts++, default));
        starts.ShouldBe(0);
        Directory.GetFiles(root).ShouldBeEmpty();
    }

    [Fact]
    public async Task Uncertain_start_readback_is_never_replayed()
    {
        var starts = 0;
        await Should.ThrowAsync<InvalidOperationException>(() => DaemonLauncher.EnsureWithProbeAsync(root,
            () => starts == 0 ? Task.FromResult(false) : throw new InvalidOperationException("Identity refused"),
            () => starts++, default));
        starts.ShouldBe(1);
    }

    [Fact]
    public async Task Readiness_cancellation_does_not_restart_or_hold_the_launch_lease()
    {
        var starts = 0;
        using var deadline = new CancellationTokenSource();
        await Should.ThrowAsync<OperationCanceledException>(() => DaemonLauncher.EnsureWithProbeAsync(root,
            () => Task.FromResult(false), () => { starts++; deadline.Cancel(); }, deadline.Token));
        starts.ShouldBe(1);
        using var lease = new FileStream(Path.Combine(root, "launch.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    public void Dispose() => Directory.Delete(root, true);
}
