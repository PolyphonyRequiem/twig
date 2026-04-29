namespace Twig.Domain.Services.Mutation;

/// <summary>
/// Value object representing the outcome of a mutation operation.
/// </summary>
public sealed record MutationResult(bool IsSuccess, string? ErrorMessage = null, int? NewRevision = null)
{
    public static MutationResult Success(int newRevision) => new(true, NewRevision: newRevision);
    public static MutationResult Error(string message) => new(false, message);
}
