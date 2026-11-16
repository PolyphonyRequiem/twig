namespace Twig.Infrastructure.Ado.Exceptions;

/// <summary>Base exception for ADO authentication transport failures.</summary>
public class AdoException : Exception
{
    public AdoException(string message) : base(message) { }
    public AdoException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Authentication failed for the explicitly selected identity.</summary>
public sealed class AdoAuthenticationException : AdoException
{
    public AdoAuthenticationException()
        : base("Authentication failed. Renew the explicitly selected identity credential.") { }
    public AdoAuthenticationException(string message) : base(message) { }
}
