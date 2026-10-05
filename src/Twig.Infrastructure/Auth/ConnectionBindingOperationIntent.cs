namespace Twig.Infrastructure.Auth;

/// <summary>
/// Explicit management intent for initialization metadata acquisition. Normal work
/// commands still require a validated attached worktree and its admitted binding.
/// </summary>
internal sealed record ConnectionBindingOperationIntent(bool InitializationMetadataOnly);
