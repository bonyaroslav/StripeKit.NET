// Purpose: Verify + dedupe Stripe webhooks and converge payment/subscription status.
// Must-not-break: raw-body verification, event.id idempotency, replay-safe handlers.
// See: docs/plan.md and module README for invariants.
using System;
using System.Threading;
using System.Threading.Tasks;

namespace StripeKit;

public sealed class StripeWebhookProcessor
{
    private readonly WebhookSignatureVerifier _signatureVerifier;
    private readonly IWebhookEventStore _eventStore;
    private readonly IPaymentRecordStore _paymentRecords;
    private readonly ISubscriptionRecordStore _subscriptionRecords;
    private readonly IStripeObjectLookup _objectLookup;
    private readonly StripeKitOptions _options;

    public StripeWebhookProcessor(
        WebhookSignatureVerifier signatureVerifier,
        IWebhookEventStore eventStore,
        IPaymentRecordStore paymentRecords,
        ISubscriptionRecordStore subscriptionRecords,
        IStripeObjectLookup objectLookup,
        StripeKitOptions options)
    {
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _paymentRecords = paymentRecords ?? throw new ArgumentNullException(nameof(paymentRecords));
        _subscriptionRecords = subscriptionRecords ?? throw new ArgumentNullException(nameof(subscriptionRecords));
        _objectLookup = objectLookup ?? throw new ArgumentNullException(nameof(objectLookup));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<WebhookProcessingResult> ProcessAsync(
        string payload,
        string signatureHeader,
        string secret,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EnableWebhooks)
        {
            throw new InvalidOperationException("Webhooks module is disabled.");
        }

        StripeWebhookEvent stripeEvent = _signatureVerifier.VerifyAndParse(payload, signatureHeader, secret);

        bool started = await _eventStore.TryBeginAsync(stripeEvent.Id).ConfigureAwait(false);
        if (!started)
        {
            WebhookEventOutcome? existing = await _eventStore.GetOutcomeAsync(stripeEvent.Id).ConfigureAwait(false);
            if (existing == null)
            {
                existing = new WebhookEventOutcome(false, "Duplicate event without recorded outcome.", DateTimeOffset.UtcNow);
            }

            return new WebhookProcessingResult(stripeEvent.Id, stripeEvent.Type, existing, true);
        }

        StripeWebhookEventData data = StripeWebhookEventData.Parse(payload);
        WebhookEventOutcome outcome = await ProcessInternalAsync(data, cancellationToken).ConfigureAwait(false);

        await _eventStore.RecordOutcomeAsync(stripeEvent.Id, outcome).ConfigureAwait(false);

        return new WebhookProcessingResult(stripeEvent.Id, stripeEvent.Type, outcome, false);
    }

    public async Task<WebhookProcessingResult> ProcessStripeEventAsync(
        Stripe.Event stripeEvent,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EnableWebhooks)
        {
            throw new InvalidOperationException("Webhooks module is disabled.");
        }

        if (stripeEvent == null)
        {
            throw new ArgumentNullException(nameof(stripeEvent));
        }

        if (string.IsNullOrWhiteSpace(stripeEvent.Id))
        {
            throw new ArgumentException("Event ID is required.", nameof(stripeEvent));
        }

        if (string.IsNullOrWhiteSpace(stripeEvent.Type))
        {
            throw new ArgumentException("Event type is required.", nameof(stripeEvent));
        }

        bool started = await _eventStore.TryBeginAsync(stripeEvent.Id).ConfigureAwait(false);
        if (!started)
        {
            WebhookEventOutcome? existing = await _eventStore.GetOutcomeAsync(stripeEvent.Id).ConfigureAwait(false);
            if (existing == null)
            {
                existing = new WebhookEventOutcome(false, "Duplicate event without recorded outcome.", DateTimeOffset.UtcNow);
            }

            return new WebhookProcessingResult(stripeEvent.Id, stripeEvent.Type, existing, true);
        }

        StripeWebhookEventData data = StripeWebhookEventData.FromEvent(stripeEvent);
        WebhookEventOutcome outcome = await ProcessInternalAsync(data, cancellationToken).ConfigureAwait(false);

        await _eventStore.RecordOutcomeAsync(stripeEvent.Id, outcome).ConfigureAwait(false);

        return new WebhookProcessingResult(stripeEvent.Id, stripeEvent.Type, outcome, false);
    }

    private async Task<WebhookEventOutcome> ProcessInternalAsync(
        StripeWebhookEventData data,
        CancellationToken cancellationToken)
    {
        DateTimeOffset recordedAt = DateTimeOffset.UtcNow;

        try
        {
            await ApplyEventAsync(data, cancellationToken).ConfigureAwait(false);
            return new WebhookEventOutcome(true, null, recordedAt);
        }
        catch (Exception ex)
        {
            return new WebhookEventOutcome(false, ex.Message, recordedAt);
        }
    }

    private async Task ApplyEventAsync(StripeWebhookEventData data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (data.Type)
        {
            case "payment_intent.succeeded":
                if (_options.EnablePayments)
                {
                    string paymentIntentId = await ResolvePaymentIntentIdAsync(data).ConfigureAwait(false);
                    await UpdatePaymentStatusAsync(paymentIntentId, PaymentStatus.Succeeded).ConfigureAwait(false);
                }
                break;
            case "payment_intent.payment_failed":
                if (_options.EnablePayments)
                {
                    string paymentIntentId = await ResolvePaymentIntentIdAsync(data).ConfigureAwait(false);
                    await UpdatePaymentStatusAsync(paymentIntentId, PaymentStatus.Failed).ConfigureAwait(false);
                }
                break;
            case "customer.subscription.deleted":
                if (_options.EnableBilling)
                {
                    string subscriptionId = await ResolveSubscriptionIdAsync(data).ConfigureAwait(false);
                    await UpdateSubscriptionStatusAsync(subscriptionId, SubscriptionStatus.Canceled).ConfigureAwait(false);
                }
                break;
            case "invoice.payment_succeeded":
                if (_options.EnableBilling)
                {
                    string subscriptionId = await ResolveSubscriptionIdAsync(data).ConfigureAwait(false);
                    await UpdateSubscriptionStatusAsync(subscriptionId, SubscriptionStatus.Active).ConfigureAwait(false);
                }
                break;
            case "invoice.payment_failed":
                if (_options.EnableBilling)
                {
                    string subscriptionId = await ResolveSubscriptionIdAsync(data).ConfigureAwait(false);
                    await UpdateSubscriptionStatusAsync(subscriptionId, SubscriptionStatus.PastDue).ConfigureAwait(false);
                }
                break;
            case "customer.subscription.created":
            case "customer.subscription.updated":
                if (_options.EnableBilling)
                {
                    if (TryMapSubscriptionStatus(data.ObjectStatus, out SubscriptionStatus status))
                    {
                        string subscriptionId = await ResolveSubscriptionIdAsync(data).ConfigureAwait(false);
                        await UpdateSubscriptionStatusAsync(subscriptionId, status).ConfigureAwait(false);
                    }
                }
                break;
        }
    }

    private async Task<string> ResolvePaymentIntentIdAsync(StripeWebhookEventData data)
    {
        if (!string.IsNullOrWhiteSpace(data.PaymentIntentId))
        {
            return data.PaymentIntentId;
        }

        if (string.IsNullOrWhiteSpace(data.ObjectId))
        {
            throw new InvalidOperationException("Missing payment_intent id.");
        }

        string? paymentIntentId = await _objectLookup.GetPaymentIntentIdAsync(data.ObjectId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(paymentIntentId))
        {
            throw new InvalidOperationException("Missing payment_intent id.");
        }

        return paymentIntentId;
    }

    private async Task<string> ResolveSubscriptionIdAsync(StripeWebhookEventData data)
    {
        if (!string.IsNullOrWhiteSpace(data.SubscriptionId))
        {
            return data.SubscriptionId;
        }

        if (string.IsNullOrWhiteSpace(data.ObjectId))
        {
            throw new InvalidOperationException("Missing subscription id.");
        }

        string? subscriptionId = await _objectLookup.GetSubscriptionIdAsync(data.ObjectId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            throw new InvalidOperationException("Missing subscription id.");
        }

        return subscriptionId;
    }

    private async Task UpdatePaymentStatusAsync(string? paymentIntentId, PaymentStatus status)
    {
        if (string.IsNullOrWhiteSpace(paymentIntentId))
        {
            throw new InvalidOperationException("Missing payment_intent id.");
        }

        PaymentRecord? record = await _paymentRecords.GetByPaymentIntentIdAsync(paymentIntentId).ConfigureAwait(false);
        if (record == null)
        {
            throw new InvalidOperationException("Payment record not found for payment_intent id.");
        }

        PaymentRecord updated = new PaymentRecord(
            record.UserId,
            record.BusinessPaymentId,
            status,
            record.PaymentIntentId,
            record.ChargeId);

        await _paymentRecords.SaveAsync(updated).ConfigureAwait(false);
    }

    private async Task UpdateSubscriptionStatusAsync(string? subscriptionId, SubscriptionStatus status)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            throw new InvalidOperationException("Missing subscription id.");
        }

        SubscriptionRecord? record = await _subscriptionRecords.GetBySubscriptionIdAsync(subscriptionId).ConfigureAwait(false);
        if (record == null)
        {
            throw new InvalidOperationException("Subscription record not found for subscription id.");
        }

        SubscriptionRecord updated = new SubscriptionRecord(
            record.UserId,
            record.BusinessSubscriptionId,
            status,
            record.CustomerId,
            record.SubscriptionId);

        await _subscriptionRecords.SaveAsync(updated).ConfigureAwait(false);
    }

    private static bool TryMapSubscriptionStatus(string? status, out SubscriptionStatus mapped)
    {
        mapped = default;

        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "trialing", StringComparison.OrdinalIgnoreCase))
        {
            mapped = SubscriptionStatus.Active;
            return true;
        }

        if (string.Equals(status, "past_due", StringComparison.OrdinalIgnoreCase))
        {
            mapped = SubscriptionStatus.PastDue;
            return true;
        }

        if (string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase))
        {
            mapped = SubscriptionStatus.Incomplete;
            return true;
        }

        if (string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase))
        {
            mapped = SubscriptionStatus.Canceled;
            return true;
        }

        return false;
    }
}

public sealed class WebhookProcessingResult
{
    public WebhookProcessingResult(string eventId, string eventType, WebhookEventOutcome? outcome, bool isDuplicate)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new ArgumentException("Event ID is required.", nameof(eventId));
        }

        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new ArgumentException("Event type is required.", nameof(eventType));
        }

        EventId = eventId;
        EventType = eventType;
        Outcome = outcome;
        IsDuplicate = isDuplicate;
    }

    public string EventId { get; }
    public string EventType { get; }
    public WebhookEventOutcome? Outcome { get; }
    public bool IsDuplicate { get; }
}
