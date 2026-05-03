namespace Twig.Infrastructure.Ado;

/// <summary>
/// Classifies ADO error messages to distinguish chainable transition rejections
/// from fatal errors (auth, validation, etc.).
/// </summary>
public static class AdoErrorClassifier
{
    /// <summary>
    /// Returns <c>true</c> when the given ADO error message looks like a state-transition
    /// rejection (HTTP 400 from a state PATCH whose target is not reachable from the
    /// current state via the workflow graph).
    /// </summary>
    /// <remarks>
    /// ADO surfaces transition rejections as HTTP 400 with a message that contains
    /// the words "state" and "transition" (and typically a TF/VS error code such as
    /// <c>TF401320</c> or <c>VS402625</c>). We pattern-match on the message text rather
    /// than relying on an unstable error code. False positives are possible but would
    /// only cause one extra PATCH attempt, which ADO would reject the same way.
    /// </remarks>
    public static bool IsTransitionError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        // Both words must be present; "state" alone matches too many unrelated 400s
        // (e.g. invalid field values), and "transition" alone is rare enough to be
        // treated as a transition error on its own.
        var hasTransition = message.Contains("transition", StringComparison.OrdinalIgnoreCase);
        if (hasTransition) return true;

        // Known ADO error codes for transition rejections.
        return message.Contains("TF401320", StringComparison.OrdinalIgnoreCase)
            || message.Contains("VS402625", StringComparison.OrdinalIgnoreCase);
    }
}
