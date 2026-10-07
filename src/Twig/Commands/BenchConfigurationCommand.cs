using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Reads and edits only the captured Bench's durable automatic selectors.</summary>
internal sealed class BenchConfigurationCommand(CommandContext ctx, CurrentBenchResolver resolver,
    DefaultBenchSelectors defaults, IBenchRepository benches, IWorkItemRepository repository,
    IAuthenticationProvider authentication, IConnectionBindingService bindings, TwigPaths paths,
    RendererFactory renderers)
{
    internal async Task<int> ExecuteAsync(string? section = null, string? action = null, string? value = null,
        bool exact = false, string output = OutputFormatterFactory.DefaultFormat,
        string? expectBench = null, string? expectBinding = null, string? expectIdentity = null,
        string? expectSettings = null, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        var fmt = ctx.FormatterFactory.GetFormatter(output);
        try
        {
            using var admission = await ConnectionOperationAdmission.AcquireAsync(authentication, ct);
            if (admission is null) throw new InvalidOperationException("Bench configuration requires an admitted native connection.");
            using var operation = repository.AcquireOperation();
            var binding = await bindings.ResolveAsync(ctx.Config, paths, ct);
            BrowserOriginGuard.EnsureExpected(binding, expectBinding, expectIdentity);
            var (effective, stored) = await resolver.ResolveCapturedAsync(ct, expectBench);
            var digest = BenchQueryRule.SettingsDigest(stored.Selectors);
            if (expectSettings is not null && !string.Equals(expectSettings, digest, StringComparison.Ordinal))
                throw new InvalidOperationException("Automatic settings changed since this form opened. Refresh and retry; no settings were changed.");
            var rules = effective.Selectors.Where(s => s.Kind == SelectorKind.Query).Select(BenchQueryRule.Parse).ToList();
            if (section is not null)
            {
                if (section is not ("area" or "sprint") || action is not ("add" or "remove"))
                    throw new ArgumentException("Expected area or sprint add/remove.");
                AreaPathFilter? area = null;
                IterationExpression? sprint = null;
                if (section == "area")
                {
                    var parsed = AreaPath.Parse(value);
                    if (!parsed.IsSuccess) throw new ArgumentException(parsed.Error);
                    area = new(parsed.Value.Value, !exact);
                }
                else sprint = BenchQueryRule.ParseSprint(value);
                if (rules.Count == 0)
                {
                    var unique = effective.IsDefault
                        ? (await defaults.BuildAsync(ct)).Single().QueryAssignedToUniqueName : null;
                    rules.Add(new(null, unique, [], []));
                }
                var replacements = new List<BenchSelector>();
                foreach (var rule in rules)
                {
                    var changed = rule;
                    if (area is { } filter)
                    {
                        var updated = rule.Areas.Where(existing => !string.Equals(existing.Path, filter.Path, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (action == "add") updated.Add(filter);
                        changed = rule with { Areas = updated };
                    }
                    else if (sprint is { } expression)
                    {
                        var updated = rule.Sprints.Where(existing => !string.Equals(existing.Raw, expression.Raw, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (action == "add") updated.Add(expression);
                        changed = rule with { Sprints = updated };
                    }
                    replacements.Add(changed.ToSelector());
                }
                if (!await benches.TryReplaceQuerySelectorsAsync(effective.Id, digest, replacements.Distinct().ToArray(), ct))
                    throw new InvalidOperationException("The captured Bench or automatic settings changed. Refresh and retry; no settings were changed.");
                (effective, stored) = await resolver.ReadCapturedAsync(effective, ct);
            }
            var configuration = await BenchConfigurationProjection.BuildAsync(effective, stored, repository, ct);
            if (output.ToLowerInvariant() is "json" or "json-full" or "json-compact")
            {
                var fields = new Dictionary<string, RenderCell>(((RenderValue.Object)configuration.Value).Cells, StringComparer.Ordinal)
                {
                    ["benchId"] = RenderCell.String(effective.Id.ToString(CultureInfo.InvariantCulture)),
                    ["benchName"] = RenderCell.String(effective.Name),
                    ["bindingId"] = RenderCell.String(binding.Binding.BindingId),
                    ["identityId"] = RenderCell.String(binding.Identity.IdentityId),
                };
                renderers.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("benchConfiguration", fields)]));
            }
            else
            {
                var nodes = new List<RenderNode> { new RenderNode.Text($"Bench configuration: {effective.Name}") };
                foreach (var rule in effective.Selectors.Where(s => s.Kind == SelectorKind.Query).Select(BenchQueryRule.Parse))
                {
                    nodes.Add(new RenderNode.Text("Ownership: " + (rule.UniqueName ?? rule.AssignedTo ?? "all assignees")));
                    nodes.Add(new RenderNode.Text("Areas: " + (rule.Areas.Count == 0 ? "unrestricted" : string.Join(", ", rule.Areas))));
                    nodes.Add(new RenderNode.Text("Sprints: " + (rule.Sprints.Count == 0 ? "none (automatic membership disabled)" : string.Join(", ", rule.Sprints))));
                }
                foreach (var selector in stored.Selectors.Where(s => s.Kind is SelectorKind.Item or SelectorKind.Subtree))
                {
                    var item = await repository.GetByIdAsync(selector.AsWorkItemId(), ct);
                    nodes.Add(new RenderNode.Text($"Pin #{selector.AsWorkItemId()} ({(selector.Kind == SelectorKind.Item ? "single" : "tree")}): {item?.Title ?? "uncached / unverified"}"));
                }
                nodes.Add(new RenderNode.Text("Settings digest: " + BenchQueryRule.SettingsDigest(stored.Selectors)));
                renderers.GetRenderer(output).Render(new RenderTree.RenderTree(nodes));
            }
            exit = 0;
            return exit;
        }
        catch (ArgumentException ex)
        {
            exit = 2;
            ctx.StderrWriter.WriteLine(fmt.FormatError(ex.Message));
            return exit;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.StderrWriter.WriteLine(fmt.FormatError(ex.Message));
            return exit;
        }
        finally
        {
            ctx.TelemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
            {
                ["command"] = "bench-configuration",
                ["output_format"] = output,
                ["exit_code"] = exit.ToString(CultureInfo.InvariantCulture),
                ["twig_version"] = VersionHelper.GetVersion().Split('+')[0],
                ["os_platform"] = RuntimeInformation.OSDescription,
            }, new Dictionary<string, double> { ["duration_ms"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
        }
    }
}
