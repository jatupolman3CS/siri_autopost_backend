namespace SIRIAUTOPOST.Domain.Exceptions;

// The payment provider could not be reached or refused for a reason that is not the customer's card. The API turns it into 502.
public class PaymentGatewayException(string message, Exception? inner = null) : Exception(message, inner);
