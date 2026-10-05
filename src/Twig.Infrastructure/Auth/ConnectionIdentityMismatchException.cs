using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// Local binding/principal refusal. This is not a server authentication challenge:
/// invalidating a token and retrying must not silently repair an account mismatch.
/// </summary>
internal sealed class ConnectionIdentityMismatchException(string message) : AdoException(message);
