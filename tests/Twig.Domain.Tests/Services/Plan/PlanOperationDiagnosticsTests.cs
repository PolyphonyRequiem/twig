using Shouldly;
using Twig.Domain.Services.Plan;
using Xunit;

namespace Twig.Domain.Tests.Services.Plan;

/// <summary>
/// #1178 D5: the shared diagnostics projection surfaces the provider revision-precondition
/// refusal code, and its summary names at most one of the three bounded refusal reasons.
/// The reason itself is never a projected field.
/// </summary>
public sealed class PlanOperationDiagnosticsTests
{
    private const string RefusedSummaryTail =
        ": this operation was not sent; earlier operations keep their recorded outcomes; a newly reviewed proposal is required";

    private static PlanJournalOperation RefusedRow(string diagnosticsBody, string rootMembers = "") => new()
    {
        Ordinal = 0,
        OpId = "op-1",
        Kind = PlanOperationKind.Batch,
        State = PlanOperationState.Failed,
        RequestJson = """{"expectedRevision":5}""",
        // Mirrors PlanOperationExecutor.SerializeDiagnostics: failureDetail sits at the root.
        ResultJson = "{\"diagnostics\":{" + diagnosticsBody + "}" + rootMembers + "}",
        Error = "revision-precondition-refused: private adapter detail; nothing was sent.",
    };

    [Theory]
    [InlineData("Missing")]
    [InlineData("ForeignProvider")]
    [InlineData("MalformedToken")]
    public void PreconditionRefusal_ProjectsItsCode_AndNamesTheBoundedReason(string reason)
    {
        var diagnostics = RefusedRow(
            "\"code\":\"revision-precondition-refused\",\"expectedRevision\":5,\"observedRevision\":null",
            ",\"failureDetail\":\"" + reason + "\"").Diagnostics;

        diagnostics.Disposition.ShouldBe("failed");
        diagnostics.Code.ShouldBe("revision-precondition-refused");
        diagnostics.ExpectedRevision.ShouldBe(5);
        diagnostics.ObservedRevision.ShouldBeNull();
        diagnostics.Fields.ShouldBeEmpty();
        diagnostics.MissingFields.ShouldBeEmpty();
        diagnostics.Summary.ShouldBe($"revision precondition refused ({reason})" + RefusedSummaryTail);
    }

    [Theory]
    [InlineData(",\"failureDetail\":\"secret@example.com\"")]
    [InlineData(",\"failureDetail\":\"missing\"")]
    [InlineData(",\"failureDetail\":7")]
    [InlineData("")]
    public void PreconditionRefusal_UnrecognisedOrAbsentReason_IsOmittedFromTheSummary(string detailMember)
    {
        var diagnostics = RefusedRow(
            "\"code\":\"revision-precondition-refused\",\"expectedRevision\":5", detailMember).Diagnostics;

        diagnostics.Code.ShouldBe("revision-precondition-refused");
        diagnostics.Summary.ShouldBe("revision precondition refused" + RefusedSummaryTail);
        diagnostics.Summary.ShouldNotContain("secret");
        diagnostics.Summary.ShouldNotContain("private adapter detail");
    }

    [Fact]
    public void ReasonNestedInsideDiagnostics_IsNotTheEmitterShape_AndIsIgnored()
    {
        var diagnostics = RefusedRow(
            "\"code\":\"revision-precondition-refused\",\"expectedRevision\":5,\"failureDetail\":\"ForeignProvider\"")
            .Diagnostics;

        diagnostics.Summary.ShouldBe("revision precondition refused" + RefusedSummaryTail);
    }

    [Fact]
    public void ReasonOnAnotherCode_NeverReachesTheSummary()
    {
        var diagnostics = RefusedRow(
            "\"code\":\"revision-conflict\",\"expectedRevision\":5,\"observedRevision\":6",
            ",\"failureDetail\":\"ForeignProvider\"")
            .Diagnostics;

        diagnostics.Code.ShouldBe("revision-conflict");
        diagnostics.Summary.ShouldNotContain("ForeignProvider");
    }
}
