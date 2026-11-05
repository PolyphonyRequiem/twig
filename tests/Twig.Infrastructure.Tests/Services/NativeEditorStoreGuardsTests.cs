using System.Collections.Immutable;
using System.Threading;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Infrastructure.Tests.Services;

public sealed class NativeEditorStoreGuardsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-editor-lease-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("save")]
    [InlineData("delete")]
    [InlineData("capture")]
    [InlineData("observe")]
    public async Task EveryNativeBenchMutationHonorsTheExistingCanonicalAuthoringLease(string route)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "benches.db");
        var user = new UserBenchStore(path);
        var connection = new Connection(new("ado"), "collection", "project", "actor");
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Guarded", []), 0)).Value;
        var key = "native-bench:" + (OperatingSystem.IsWindows() ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path))
            + ":" + bench.Id.ToString("D");
        using var held = ConnectionOperationGate.AcquireCoreTargets(path, [key]);
        Task<Result<bool>> foreign;
        using (ExecutionContext.SuppressFlow())
            foreign = Task.Run(async () =>
            {
                var store = new UserBenchStore(path);
                var changed = new Bench(bench.Id, "Guarded",
                    [new(SelectorKind.Pin, ItemKey.Remote(connection.Provider, connection.ResourceScope, "42"), connection: connection)], bench.Revision);
                if (route == "capture") return await store.CaptureAsync(connection, [bench], [changed], default);
                if (route == "observe") return await store.ObserveAsync(root, connection, Guid.NewGuid(), [changed], default);
                if (route == "delete") return await store.DeleteAsync(bench.Id, bench.Revision);
                var saved = await store.SaveAsync(new(bench.Id, "Changed", []), bench.Revision);
                return saved.IsSuccess ? Result<bool>.Ok(true) : Result<bool>.Fail(saved.Error);
            });
        var refused = await foreign;
        refused.IsSuccess.ShouldBeFalse();
        refused.Error.Code.ShouldBe(ErrorCode.Conflict);
        (await user.ReadAsync()).Value.Benches.Single().Revision.ShouldBe(bench.Revision);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
