using Microsoft.Extensions.DependencyInjection;
using Twig.Core;
using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.Services;

/// <summary>Exact one-time native inventory; not per-Save approval or proof of arbitrary historical binaries.</summary>
public sealed class NativeAuthoringReview
{
    private readonly string registry;
    private readonly NativeCoreInventory inventory;
    private NativeAuthoringReview(string registry, NativeCoreInventory inventory)
        => (this.registry, this.inventory) = (registry, inventory);
    public string Digest => inventory.Digest;
    public string Confirmation => NativeInitialAuthoring.Statement(Digest);
    public static string NativeBuild => NativeInitialAuthoring.NativeBuild;
    public static Task<Result<NativeAuthoringReview>> ReviewAsync(string root, string observedReadonlyRelease,
        string observedCliBuild, string panelBuild, CancellationToken ct = default)
        => ReviewAsync(root, observedReadonlyRelease, observedCliBuild, panelBuild, null, ct);
    internal static async Task<Result<NativeAuthoringReview>> ReviewAsync(string root, string observedReadonlyRelease,
        string observedCliBuild, string panelBuild, Action<IServiceCollection>? configure, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(root))
            return Result<NativeAuthoringReview>.Fail(ErrorCode.InvalidInput, "An actual native root is required.");
        var paths = TwigPaths.BuildPaths(Path.Combine(root, ".twig"), new TwigConfiguration(), root);
        var config = await TwigConfiguration.LoadSplitAsync(paths, ct).ConfigureAwait(false);
        var services = new ServiceCollection(); configure?.Invoke(services);
        services.AddCoreWorkspaceServices(config, paths);
        using var runtime = services.BuildServiceProvider();
        var factory = runtime.GetRequiredService<CoreWorkspaceFactory>();
        if (observedReadonlyRelease != NativeInitialAuthoring.ReadOnlyRelease || observedCliBuild != NativeBuild || string.IsNullOrWhiteSpace(panelBuild))
            return Result<NativeAuthoringReview>.Fail(ErrorCode.InvalidInput, "Exact observed supported readonly and paired source build identities are required.");
        var result = await NativeCoreExecution.InventoryAsync(factory.ExecutionRegistryPath, ct: ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            result = await NativeCoreExecution.InventoryAsync(factory.ExecutionRegistryPath, ct: ct,
                initialAuthoring: new(observedReadonlyRelease, observedCliBuild, panelBuild)).ConfigureAwait(false);
        return result.IsSuccess ? Result<NativeAuthoringReview>.Ok(new(factory.ExecutionRegistryPath, result.Value))
            : Result<NativeAuthoringReview>.Fail(result.Error);
    }
    public Task<Result<bool>> ConfirmAsync(string digest, string confirmation, bool completeInventory,
        bool oldProcessesQuiesced, CancellationToken ct = default)
        => confirmation == Confirmation
            ? NativeCoreExecution.ActivateAsync(registry, inventory, digest, completeInventory, oldProcessesQuiesced, ct, confirmation)
            : Task.FromResult(Result<bool>.Fail(ErrorCode.DigestMismatch, "Confirm the exact one-time native inventory statement."));
}
