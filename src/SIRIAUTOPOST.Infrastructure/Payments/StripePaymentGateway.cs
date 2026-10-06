using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.Features.Billing;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.ValueObjects;
using Stripe;

namespace SIRIAUTOPOST.Infrastructure.Payments;

/// <summary>
/// <see cref="IPaymentGateway"/> on Stripe. Plans are not Stripe prices: the database stays the source of truth
/// for what a plan costs, and each checkout (or plan change) sends the price inline against one Stripe product
/// per plan. Card declines become <see cref="DomainException"/>s (422); anything else Stripe refuses or fails
/// to answer becomes a <see cref="PaymentGatewayException"/> (502).
/// </summary>
public sealed class StripePaymentGateway : IPaymentGateway
{
    private readonly StripeOptions options;
    private readonly ILogger<StripePaymentGateway> log;
    private readonly StripeClient? client;
    private readonly StripeWebhookParser webhooks;
    private readonly ConcurrentDictionary<Domain.Enums.PlanKey, string> products = new();

    /// <param name="http">Replaces Stripe's own HTTP client (tests answer Stripe's calls with canned JSON).</param>
    public StripePaymentGateway(IOptions<StripeOptions> options, ILogger<StripePaymentGateway> log, HttpClient? http = null)
    {
        this.options = options.Value;
        this.log = log;
        webhooks = new StripeWebhookParser(this.options.WebhookSecret);
        if (Enabled)
            client = new StripeClient(new StripeClientOptions
            {
                ApiKey = this.options.SecretKey,
                // Stripe's client retries connection errors and 409/5xx answers itself (with the same idempotency key).
                HttpClient = new SystemNetHttpClient(http, maxNetworkRetries: 2),
            });
        if (Enabled && string.IsNullOrWhiteSpace(this.options.WebhookSecret))
            log.LogWarning("Stripe:SecretKey is set but Stripe:WebhookSecret is not: payments will be taken but plans only follow Stripe on the customer's return from Checkout");
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(options.SecretKey);

    private IStripeClient Client => client ?? throw new DomainException(ChangePlanCommandHandler.NotReady);

    private string Currency => string.IsNullOrWhiteSpace(options.Currency) ? "thb" : options.Currency.Trim().ToLowerInvariant();

    public PaymentEvent? ParseWebhook(string payload, string? signature) => webhooks.Parse(payload, signature);

    public Task<string> EnsureCustomerAsync(User user, CancellationToken ct = default) => Call(async () =>
    {
        var customer = await new CustomerService(Client).CreateAsync(
            new CustomerCreateOptions
            {
                Email = user.Email,
                Name = user.Name,
                Metadata = new Dictionary<string, string> { [StripeMapping.UserKey] = user.Id.ToString() },
            },
            new RequestOptions { IdempotencyKey = $"customer:{user.Id}" }, ct);
        return customer.Id;
    });

    public Task<string> CreateCheckoutAsync(CheckoutRequest r, CancellationToken ct = default) => Call(async () =>
    {
        var metadata = StripeMapping.Metadata(r.UserId, r.Plan, r.Cycle, r.PromoCode);
        if (r.PrepaidAmount is { } prepaid)
        {
            // PromptPay: Stripe's page for ONE payment (it is not allowed in subscription mode), for one period of the plan.
            metadata[StripeMapping.KindKey] = StripeMapping.PrepaidKind;
            var once = await new Stripe.Checkout.SessionService(Client).CreateAsync(new Stripe.Checkout.SessionCreateOptions
            {
                Mode = "payment",
                AllowedPaymentMethodTypes = ["promptpay"],
                Customer = r.CustomerId,
                ClientReferenceId = r.UserId.ToString(),
                SuccessUrl = r.SuccessUrl,
                CancelUrl = r.CancelUrl,
                Locale = "th",
                LineItems =
                [
                    new Stripe.Checkout.SessionLineItemOptions
                    {
                        Quantity = 1,
                        PriceData = new Stripe.Checkout.SessionLineItemPriceDataOptions
                        {
                            Currency = Currency,
                            Product = await ProductAsync(r.Plan, ct),
                            UnitAmount = Money.Satang(prepaid),
                        },
                    },
                ],
                Metadata = metadata,
                PaymentIntentData = new Stripe.Checkout.SessionPaymentIntentDataOptions
                {
                    Metadata = metadata,
                    Description = $"AutoPost {r.Plan} ({StripeMapping.Key(r.Cycle)})",
                },
            }, cancellationToken: ct);
            return once.Url ?? throw new PaymentGatewayException("Stripe ไม่ได้ส่งหน้าชำระเงินกลับมา");
        }
        var session = new Stripe.Checkout.SessionCreateOptions
        {
            Mode = "subscription",
            Customer = r.CustomerId,
            ClientReferenceId = r.UserId.ToString(),
            SuccessUrl = r.SuccessUrl,
            CancelUrl = r.CancelUrl,
            Locale = "th",
            LineItems = [new Stripe.Checkout.SessionLineItemOptions { Quantity = 1, PriceData = await PriceDataAsync(r.Plan, r.Cycle, r.Price, r.ChargeOverride, ct) }],
            Metadata = metadata,
            SubscriptionData = new Stripe.Checkout.SessionSubscriptionDataOptions { Metadata = metadata },
        };
        if (r.FirstDiscount > 0)
        {
            // The promo code takes this much off the first invoice only (Pricing.FirstDiscount).
            var coupon = await new CouponService(Client).CreateAsync(new CouponCreateOptions
            {
                AmountOff = Money.Satang(r.FirstDiscount),
                Currency = Currency,
                Duration = "once",
                MaxRedemptions = 1,
                Name = $"Promo {r.PromoCode}",
                Metadata = new Dictionary<string, string> { [StripeMapping.PromoKey] = r.PromoCode ?? "", [StripeMapping.UserKey] = r.UserId.ToString() },
            }, cancellationToken: ct);
            session.Discounts = [new Stripe.Checkout.SessionDiscountOptions { Coupon = coupon.Id }];
        }
        var created = await new Stripe.Checkout.SessionService(Client).CreateAsync(session, cancellationToken: ct);
        return created.Url ?? throw new PaymentGatewayException("Stripe ไม่ได้ส่งหน้าชำระเงินกลับมา");
    });

    // The in-app checkout. A subscription made "default_incomplete" stays incomplete until its first invoice is paid:
    // Stripe hands back that invoice's client secret and the browser pays it with Stripe.js (card, Apple Pay, Google
    // Pay and Link all come out of the card/link types below; Stripe.js decides which the device can show).
    public Task<StartedPayment> CreateSubscriptionPaymentAsync(SubscriptionPaymentRequest r, CancellationToken ct = default) => Call(async () =>
    {
        var metadata = StripeMapping.Metadata(r.UserId, r.Plan, r.Cycle, r.PromoCode);
        var options = new SubscriptionCreateOptions
        {
            Customer = r.CustomerId,
            Items =
            [
                new SubscriptionItemOptions
                {
                    PriceData = new SubscriptionItemPriceDataOptions
                    {
                        Currency = Currency,
                        Product = await ProductAsync(r.Plan, ct),
                        UnitAmount = Money.Satang(r.ChargeOverride ?? Pricing.Period(r.Price, r.Cycle)),
                        Recurring = new SubscriptionItemPriceDataRecurringOptions { Interval = StripeMapping.Interval(r.Cycle) },
                    },
                },
            ],
            PaymentBehavior = "default_incomplete",
            PaymentSettings = new SubscriptionPaymentSettingsOptions
            {
                // The card used now pays the renewals too.
                SaveDefaultPaymentMethod = "on_subscription",
                PaymentMethodTypes = SubscriptionMethods(),
            },
            Metadata = metadata,
            Expand = ["latest_invoice.confirmation_secret"],
        };
        if (r.FirstDiscount > 0)
        {
            var coupon = await new CouponService(Client).CreateAsync(new CouponCreateOptions
            {
                AmountOff = Money.Satang(r.FirstDiscount),
                Currency = Currency,
                Duration = "once",
                MaxRedemptions = 1,
                Name = $"Promo {r.PromoCode}",
                Metadata = new Dictionary<string, string> { [StripeMapping.PromoKey] = r.PromoCode ?? "", [StripeMapping.UserKey] = r.UserId.ToString() },
            }, cancellationToken: ct);
            options.Discounts = [new SubscriptionDiscountOptions { Coupon = coupon.Id }];
        }
        var subscription = await new SubscriptionService(Client).CreateAsync(
            options, new RequestOptions { IdempotencyKey = $"payment-subscription:{r.AttemptId}" }, ct);
        var invoice = subscription.LatestInvoice;
        var secret = invoice?.ConfirmationSecret?.ClientSecret
            ?? throw new PaymentGatewayException("Stripe ไม่ได้ส่งข้อมูลสำหรับชำระเงินกลับมา");
        return new StartedPayment(StripeMapping.IntentIdOf(secret), secret, Money.FromSatang(invoice!.AmountDue), subscription.Id);
    });

    /// <summary>The payment method types of the subscription's first invoice; null leaves the choice to the account's Dashboard settings.</summary>
    private List<string>? SubscriptionMethods()
    {
        var types = (options.SubscriptionPaymentMethods ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return types.Length == 0 ? null : types.Select(t => t.ToLowerInvariant()).Distinct().ToList();
    }

    // PromptPay is single-use at Stripe: it cannot be on a subscription, so it is one PaymentIntent for one period.
    public Task<StartedPayment> CreatePrepaidPaymentAsync(PrepaidPaymentRequest r, CancellationToken ct = default) => Call(async () =>
    {
        var metadata = StripeMapping.Metadata(r.UserId, r.Plan, r.Cycle, r.PromoCode);
        metadata[StripeMapping.KindKey] = StripeMapping.PrepaidKind;
        var intent = await new PaymentIntentService(Client).CreateAsync(new PaymentIntentCreateOptions
        {
            Amount = Money.Satang(r.Amount),
            Currency = Currency,
            Customer = r.CustomerId,
            AllowedPaymentMethodTypes = ["promptpay"],
            ReceiptEmail = r.Email,
            Description = $"AutoPost {r.Plan} ({StripeMapping.Key(r.Cycle)})",
            Metadata = metadata,
        }, new RequestOptions { IdempotencyKey = $"payment-prepaid:{r.AttemptId}" }, ct);
        return new StartedPayment(intent.Id, intent.ClientSecret, r.Amount, null);
    });

    public Task<PaymentIntentSnapshot> GetPaymentIntentAsync(string paymentIntentId, CancellationToken ct = default) => Call(async () =>
        StripeMapping.PaymentIntent(await new PaymentIntentService(Client).GetAsync(
            paymentIntentId, new PaymentIntentGetOptions { Expand = ["latest_charge"] }, cancellationToken: ct)));

    public Task<CheckoutSessionSnapshot> GetCheckoutSessionAsync(string sessionId, CancellationToken ct = default) => Call(async () =>
        StripeMapping.Session(await new Stripe.Checkout.SessionService(Client).GetAsync(sessionId, cancellationToken: ct)));

    public Task<string> CreatePortalAsync(string customerId, string returnUrl, CancellationToken ct = default) => Call(async () =>
    {
        var portal = await new Stripe.BillingPortal.SessionService(Client).CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = customerId,
            ReturnUrl = returnUrl,
            Locale = "th",
            Configuration = string.IsNullOrWhiteSpace(options.PortalConfigurationId) ? null : options.PortalConfigurationId,
        }, cancellationToken: ct);
        return portal.Url;
    });

    public Task<SubscriptionSnapshot> GetSubscriptionAsync(string subscriptionId, CancellationToken ct = default) => Call(async () =>
        StripeMapping.Subscription(await new SubscriptionService(Client).GetAsync(subscriptionId, cancellationToken: ct)));

    public Task<SubscriptionSnapshot> ChangeSubscriptionAsync(
        string subscriptionId, Domain.Enums.PlanKey plan, BillingCycle cycle, int price, int? chargeOverride = null, CancellationToken ct = default) => Call(async () =>
    {
        var subscriptions = new SubscriptionService(Client);
        var current = await subscriptions.GetAsync(subscriptionId, cancellationToken: ct);
        var item = current.Items.Data.FirstOrDefault() ?? throw new PaymentGatewayException("การสมัครสมาชิกนี้ไม่มีรายการสินค้า");
        var updated = await subscriptions.UpdateAsync(subscriptionId, new SubscriptionUpdateOptions
        {
            Items =
            [
                new SubscriptionItemOptions
                {
                    Id = item.Id,
                    PriceData = new SubscriptionItemPriceDataOptions
                    {
                        Currency = Currency,
                        Product = await ProductAsync(plan, ct),
                        UnitAmount = Money.Satang(chargeOverride ?? Pricing.Period(price, cycle)),
                        Recurring = new SubscriptionItemPriceDataRecurringOptions { Interval = StripeMapping.Interval(cycle) },
                    },
                },
            ],
            // The difference is invoiced and collected now; if the card declines the plan does not move.
            ProrationBehavior = "always_invoice",
            PaymentBehavior = "error_if_incomplete",
            CancelAtPeriodEnd = false,
            // Merged into the subscription's metadata: the user id set at checkout stays.
            Metadata = new Dictionary<string, string> { [StripeMapping.PlanKey] = StripeMapping.Key(plan), [StripeMapping.CycleKey] = StripeMapping.Key(cycle) },
        }, cancellationToken: ct);
        return StripeMapping.Subscription(updated);
    });

    public Task<SubscriptionSnapshot> SetCancelAtPeriodEndAsync(string subscriptionId, bool cancel, CancellationToken ct = default) => Call(async () =>
    {
        var update = new SubscriptionUpdateOptions { CancelAtPeriodEnd = cancel };
        if (!cancel) update.AddExtraParam("cancel_at", ""); // a date the portal set is cleared too
        return StripeMapping.Subscription(await new SubscriptionService(Client).UpdateAsync(subscriptionId, update, cancellationToken: ct));
    });

    public Task SetBillingPausedAsync(string subscriptionId, bool paused, CancellationToken ct = default) => Call(async () =>
    {
        var update = new SubscriptionUpdateOptions();
        // "void": invoices made while paused are voided, not collected and not owed later.
        if (paused) update.PauseCollection = new SubscriptionPauseCollectionOptions { Behavior = "void" };
        else update.AddExtraParam("pause_collection", "");
        await new SubscriptionService(Client).UpdateAsync(subscriptionId, update, cancellationToken: ct);
        return true;
    });

    public async Task<CardSnapshot?> GetCardAsync(string customerId, CancellationToken ct = default)
    {
        if (!Enabled) return null;
        try
        {
            var customer = await new CustomerService(Client).GetAsync(
                customerId, new CustomerGetOptions { Expand = ["invoice_settings.default_payment_method"] }, cancellationToken: ct);
            var method = customer.InvoiceSettings?.DefaultPaymentMethod;
            method ??= (await new PaymentMethodService(Client).ListAsync(
                new PaymentMethodListOptions { Customer = customerId, Type = "card", Limit = 1 }, cancellationToken: ct)).FirstOrDefault();
            return method?.Card is { } card ? new CardSnapshot(card.Brand, card.Last4, (int)card.ExpMonth, (int)card.ExpYear) : null;
        }
        catch (StripeException ex)
        {
            // The card is only decoration on the billing page: it must not break it.
            log.LogWarning(ex, "Could not read the card of Stripe customer {Customer}", customerId);
            return null;
        }
    }

    public Task<string?> GetInvoicePaymentIntentAsync(string invoiceId, CancellationToken ct = default) => Call(async () =>
    {
        var payments = await new InvoicePaymentService(Client).ListAsync(new InvoicePaymentListOptions { Invoice = invoiceId }, cancellationToken: ct);
        return payments
            .Where(p => p.Payment?.Type == "payment_intent")
            .OrderByDescending(p => p.Status == "paid")
            .Select(p => p.Payment.PaymentIntentId)
            .FirstOrDefault();
    });

    public Task<RefundSnapshot> RefundAsync(string paymentIntentId, decimal amount, string idempotencyKey, CancellationToken ct = default) => Call(async () =>
        StripeMapping.Refund(await new RefundService(Client).CreateAsync(
            new RefundCreateOptions { PaymentIntent = paymentIntentId, Amount = Money.Satang(amount), Reason = "requested_by_customer" },
            new RequestOptions { IdempotencyKey = idempotencyKey }, ct)));

    public Task<InvoiceSnapshot> PayInvoiceAsync(string invoiceId, CancellationToken ct = default) => Call(async () =>
        StripeMapping.Invoice(await new InvoiceService(Client).PayAsync(invoiceId, cancellationToken: ct), paid: true));

    // One Stripe product per plan, created on first use under a fixed id; Stripe prices are sent inline.
    private async Task<string> ProductAsync(Domain.Enums.PlanKey plan, CancellationToken ct)
    {
        if (products.TryGetValue(plan, out var known)) return known;
        var id = $"autopost_{StripeMapping.Key(plan)}";
        var service = new ProductService(Client);
        try
        {
            await service.GetAsync(id, cancellationToken: ct);
        }
        catch (StripeException ex) when (ex.StripeError?.Code == "resource_missing")
        {
            try
            {
                await service.CreateAsync(new ProductCreateOptions { Id = id, Name = $"AutoPost {plan}" }, cancellationToken: ct);
            }
            catch (StripeException created) when (created.StripeError?.Code == "resource_already_exists")
            {
                // Another request made it first.
            }
        }
        products[plan] = id;
        return id;
    }

    private async Task<Stripe.Checkout.SessionLineItemPriceDataOptions> PriceDataAsync(
        Domain.Enums.PlanKey plan, BillingCycle cycle, int price, int? chargeOverride, CancellationToken ct) => new()
    {
        Currency = Currency,
        Product = await ProductAsync(plan, ct),
        // The admin's test amount (PaymentOverride) replaces the price of the period; the plan the customer gets is the same.
        UnitAmount = Money.Satang(chargeOverride ?? Pricing.Period(price, cycle)),
        Recurring = new Stripe.Checkout.SessionLineItemPriceDataRecurringOptions { Interval = StripeMapping.Interval(cycle) },
    };

    private async Task<T> Call<T>(Func<Task<T>> action)
    {
        _ = Client; // fails with "not ready" before anything else when no key is set
        try
        {
            return await action();
        }
        catch (StripeException ex) when (ex.StripeError?.Type == "card_error")
        {
            throw new DomainException($"ชำระเงินไม่สำเร็จ: {ex.StripeError.Message}");
        }
        catch (StripeException ex)
        {
            log.LogError(ex, "Stripe call failed: {Code} {Message}", ex.StripeError?.Code, ex.Message);
            throw new PaymentGatewayException("ติดต่อ Stripe ไม่ได้ในตอนนี้ ลองใหม่อีกครั้ง", ex);
        }
    }
}
