using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Domain.Entities;

// One line of the activity log: what a platform admin did, and the plan changes customers make
// themselves (the plan history the churn and MRR figures are rebuilt from). Never edited.
public class AuditEntry : Entity
{
    public DateTimeOffset At { get; private set; }
    /// <summary>Who did it: an admin, or the customer themselves for self-service plan changes.</summary>
    public Guid ActorId { get; private set; }
    /// <summary>The customer it concerns; null for platform-wide changes (plan prices, promo codes).</summary>
    public Guid? CustomerId { get; private set; }
    public AuditAction Action { get; private set; }
    /// <summary>Machine-readable values (a plan key, a status, "accounts=5"), not display text.</summary>
    public string? From { get; private set; }
    public string? To { get; private set; }

    private AuditEntry() { } // EF Core

    public static AuditEntry Create(
        Guid actorId, Guid? customerId, AuditAction action, DateTimeOffset now, string? from = null, string? to = null) =>
        new() { ActorId = actorId, CustomerId = customerId, Action = action, At = now, From = from, To = to };

    public static AuditEntry PlanChange(Guid actorId, User customer, PlanKey from, DateTimeOffset now) =>
        Create(actorId, customer.Id, AuditAction.PlanChanged, now, Key(from), Key(customer.Plan));

    public static string Key(PlanKey plan) => plan.ToString().ToLowerInvariant();
}
