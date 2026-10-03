namespace SIRIAUTOPOST.Domain.Exceptions;

// The requested entity does not exist. The API turns it into 404 Not Found.
public class NotFoundException(string entity, object key) : Exception($"ไม่พบ {entity} ({key})");
