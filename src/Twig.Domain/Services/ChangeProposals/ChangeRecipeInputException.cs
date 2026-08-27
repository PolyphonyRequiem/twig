namespace Twig.Domain.Services.ChangeProposals;

/// <summary>
/// Thrown when a Change Recipe is rendered against inputs that are missing or unusable.
/// <para>
/// This is deliberately an exception rather than an issue list entry. Rendering against bad
/// input produces no document at all, so there is nothing to review and nothing to
/// authorize; degrading to a partially-rendered proposal would hand a reviewer a document
/// that does not say what they think it says.
/// </para>
/// </summary>
public sealed class ChangeRecipeInputException : Exception
{
    /// <summary>The offending input's name.</summary>
    public string InputName { get; }

    /// <summary>Creates the exception for <paramref name="inputName"/>.</summary>
    public ChangeRecipeInputException(string inputName, string reason)
        : base($"Change Recipe input '{inputName}': {reason}.")
        => InputName = inputName;

    /// <summary>Creates the exception with an explicit message.</summary>
    public ChangeRecipeInputException(string message)
        : base(message) => InputName = string.Empty;

    /// <summary>Creates the exception with an explicit message and inner cause.</summary>
    public ChangeRecipeInputException(string message, Exception innerException)
        : base(message, innerException) => InputName = string.Empty;

    /// <summary>Parameterless constructor required by the exception design guideline.</summary>
    public ChangeRecipeInputException()
        : base("Change Recipe input was missing or invalid.") => InputName = string.Empty;
}
