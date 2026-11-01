namespace Twig.Infrastructure.Persistence;

internal sealed record SystemWorktreeRow(string ConnectionRef, DateTimeOffset? RetiredAt);
