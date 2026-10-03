namespace SIRIAUTOPOST.Domain.Exceptions;

// A business rule was broken. The API turns it into 422 Unprocessable Entity.
public class DomainException(string message) : Exception(message);
