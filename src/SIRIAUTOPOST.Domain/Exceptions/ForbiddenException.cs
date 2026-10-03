namespace SIRIAUTOPOST.Domain.Exceptions;

// Signed in, but this user's role (in the workspace, or on the platform) does not allow it. The API turns it into 403.
public class ForbiddenException(string message) : Exception(message);
