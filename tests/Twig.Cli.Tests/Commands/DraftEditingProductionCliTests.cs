using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

[Trait("Category", "HostInventory")]
public sealed class DraftEditingProductionCliTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture _fixture = new();
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync().AsTask();

    [HostMigrationFact]
    public async Task RealRetainParserEmitsStructuredUsageAndJsonRefusalsWithoutWrites()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        foreach (var (args, input, code) in new[]
        {
            (new[] { "draft", "retain", "-o", "json" }, "", "usage"),
            (new[] { "draft", "retain", "--stdin", "-o", "json" }, "{invalid json", "invalid-json"),
        })
        {
            var result = await RunAsync(args, input);
            result.Exit.ShouldBe(2, $"stdout={result.Out}\nstderr={result.Error}");
            using var output = JsonDocument.Parse(result.Out);
            output.RootElement.GetProperty("version").GetInt32().ShouldBe(1);
            output.RootElement.GetProperty("status").GetString().ShouldBe("refused");
            output.RootElement.GetProperty("committed").GetBoolean().ShouldBeFalse();
            output.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe(code);
            output.RootElement.TryGetProperty("capture", out _).ShouldBeFalse();
            using var error = JsonDocument.Parse(result.Error);
            error.RootElement.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.String);
        }
        (await runtime.GetRequiredService<IPlanJournalRepository>().GetUnresolvedAsync()).ShouldBeEmpty();
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task RealParserBindsStdinBenchAndOutputAndEmitsStructuredUsageRefusals()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        await repository.SaveAsync(NativeBrowserFixture.Item(42, "Retained"));
        foreach (var (args, input, code) in new[]
        {
            (new[] { "draft", "stage", "-o", "json" }, "", "usage"),
            (new[] { "draft", "stage", "--stdin", "-o", "json" }, "{invalid json", "invalid-json"),
            (new[] { "draft", "open", "0", "--bench", "default", "-o", "json" }, "", "usage"),
        })
        {
            var result = await RunAsync(args, input);
            result.Exit.ShouldBe(2, $"stdout={result.Out}\nstderr={result.Error}");
            result.Out.ShouldNotBeNullOrWhiteSpace(
                $"The real endpoint must emit its structured envelope. Args={string.Join(' ', args)}; stderr={result.Error}");
            using var output = JsonDocument.Parse(result.Out);
            output.RootElement.GetProperty("version").GetInt32().ShouldBe(1);
            output.RootElement.GetProperty("committed").GetBoolean().ShouldBeFalse();
            output.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe(code);
            result.Error.ShouldStartWith("{", customMessage: $"Args={string.Join(' ', args)}; stderr={result.Error}");
            using var formattedError = JsonDocument.Parse(result.Error);
            formattedError.RootElement.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.String);
        }
        (await runtime.GetRequiredService<IPendingChangeStore>().GetChangesAsync(42)).ShouldBeEmpty();
        (await repository.GetByIdAsync(42))!.Title.ShouldBe("Retained");
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task GeneratedParserReviewsExactRetainedCaptureWithoutSourceOrJournalChanges()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var (retained, staged) = await CreateRetainedCaptureAsync(runtime,
            [new("System.Title", "Retained proposed title"), new("System.AssignedTo", "")]);
        var digest = retained.Digest;
        var journals = runtime.GetRequiredService<IPlanJournalRepository>();
        var before = (await journals.GetAsync(digest))!;
        File.Exists(before.SourcePath).ShouldBeFalse();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var journalStateBefore = CaptureDatabaseState(store);

        var result = await RunReviewParserAsync(runtime, ["draft", "review", digest, "-o", "json"]);
        result.Exit.ShouldBe(0, $"stdout={result.Out}\nstderr={result.Error}");
        result.Error.ShouldBeEmpty();
        using var output = JsonDocument.Parse(result.Out);
        var review = output.RootElement.GetProperty("review");
        review.GetProperty("digest").GetString().ShouldBe(digest);
        review.GetProperty("workspace").GetProperty("organization").GetString().ShouldBe(NativeBrowserFixture.Organization);
        review.GetProperty("workspace").GetProperty("project").GetString().ShouldBe(NativeBrowserFixture.Project);
        review.GetProperty("workItemId").GetInt32().ShouldBe(42);
        review.GetProperty("operationId").GetString().ShouldBe(retained.OperationId);
        review.GetProperty("generation").GetString().ShouldBe(staged.Generation);
        review.GetProperty("revision").GetString().ShouldBe(staged.CurrentRevision);
        review.GetProperty("itemContext").GetString().ShouldBe("not-retained");
        review.GetProperty("freshness").GetString().ShouldBe("current-against-cached-native-evidence-at-validation");
        review.GetProperty("publisherProvenance").GetString().ShouldBe("captured-native-origin-validated");
        review.GetProperty("authorized").GetBoolean().ShouldBeFalse();
        review.GetProperty("canApply").GetBoolean().ShouldBeFalse();
        var effects = review.GetProperty("effects").EnumerateArray().ToArray();
        effects.Length.ShouldBe(2);
        var titleEffect = effects.Single(effect => effect.GetProperty("fieldRef").GetString() == "System.Title");
        titleEffect.GetProperty("baselineValue").GetString().ShouldBe("Private cached title");
        titleEffect.GetProperty("proposedValue").GetString().ShouldBe("Retained proposed title");
        var assignedToEffect = effects.Single(effect => effect.GetProperty("fieldRef").GetString() == "System.AssignedTo");
        assignedToEffect.GetProperty("baselineValue").ValueKind.ShouldBe(JsonValueKind.Null);
        assignedToEffect.GetProperty("proposedValue").GetString().ShouldBe("");
        foreach (var forbidden in new[] { "title", "type", "state", "canonicalJson", "sourcePath", "credentialRef", "tenantId", "objectId", "worktreeRoot" })
            review.TryGetProperty(forbidden, out _).ShouldBeFalse();
        result.Out.ShouldNotContain(_fixture.Home);
        result.Out.ShouldNotContain(NativeBrowserFixture.ActorPat);
        CaptureDatabaseState(store).ShouldBe(journalStateBefore);
    }

    [HostMigrationFact]
    public async Task GeneratedParserHumanAndMinimalReviewShowExactNullableEffects()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var (retained, _) = await CreateRetainedCaptureAsync(runtime,
            [new("System.AssignedTo", null)], assignedTo: "Initial assignee");
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var before = CaptureDatabaseState(store);

        var human = await RunReviewParserAsync(runtime, ["draft", "review", retained.Digest]);
        human.Exit.ShouldBe(0, $"stdout={human.Out}\nstderr={human.Error}");
        human.Error.ShouldBeEmpty();
        human.Out.ShouldContain("\"System.AssignedTo\": \"Initial assignee\" -> null");
        human.Out.ShouldContain("Freshness: current against cached native evidence at validation time");
        human.Out.ShouldContain("Authority: not authorized; this review cannot apply the proposal.");
        human.Out.ShouldNotContain(_fixture.Home);
        human.Out.ShouldNotContain(NativeBrowserFixture.ActorPat);
        CaptureDatabaseState(store).ShouldBe(before);

        var minimal = await RunReviewParserAsync(runtime, ["draft", "review", retained.Digest, "-o", "minimal"]);
        minimal.Exit.ShouldBe(0, $"stdout={minimal.Out}\nstderr={minimal.Error}");
        minimal.Error.ShouldBeEmpty();
        minimal.Out.Trim().ShouldBe($"draft reviewed\t{retained.Digest}\t1");
        CaptureDatabaseState(store).ShouldBe(before);
    }

    [HostMigrationFact]
    public async Task GeneratedParserRejectsInvalidAndAbsentReviewDigestsWithoutWrites()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var before = CaptureDatabaseState(store);

        var missing = await RunReviewParserAsync(runtime, ["draft", "review", "-o", "json"]);
        missing.Exit.ShouldBe(2, $"stdout={missing.Out}\nstderr={missing.Error}");
        AssertReviewRefusal(missing, "usage");
        AssertNoSensitiveReviewData(missing);
        CaptureDatabaseState(store).ShouldBe(before);

        var invalid = await RunReviewParserAsync(runtime, ["draft", "review", "NOT-A-DIGEST", "-o", "json"]);
        invalid.Exit.ShouldBe(2, $"stdout={invalid.Out}\nstderr={invalid.Error}");
        AssertReviewRefusal(invalid, "usage");
        AssertNoSensitiveReviewData(invalid);
        CaptureDatabaseState(store).ShouldBe(before);

        var absent = await RunReviewParserAsync(runtime, ["draft", "review", new string('a', 64), "-o", "json"]);
        absent.Exit.ShouldBe(1, $"stdout={absent.Out}\nstderr={absent.Error}");
        AssertReviewRefusal(absent, "capture-not-found");
        AssertNoSensitiveReviewData(absent);
        CaptureDatabaseState(store).ShouldBe(before);
    }

    [HostMigrationFact]
    public async Task GeneratedParserRefusesStaleEvidenceWithoutChangingJournalOrExportState()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var (retained, _) = await CreateRetainedCaptureAsync(runtime,
            [new("System.Title", "Retained proposed title")]);
        await runtime.GetRequiredService<IWorkItemRepository>()
            .SaveAsync(NativeBrowserFixture.Item(42, "Same-revision changed title"));
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var before = CaptureDatabaseState(store);

        var result = await RunReviewParserAsync(runtime, ["draft", "review", retained.Digest, "-o", "json"]);
        result.Exit.ShouldBe(1, $"stdout={result.Out}\nstderr={result.Error}");
        AssertReviewRefusal(result, "draft-capture-stale");
        result.Out.ShouldNotContain(_fixture.Home);
        result.Out.ShouldNotContain(NativeBrowserFixture.ActorPat);
        result.Error.ShouldNotContain(_fixture.Home);
        result.Error.ShouldNotContain(NativeBrowserFixture.ActorPat);
        CaptureDatabaseState(store).ShouldBe(before);
    }

    [HostMigrationFact]
    public async Task GeneratedParserRefusesLegacyCaptureWithoutChangingJournalOrExportState()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var (_, document) = await CreateRetainedCaptureAsync(runtime,
            [new("System.Title", "Retained proposed title")]);
        var key = document.NativeContext.ItemKey;
        var draft = (await runtime.GetRequiredService<IRecoverableWorkItemDraftStore>().GetAsync(key)).ShouldNotBeNull();
        var snapshot = (await new SqliteWorkItemRepository(runtime.GetRequiredService<SqliteCacheStore>(),
            new Twig.Domain.Services.WorkItemMapper()).GetSnapshotByIdAsync(key.WorkItemId)).ShouldNotBeNull();
        var origin = await runtime.GetRequiredService<IPlanOriginProvider>().GetOriginAsync();
        var config = runtime.GetRequiredService<CommandContext>().Config;
        var legacy = await runtime.GetRequiredService<IDraftProposalCaptureStore>().CaptureAsync(
            draft, snapshot, origin,
            new PlanWorkspace { Organization = config.Organization, Project = config.Project },
            Path.Combine(key.CheckoutRoot, ".twig", "proposals"));
        legacy.DraftCapture.ShouldNotBeNull().Version.ShouldBe(1);
        legacy.DraftCapture!.EvidenceFingerprint.ShouldBeNull();
        File.Exists(legacy.SourcePath).ShouldBeFalse();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var before = CaptureDatabaseState(store);

        var result = await RunReviewParserAsync(runtime, ["draft", "review", legacy.Digest, "-o", "json"]);
        result.Exit.ShouldBe(1, $"stdout={result.Out}\nstderr={result.Error}");
        AssertReviewRefusal(result, "draft-capture-missing-evidence");
        AssertNoSensitiveReviewData(result);
        CaptureDatabaseState(store).ShouldBe(before);
    }

    [HostMigrationFact]
    public async Task GeneratedParserRefusesMalformedAndNonCaptureJournalsWithoutChangingTheirRows()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var (retained, _) = await CreateRetainedCaptureAsync(runtime,
            [new("System.Title", "Retained proposed title")]);
        var store = runtime.GetRequiredService<SqliteCacheStore>();

        using (var corrupt = store.GetConnection().CreateCommand())
        {
            corrupt.CommandText = "UPDATE pending.proposal_journals SET draft_capture_json='{' WHERE digest=@digest;";
            corrupt.Parameters.AddWithValue("@digest", retained.Digest);
            corrupt.ExecuteNonQuery().ShouldBe(1);
        }
        var malformedState = CaptureDatabaseState(store);
        var malformed = await RunReviewParserAsync(runtime, ["draft", "review", retained.Digest, "-o", "json"]);
        malformed.Exit.ShouldBe(1, $"stdout={malformed.Out}\nstderr={malformed.Error}");
        AssertReviewRefusal(malformed, "draft-capture-invalid");
        AssertNoSensitiveReviewData(malformed);
        CaptureDatabaseState(store).ShouldBe(malformedState);

        using (var removeCapture = store.GetConnection().CreateCommand())
        {
            removeCapture.CommandText = "UPDATE pending.proposal_journals SET draft_capture_json=NULL WHERE digest=@digest;";
            removeCapture.Parameters.AddWithValue("@digest", retained.Digest);
            removeCapture.ExecuteNonQuery().ShouldBe(1);
        }
        var nonCaptureState = CaptureDatabaseState(store);
        var nonCapture = await RunReviewParserAsync(runtime, ["draft", "review", retained.Digest, "-o", "json"]);
        nonCapture.Exit.ShouldBe(1, $"stdout={nonCapture.Out}\nstderr={nonCapture.Error}");
        AssertReviewRefusal(nonCapture, "draft-capture-required");
        AssertNoSensitiveReviewData(nonCapture);
        CaptureDatabaseState(store).ShouldBe(nonCaptureState);
    }

    [HostMigrationFact]
    public async Task GeneratedParserMapsSqliteBusyToSafeRefusalWithoutChangingJournalOrExportState()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var (retained, _) = await CreateRetainedCaptureAsync(runtime,
            [new("System.Title", "Retained proposed title")]);
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        store.GetConnection().DefaultTimeout = 1;
        var before = CaptureDatabaseState(store);
        var blockerConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _fixture.Paths.DbPath,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString();
        using (var blocker = new SqliteConnection(blockerConnectionString))
        {
            blocker.Open();
            using (blocker.BeginTransaction(deferred: false))
            {
                using var probe = new SqliteConnection(blockerConnectionString);
                probe.Open();
                var locked = Should.Throw<SqliteException>(() => probe.BeginTransaction(deferred: false));
                locked.SqliteErrorCode.ShouldBe(5);

                var result = await RunReviewParserAsync(runtime,
                    ["draft", "review", retained.Digest, "-o", "json"]);
                result.Exit.ShouldBe(1, $"stdout={result.Out}\nstderr={result.Error}");
                AssertReviewRefusal(result, "capture-unavailable");
                result.Out.ShouldContain("The retained capture could not be read from local storage.");
                result.Out.ShouldNotContain("database is locked");
                result.Error.ShouldNotContain("database is locked");
                result.Error.ShouldNotContain("SQLite Error");
                AssertNoSensitiveReviewData(result);
            }
        }
        CaptureDatabaseState(store).ShouldBe(before);
    }

    private RecoverableDraftEditingModule CreateEditingModule(ServiceProvider runtime)
    {
        var operations = runtime.GetRequiredService<BrowserOperationModule>();
        return new RecoverableDraftEditingModule(
            operations,
            new BrowserEditingModule(operations, runtime.GetRequiredService<IWorkItemRepository>(),
                runtime.GetRequiredService<IPendingChangeStore>(),
                new DetailDocumentSource(new ReviewLayoutProvider()),
                runtime.GetRequiredService<IProcessConfigurationProvider>()),
            runtime.GetRequiredService<SqliteCacheStore>(),
            runtime.GetRequiredService<IRecoverableWorkItemDraftStore>(),
            runtime.GetRequiredService<IWorkItemRepository>(),
            runtime.GetRequiredService<IPendingChangeStore>(),
            runtime.GetRequiredService<IPlanOriginProvider>(),
            runtime.GetRequiredService<IWorkItemProvider>(),
            new ReviewRuleProvider(),
            runtime.GetRequiredService<IDraftProposalCaptureStore>());
    }

    private async Task<(DraftRetainedCapture Capture, DraftEditingDocument Document)> CreateRetainedCaptureAsync(
        ServiceProvider runtime, IReadOnlyList<DraftEditingChange> changes, string? assignedTo = null)
    {
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        var item = new WorkItem
        {
            Id = 42,
            Type = WorkItemType.Task,
            Title = "Private cached title",
            State = "To Do",
            AssignedTo = assignedTo,
        };
        item.MarkSynced(3);
        await repository.SaveAsync(item);
        var benches = runtime.GetRequiredService<IBenchRepository>();
        await benches.AddSelectorAsync((await benches.GetByNameAsync("default"))!.Id, BenchSelector.ForItem(42));

        var module = CreateEditingModule(runtime);
        var opened = await module.OpenAsync(42, "default");
        opened.Error.ShouldBeNull(opened.Error?.Message);
        var staged = await module.StageAsync(new DraftStageRequest(1, opened.Document!.NativeContext,
            opened.Document.Generation, opened.Document.BaselineRevision, opened.Document.ObservedRevision, changes));
        staged.Error.ShouldBeNull(staged.Error?.Message);
        var document = staged.Document!;
        var retained = await runtime.GetRequiredService<RecoverableDraftEditingModule>().RetainAsync(
            new DraftRetainRequest(1, document.NativeContext, document.Generation,
                document.BaselineRevision, document.ObservedRevision),
            new PlanWorkspace
            {
                Organization = runtime.GetRequiredService<CommandContext>().Config.Organization,
                Project = runtime.GetRequiredService<CommandContext>().Config.Project,
            });
        retained.Error.ShouldBeNull(retained.Error?.Message);
        return (retained.Capture!, document);
    }

    private static void AssertReviewRefusal((int Exit, string Out, string Error) result, string code)
    {
        using var output = JsonDocument.Parse(result.Out);
        output.RootElement.GetProperty("status").GetString().ShouldBe("refused");
        output.RootElement.GetProperty("committed").GetBoolean().ShouldBeFalse();
        output.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe(code);
        using var error = JsonDocument.Parse(result.Error);
        error.RootElement.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.String);
    }

    private void AssertNoSensitiveReviewData((int Exit, string Out, string Error) result)
    {
        result.Out.ShouldNotContain(_fixture.Home);
        result.Out.ShouldNotContain(NativeBrowserFixture.ActorPat);
        result.Error.ShouldNotContain(_fixture.Home);
        result.Error.ShouldNotContain(NativeBrowserFixture.ActorPat);
    }

    private static string CaptureDatabaseState(SqliteCacheStore store)
    {
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in new[] { "proposal_journals", "proposal_operations", "proposal_receipts" })
        {
            using var command = store.GetConnection().CreateCommand();
            command.CommandText = $"SELECT * FROM pending.{table};";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                var values = new string?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    values[i] = reader.IsDBNull(i) ? null : DbValue(reader.GetValue(i));
                rows.Add(JsonSerializer.Serialize(values));
            }
            rows.Sort(StringComparer.Ordinal);
            state[table] = string.Join('\n', rows);
        }
        using (var command = store.GetConnection().CreateCommand())
        {
            command.CommandText = "SELECT digest, source_path FROM pending.proposal_journals;";
            using var reader = command.ExecuteReader();
            var exports = new List<string>();
            while (reader.Read())
                exports.Add(JsonSerializer.Serialize(new
                {
                    Digest = reader.GetString(0),
                    Exists = File.Exists(reader.GetString(1)),
                }));
            exports.Sort(StringComparer.Ordinal);
            state["exportFiles"] = string.Join('\n', exports);
        }
        return JsonSerializer.Serialize(state);
    }

    private static string DbValue(object value) => value switch
    {
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => $"{value.GetType().FullName}:{Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)}",
    };

    private static Task<(int Exit, string Out, string Error)> RunReviewParserAsync(
        ServiceProvider runtime, string[] args)
    {
        var stdout = Console.Out;
        var stderr = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var context = runtime.GetRequiredService<CommandContext>() with { Stderr = error };
            var exitCode = Twig.TwigConsoleApp.Run(args, services =>
                services.AddSingleton<DraftEditingCommand>(_ =>
                    new DraftEditingCommand(context,
                        () => runtime.GetRequiredService<RecoverableDraftEditingModule>(), output)));
            return Task.FromResult((exitCode, output.ToString(), error.ToString()));
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
    }

    private async Task<(int Exit, string Out, string Error)> RunAsync(string[] args, string input)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _fixture.Paths.StartDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Add(typeof(OutputFormatArgumentValidator).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["TWIG_USER_HOME"] = _fixture.Home;
        start.Environment.Remove("TWIG_TELEMETRY_ENDPOINT");
        start.Environment["HTTP_PROXY"] = "http://127.0.0.1:1";
        start.Environment["HTTPS_PROXY"] = "http://127.0.0.1:1";
        start.Environment["ALL_PROXY"] = "http://127.0.0.1:1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The built CLI did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            throw;
        }
    }

    private sealed class ReviewLayoutProvider : IFormLayoutProvider
    {
        public Task<FormLayoutResult> GetFormLayoutAsync(string type, CancellationToken ct = default)
            => Task.FromResult<FormLayoutResult>(new FormLayoutResult.Served(new("Fixture.Task", "fixture-process",
            [
                new("fields", "Fields", "custom", true, false,
                [
                    new("column", [new("group", "Group", true, false,
                    [
                        new("System.Title", "Title", "FieldControl", false, true, false),
                        new("System.AssignedTo", "Assigned", "FieldControl", false, true, false),
                        new("System.State", "State", "FieldControl", false, true, false),
                    ])]),
                ]),
                new("history", "History", "history", true, false, []),
            ])));
    }

    private sealed class ReviewRuleProvider : IProcessRuleProvider
    {
        public Task<IReadOnlyList<ProcessRule>> GetRulesAsync(string type, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProcessRule>>([]);
    }
}
