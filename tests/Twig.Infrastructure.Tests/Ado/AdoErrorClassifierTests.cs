using Shouldly;
using Twig.Infrastructure.Ado;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

public sealed class AdoErrorClassifierTests
{
    [Theory]
    [InlineData("The state transition from 'New' to 'Closed' is not allowed.")]
    [InlineData("TF401320: Rule Error: The current state of work item is not valid for transition")]
    [InlineData("VS402625: Cannot change state from 'Active' to 'New'.")]
    [InlineData("State transition not permitted by the workflow.")]
    [InlineData("STATE TRANSITION REJECTED")]
    public void IsTransitionError_TrueForKnownPatterns(string msg)
        => AdoErrorClassifier.IsTransitionError(msg).ShouldBeTrue();

    [Theory]
    [InlineData("Field 'System.Title' contains an invalid value.")]
    [InlineData("Authentication failed.")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Concurrency conflict; refresh the work item.")]
    [InlineData("Bad request: required field missing.")]
    public void IsTransitionError_FalseForUnrelatedErrors(string? msg)
        => AdoErrorClassifier.IsTransitionError(msg).ShouldBeFalse();

    [Fact]
    public void IsTransitionError_TF401320CodeAlone_ReturnsTrue()
        => AdoErrorClassifier.IsTransitionError("TF401320: validation error").ShouldBeTrue();

    [Fact]
    public void IsTransitionError_VS402625CodeAlone_ReturnsTrue()
        => AdoErrorClassifier.IsTransitionError("VS402625: validation error").ShouldBeTrue();
}
