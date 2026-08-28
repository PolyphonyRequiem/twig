namespace Twig.Domain.Services.ChangeProposals;

/// <summary>
/// Thrown when a Change Recipe produced something that is not a usable set of Change
/// Proposals — no documents at all, a null document, or a document that is not valid Plan v1.
/// <para>
/// Distinct from <see cref="ChangeRecipeInputException"/> on purpose: that one means the
/// caller supplied bad input, this one means the recipe itself is defective. Collapsing them
/// would send a recipe author hunting through call sites for a bug in their own template.
/// </para>
/// </summary>
public sealed class ChangeRecipeRenderException : Exception
{
    /// <summary>Creates the exception with an explicit message.</summary>
    public ChangeRecipeRenderException(string message)
        : base(message) { }

    /// <summary>Creates the exception with an explicit message and inner cause.</summary>
    public ChangeRecipeRenderException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Parameterless constructor required by the exception design guideline.</summary>
    public ChangeRecipeRenderException()
        : base("Change Recipe rendering failed.") { }
}
