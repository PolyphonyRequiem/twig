using Bench.Infrastructure;

namespace Bench.Server;

public sealed class NativeMetadataSync(NativePanel panel, ILogger<NativeMetadataSync> logger, TimeProvider? time = null) : BackgroundService
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        try
        {
            do { await RefreshAsync(stoppingToken).ConfigureAwait(false); }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var workspaces = await panel.WorkspacesAsync(ct).ConfigureAwait(false);
        if (!workspaces.IsSuccess)
        {
            logger.LogWarning("Cannot enumerate registered connections for metadata refresh: {ErrorCode}.", workspaces.Error.Code);
            return;
        }
        foreach (var workspace in workspaces.Value)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(35));
            try
            {
                var result = await panel.RefreshMetadataAsync(workspace, clock.GetUtcNow(), budget.Token).ConfigureAwait(false);
                if (!result.IsSuccess)
                    logger.LogWarning("Scheduled connection metadata refresh failed: {ErrorCode}.", result.Error.Code);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { logger.LogWarning("Scheduled connection metadata refresh exceeded its deadline."); }
        }
    }
}
