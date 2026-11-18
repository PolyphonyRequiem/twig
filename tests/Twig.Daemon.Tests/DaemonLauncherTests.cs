using Bench.Server;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    public async Task Closed_loopback_listener_reaches_the_start_boundary()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        var starts = 0;
        var failure = await Should.ThrowAsync<InvalidOperationException>(() => DaemonLauncher.EnsureRunningAsync(
            root, port, certificate, () => { starts++; throw new InvalidOperationException("Owned start boundary"); }, default));
        failure.Message.ShouldBe("Owned start boundary");
        starts.ShouldBe(1);
    }

    [Fact]
    public async Task Status_on_a_closed_port_does_not_create_any_daemon_state()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        using var key = RSA.Create(2048);
        using var cert = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        (await DaemonLauncher.IsRunningAsync(root, port, cert, default)).ShouldBeFalse();
        Directory.GetFiles(root).ShouldBeEmpty();
        using var held = new FileStream(Path.Combine(root, "daemon.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        await Should.ThrowAsync<InvalidOperationException>(() => DaemonLauncher.IsRunningAsync(root, port, cert, default));
    }

    [Fact]
    public async Task Stop_signals_only_the_verified_run_and_waits_for_instance_release()
    {
        if (!OperatingSystem.IsWindows()) return;
        var selected = Guid.NewGuid();
        using var expected = DaemonLauncher.CreateStopSignal(selected);
        using var foreign = DaemonLauncher.CreateStopSignal(Guid.NewGuid());
        using var held = new FileStream(Path.Combine(root, "daemon.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var probes = 0;
        var stop = DaemonLauncher.StopWithProbeAsync(root,
            () => Task.FromResult<Guid?>(++probes == 1 ? selected : null), timeout.Token);
        expected.WaitOne(TimeSpan.FromSeconds(1)).ShouldBeTrue();
        foreign.WaitOne(0).ShouldBeFalse();
        stop.IsCompleted.ShouldBeFalse();
        held.Dispose();
        await stop;
        probes.ShouldBe(2);
    }

    [Fact]
    public async Task Stop_cannot_signal_any_run_after_failed_identity_verification()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var foreign = DaemonLauncher.CreateStopSignal(Guid.NewGuid());
        var stop = DaemonLauncher.StopWithProbeAsync(root, () => throw new InvalidOperationException("Wrong listener"), default);
        await Should.ThrowAsync<InvalidOperationException>(stop);
        foreign.WaitOne(0).ShouldBeFalse();
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
