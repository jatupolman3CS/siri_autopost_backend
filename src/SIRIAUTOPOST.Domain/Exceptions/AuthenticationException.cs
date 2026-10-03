namespace SIRIAUTOPOST.Domain.Exceptions;

// Wrong credentials. The API turns it into 401 Unauthorized.
public class AuthenticationException(string message) : Exception(message);
