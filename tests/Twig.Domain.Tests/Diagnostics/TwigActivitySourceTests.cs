using System.Diagnostics;
using Shouldly;
using Twig.Domain.Diagnostics;
using Xunit;

namespace Twig.Domain.Tests.Diagnostics;

public sealed class TwigActivitySourceTests
{
    [Fact]
    public void Source_Has_Expected_Name()
    {
        TwigActivitySource.Source.Name.ShouldBe("Twig");
    }

    [Fact]
    public void StartActivity_Returns_Activity_When_Listener_Active()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TwigActivitySource.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = TwigActivitySource.Source.StartActivity("test.operation");
        activity.ShouldNotBeNull();
        activity.OperationName.ShouldBe("test.operation");
    }
}

public sealed class TraceTagsTests
{
    /// <summary>
    /// All allowed trace tag keys must NOT contain privacy-sensitive substrings.
    /// This mirrors the telemetry allowlist enforcement.
    /// </summary>
    [Fact]
    public void AllowedKeys_Must_Not_Contain_Sensitive_Substrings()
    {
        string[] sensitiveSubstrings =
        [
            "org", "project", "user", "type", "name", "path",
            "template", "field", "title", "area", "iteration", "repo",
            "branch", "commit", "email", "url"
        ];

        foreach (var key in TraceTags.AllowedKeys)
        {
            foreach (var sensitive in sensitiveSubstrings)
            {
                key.Contains(sensitive, StringComparison.OrdinalIgnoreCase)
                    .ShouldBeFalse($"Trace tag key '{key}' contains sensitive substring '{sensitive}'");
            }
        }
    }

    [Fact]
    public void AllowedKeys_Is_Not_Empty()
    {
        TraceTags.AllowedKeys.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void AllowedKeys_Contains_Core_Keys()
    {
        TraceTags.AllowedKeys.ShouldContain(TraceTags.Command);
        TraceTags.AllowedKeys.ShouldContain(TraceTags.ExitCode);
        TraceTags.AllowedKeys.ShouldContain(TraceTags.Operation);
        TraceTags.AllowedKeys.ShouldContain(TraceTags.OutputFormat);
    }
}

public sealed class ActivityHelperTests : IDisposable
{
    private readonly ActivityListener _listener;

    public ActivityHelperTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TwigActivitySource.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void StartCommand_Creates_Activity_With_Correct_Tags()
    {
        using var activity = ActivityHelper.StartCommand("show", "json");

        activity.ShouldNotBeNull();
        activity.OperationName.ShouldBe("command.show");
        activity.Kind.ShouldBe(ActivityKind.Internal);
        activity.GetTagItem(TraceTags.Command).ShouldBe("show");
        activity.GetTagItem(TraceTags.OutputFormat).ShouldBe("json");
    }

    [Fact]
    public void StartAdoOperation_Creates_Client_Activity()
    {
        using var activity = ActivityHelper.StartAdoOperation("get_work_item");

        activity.ShouldNotBeNull();
        activity.OperationName.ShouldBe("ado.get_work_item");
        activity.Kind.ShouldBe(ActivityKind.Client);
        activity.GetTagItem(TraceTags.Operation).ShouldBe("get_work_item");
    }

    [Fact]
    public void StartSqliteOperation_Creates_Internal_Activity()
    {
        using var activity = ActivityHelper.StartSqliteOperation("query");

        activity.ShouldNotBeNull();
        activity.OperationName.ShouldBe("sqlite.query");
        activity.Kind.ShouldBe(ActivityKind.Internal);
    }

    [Fact]
    public void StartRenderOperation_Creates_Internal_Activity()
    {
        using var activity = ActivityHelper.StartRenderOperation("tree");

        activity.ShouldNotBeNull();
        activity.OperationName.ShouldBe("render.tree");
        activity.Kind.ShouldBe(ActivityKind.Internal);
    }

    [Fact]
    public void Complete_Sets_Ok_Status_For_Zero_ExitCode()
    {
        using var activity = ActivityHelper.StartCommand("test", "human");

        ActivityHelper.Complete(activity, 0);

        activity!.Status.ShouldBe(ActivityStatusCode.Ok);
        activity.GetTagItem(TraceTags.ExitCode).ShouldBe(0);
    }

    [Fact]
    public void Complete_Sets_Error_Status_For_NonZero_ExitCode()
    {
        using var activity = ActivityHelper.StartCommand("test", "human");

        ActivityHelper.Complete(activity, 1);

        activity!.Status.ShouldBe(ActivityStatusCode.Error);
        activity.GetTagItem(TraceTags.ExitCode).ShouldBe(1);
    }

    [Fact]
    public void Fail_Sets_Error_Status_And_ExceptionType()
    {
        using var activity = ActivityHelper.StartCommand("test", "human");

        ActivityHelper.Fail(activity, new InvalidOperationException("secret message"));

        activity!.Status.ShouldBe(ActivityStatusCode.Error);
        activity.GetTagItem(TraceTags.ExceptionKind).ShouldBe("InvalidOperationException");
        // Privacy: exception message must NOT be recorded
        activity.Tags.ShouldNotContain(t => t.Value != null && t.Value.Contains("secret"));
    }

    [Fact]
    public void SetStatusCodeClass_Maps_Correctly()
    {
        using var activity = ActivityHelper.StartAdoOperation("test");

        ActivityHelper.SetStatusCodeClass(activity, 200);
        activity!.GetTagItem(TraceTags.StatusCodeClass).ShouldBe("2xx");
    }

    [Fact]
    public void SetStatusCodeClass_Handles_Error_Codes()
    {
        using var activity = ActivityHelper.StartAdoOperation("test");

        ActivityHelper.SetStatusCodeClass(activity, 404);
        activity!.GetTagItem(TraceTags.StatusCodeClass).ShouldBe("4xx");
    }

    [Fact]
    public void Null_Activity_Methods_Do_Not_Throw()
    {
        // All helper methods should gracefully handle null activity
        Should.NotThrow(() => ActivityHelper.Complete(null, 0));
        Should.NotThrow(() => ActivityHelper.Fail(null, new Exception()));
        Should.NotThrow(() => ActivityHelper.SetItemCount(null, 5));
        Should.NotThrow(() => ActivityHelper.SetStatusCodeClass(null, 200));
    }
}
