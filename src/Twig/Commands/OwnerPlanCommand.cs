using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;

namespace Twig.Commands;

internal sealed record OwnerPlanReply(string Digest, string Status, PlanCaptureUnit[] Units, string? Detail);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OwnerPlanReply))]
internal sealed partial class OwnerPlanJson : JsonSerializerContext;

internal static class OwnerPlanCommand
{
    internal static async Task<int> ExecuteAsync(string home, UserBenchStore user, string directory, string[] args,
        TextWriter output, TextWriter errors, IOwnerBenchPublication? publication, CancellationToken ct)
    {
        try
        {
            var root = Path.Combine(Path.GetDirectoryName(home)!, "work");
            if (!OwnerFixtureSelection.Same(root, directory)
                || args is not ["plan", var verb, var digest, "--bench", var bench, "-o", "json"]
                || verb is not ("observe" or "continue" or "abandon-unadmitted") || !Guid.TryParseExact(bench, "D", out var id))
                throw new InvalidDataException("Use an original digest and exact Bench in the qualification workspace.");
            var retained = Require(await user.ReadPlanReviewAsync(digest, ct));
            if (retained.Document.BenchId != id) throw new InvalidDataException("Original Plan belongs to a different Bench.");
            var plan = Require(await user.ReadPlanAsync(id, ct));
            var paths = TwigPaths.BuildPaths(Path.Combine(root, ".twig"), new TwigConfiguration(), root);
            var marker = Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName);
            var database = Path.Combine(paths.TwigDir, "core.db");
            foreach (var path in new[] { root, paths.TwigDir, marker, database })
                RejectLink(path);
            using var pin = File.Open(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = await JsonSerializer.DeserializeAsync(pin, CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument, ct);
            if (!OwnerFixtureSelection.Same(document?.DatabasePath, database) || document!.Connection.Provider.Value != "twig")
                throw new InvalidDataException("The original Plan must belong to the selected local authority.");
            var config = await TwigConfiguration.LoadSplitAsync(paths, ct);
            if (config.Provider != "twig" || config.NativeDatabase != "qualification")
                throw new InvalidDataException("Only the isolated Twig qualification authority is admitted.");
            var services = new ServiceCollection();
            services.AddCoreWorkspaceServices(config, paths, executionOwner: true);
            using var runtime = services.BuildServiceProvider();
            using var handle = Require(await runtime.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(ct));
            if (!OwnerFixtureSelection.Same(handle.Document.DatabasePath, Path.Combine(paths.TwigDir, "core.db"))
                || retained.Document.PlanId != plan.Id)
                throw new InvalidDataException("Original Plan authority changed.");
            foreach (var entry in retained.Document.Scopes)
            {
                var scope = entry.Scope;
                var folder = Path.Combine(home, "plan-scopes", plan.Id.ToString("D"));
                if (!plan.Scopes.Contains(scope) || scope.Source != handle.Source || !OwnerFixtureSelection.Same(scope.OriginRoot, root)
                    || !OwnerFixtureSelection.Same(scope.DatabasePath, Path.Combine(folder, scope.Id.ToString("D") + ".db")))
                    throw new InvalidDataException("Original Plan scope is not owned by the selected fixture.");
                RejectLink(Path.GetDirectoryName(folder)!);
                RejectLink(folder);
                RejectLink(scope.DatabasePath);
            }
            var review = Require(await NativePlanReview.OpenAsync(root, digest, ct));
            var outcome = verb == "abandon-unadmitted" ? Require(await review.AbandonUnadmittedAsync(ct)) : Require(await review.ObserveAsync(ct));
            if (verb == "continue" && outcome.Units.Any(unit => unit.State is not ("Unadmitted" or "Abandoned")))
            {
                if (publication is null) throw new InvalidDataException("Scoped settlement requires the original owner publication ledger.");
                outcome = Require(await publication.ContinuePlanAsync(root, handle.Source, user, review, ct));
            }
            output.WriteLine(JsonSerializer.Serialize(new OwnerPlanReply(digest, outcome.Status, outcome.Units, outcome.Error?.Message),
                OwnerPlanJson.Default.OwnerPlanReply));
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            errors.WriteLine(ex.Message);
            return 2;
        }
    }

    private static void RejectLink(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Qualification paths cannot redirect Plan authority.");
    }

    private static T Require<T>(Core.Result<T> result) => result.IsSuccess ? result.Value : throw new InvalidDataException(result.Error.Message);
}
