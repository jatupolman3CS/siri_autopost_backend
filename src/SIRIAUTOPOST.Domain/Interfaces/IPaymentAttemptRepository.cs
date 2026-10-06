using SIRIAUTOPOST.Domain.Entities;

namespace SIRIAUTOPOST.Domain.Interfaces;

public interface IPaymentAttemptRepository
{
    /// <summary>The attempt that belongs to a Stripe PaymentIntent; null for a PaymentIntent that is not ours (a renewal's, say).</summary>
    Task<PaymentAttempt?> GetByIntentAsync(string paymentIntentId, CancellationToken ct = default);
    void Add(PaymentAttempt attempt);
}
