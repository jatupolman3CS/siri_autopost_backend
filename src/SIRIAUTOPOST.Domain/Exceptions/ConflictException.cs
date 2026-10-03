namespace SIRIAUTOPOST.Domain.Exceptions;

// The request clashes with existing data (e.g. an email already registered). The API turns it into 409.
public class ConflictException(string message) : Exception(message);
