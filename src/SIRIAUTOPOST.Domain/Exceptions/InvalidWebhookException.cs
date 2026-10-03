namespace SIRIAUTOPOST.Domain.Exceptions;

// A webhook body whose signature does not verify. The API turns it into 400, so Stripe does not treat it as delivered.
public class InvalidWebhookException(string message) : Exception(message);
