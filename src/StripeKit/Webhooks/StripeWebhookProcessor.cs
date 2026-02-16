// Purpose: Verify + dedupe Stripe webhooks and converge payment/subscription status.
// Must-not-break: raw-body verification, event.id idempotency, replay-safe handlers.
// See: docs/plan.md and module README for invariants.
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace StripeKit;

public sealed class StripeWebhookProcessor
{
    private readonly WebhookSignatureVerifier _signatureVerifier;
    private readonly IWebhookEventStore _eventStore;
    private readonly IPaymentRecordStore _paymentRecords;
    private readonly ISubscriptionRecordStore _subscriptionRecords;
    private readonly IRefundRecordStore _refundRecords;
    private readonly IStripeObjectLookup _objectLookup;
    private readonly StripeKitOptions _options;

    public StripeWebhookProcessor(
        WebhookSignatureVerifier signatureVerifier,
        IWebhookEventStore eventStore,
        IPaymentRecordStore paymentRecords,
        ISubscriptionRecordStore subscriptionRecords,
        IRefundRecordStore refundRecords,
        IStripeObjectLookup objectLookup,
        StripeKitOptions options)
    {
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _paymentRecords = paymentRecords ?? throw new ArgumentNullException(nameof(paymentRecords));
        _subscriptionRecords = subscriptionRecords ?? throw new ArgumentNullException(nameof(subscriptionRecords));
        _refundRecords = refundRecords ?? throw new ArgumentNullException(nameof(refundRecords));
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
        using Activity? activity = StripeKitDiagnostics.ActivitySource.StartActivity("stripekit.webhook.process");
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.EventId, stripeEvent.Id);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.EventType, stripeEvent.Type);

        bool started = await _eventStore.TryBeginAsync(stripeEvent.Id).ConfigureAwait(false);
        if (!started)
        {
            activity?.SetTag("duplicate", true);
            WebhookEventOutcome? existing = await _eventStore.GetOutcomeAsync(stripeEvent.Id).ConfigureAwait(false);
            bool terminalDuplicate = IsTerminalDuplicate(existing);
            if (!terminalDuplicate)
            {
                WebhookEventOutcome retryable = CreateRetryableDuplicateOutcome(existing);
                StripeKitDiagnostics.EmitLog(
                    "webhook.processed",
                    (StripeKitDiagnosticTags.EventId, stripeEvent.Id),
                    (StripeKitDiagnosticTags.EventType, stripeEvent.Type),
                    ("duplicate", true),
                    ("terminal_duplicate", false),
                    ("succeeded", retryable.Succeeded),
                    ("error", retryable.ErrorMessage));

                return new WebhookProcessingResult(stripeEvent.Id, stripeEvent.Type, retryable, false);
            }

            StripeKitDiagnostics.EmitLog(
                "webhook.processed",
                (StripeKitDiagnosticTags.EventId, stripeEvent.Id),
                (StripeKitDiagnosticTags.EventType, stripeEvent.Type),
                ("duplicate", true),
                ("terminal_duplicate", true),
                ("succeeded", existing!.Succeeded),
                ("error", existing.ErrorMessage));

            return new WebhookProcessingResult(stripeEvent.Id, stripeEvent.Type, existing, true);
        }

        StripeWebhookEventData data = StripeWebhookEventData.Parse(payload);
        AddDataTags(activity, data);
        WebhookEventOutcome outcome = await ProcessInternalAsync(data, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("succeeded", outcome.Succeeded);
        StripeKitDiagnostics.SetTag(activity, "error", outcome.ErrorMessage);

        await _eventStore.RecordOutcomeAsync(stripeEvent.Id, outcome).ConfigureAwait(false);
        StripeKitDiagnostics.EmitLog(
            "webhook.processed",
            (StripeKitDiagnosticTags.EventId, stripeEvent.Id),
            (StripeKitDiagnosticTags.EventType, stripeEvent.Type),
            (StripeKitDiagnosticTags.UserId, GetTagValue(activity, StripeKitDiagnosticTags.UserId)),
            (StripeKitDiagnosticTags.BusinessPaymentId, GetTagValue(activity, StripeKitDiagnosticTags.BusinessPaymentId)),
            (StripeKitDiagnosticTags.BusinessSubscriptionId, GetTagValue(activity, StripeKitDiagnosticTags.BusinessSubscriptionId)),
            (StripeKitDiagnosticTags.BusinessRefundId, GetTagValue(activity, StripeKitDiagnosticTags.BusinessRefundId)),
            (StripeKitDiagnosticTags.PaymentIntentId, data.PaymentIntentId),
            (StripeKitDiagnosticTags.SubscriptionId, data.SubscriptionId),
            (StripeKitDiagnosticTags.InvoiceId, string.Equals(data.ObjectType, "invoice", StringComparison.Ordinal) ? data.ObjectId : null),
            (StripeKitDiagnosticTags.RefundId, data.RefundId),
            ("duplicate", false),
            ("succeeded", outcome.Succeeded),
            ("error", outcome.ErrorMessage));

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

        using Activity? activity = StripeKitDiagnostics.ActivitySource.StartActivity("stripekit.webhook.process");
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.EventId, stripeEvent.Id);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.EventType, stripeEvent.Type);

        bool started = await _eventStore.TryBeginAsync(stripeEvent.Id).ConfigureAwait(false);
        if (!started)
        {
            activity?.SetTag("duplicate", true);
            WebhookEventOutcome? existing = await _eventStore.GetOutcomeAsync(stripeEvent.Id).ConfigureAwait(false);
            bool terminalDuplicate = IsTerminalDuplicate(existing);
            if (!terminalDuplicate)
            {
                WebhookEventOutcome retryable = CreateRetryableDuplicateOutcome(existing);
                StripeKitDiagnostics.EmitLog(
                    "webhook.processed",
                    (StripeKitDiagnosticTags.EventId, stripeEvent.Id),
                    (StripeKitDiagnosticTags.EventType, stripeEvent.Type),
                    ("duplicate", true),
                    ("terminal_duplicate", false),
                    ("succeeded", retryable.Succeeded),
                    ("error", retryable.ErrorMessage));

                return new WebhookProcessingResult(stripeEvent.Id, stripeEvent.Type, retryable, false);
            }

            StripeKitDiagnostics.EmitLog(
                "webhook.processed",
                (StripeKitDiagnosticTags.EventId, stripeEvent.Id),
                (StripeKitDiagnosticTags.EventType, stripeEvent.Type),
                ("duplicate", true),
                ("terminal_duplicate", true),
                ("succeeded", existing!.Succeeded),
                ("error", existing.ErrorMessage));

            return new WebhookProcessingResult(stripeEvent.Id, stripeEvent.Type, existing, true);
        }

        StripeWebhookEventData data = StripeWebhookEventData.FromEvent(stripeEvent);
        AddDataTags(activity, data);
        WebhookEventOutcome outcome = await ProcessInternalAsync(data, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("succeeded", outcome.Succeeded);
        StripeKitDiagnostics.SetTag(activity, "error", outcome.ErrorMessage);

        await _eventStore.RecordOutcomeAsync(stripeEvent.Id, outcome).ConfigureAwait(false);
        StripeKitDiagnostics.EmitLog(
            "webhook.processed",
            (StripeKitDiagnosticTags.EventId, stripeEvent.Id),
            (StripeKitDiagnosticTags.EventType, stripeEvent.Type),
            (StripeKitDiagnosticTags.UserId, GetTagValue(activity, StripeKitDiagnosticTags.UserId)),
            (StripeKitDiagnosticTags.BusinessPaymentId, GetTagValue(activity, StripeKitDiagnosticTags.BusinessPaymentId)),
            (StripeKitDiagnosticTags.BusinessSubscriptionId, GetTagValue(activity, StripeKitDiagnosticTags.BusinessSubscriptionId)),
            (StripeKitDiagnosticTags.BusinessRefundId, GetTagValue(activity, StripeKitDiagnosticTags.BusinessRefundId)),
            (StripeKitDiagnosticTags.PaymentIntentId, data.PaymentIntentId),
            (StripeKitDiagnosticTags.SubscriptionId, data.SubscriptionId),
            (StripeKitDiagnosticTags.InvoiceId, string.Equals(data.ObjectType, "invoice", StringComparison.Ordinal) ? data.ObjectId : null),
            (StripeKitDiagnosticTags.RefundId, data.RefundId),
            ("duplicate", false),
            ("succeeded", outcome.Succeeded),
            ("error", outcome.ErrorMessage));

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
                    await UpdatePaymentStatusAsync(paymentIntentId, PaymentStatus.Succeeded, data.EventCreated).ConfigureAwait(false);
                }
                break;
            case "payment_intent.payment_failed":
                if (_options.EnablePayments)
                {
                    string paymentIntentId = await ResolvePaymentIntentIdAsync(data).ConfigureAwait(false);
                    await UpdatePaymentStatusAsync(paymentIntentId, PaymentStatus.Failed, data.EventCreated).ConfigureAwait(false);
                }
                break;
            case "customer.subscription.deleted":
                if (_options.EnableBilling)
                {
                    string subscriptionId = await ResolveSubscriptionIdAsync(data).ConfigureAwait(false);
                    await UpdateSubscriptionStatusAsync(subscriptionId, SubscriptionStatus.Canceled, data.EventCreated).ConfigureAwait(false);
                }
                break;
            case "invoice.payment_succeeded":
                if (_options.EnableBilling)
                {
                    string subscriptionId = await ResolveSubscriptionIdAsync(data).ConfigureAwait(false);
                    await UpdateSubscriptionStatusAsync(subscriptionId, SubscriptionStatus.Active, data.EventCreated).ConfigureAwait(false);
                }
                break;
            case "invoice.payment_failed":
                if (_options.EnableBilling)
                {
                    string subscriptionId = await ResolveSubscriptionIdAsync(data).ConfigureAwait(false);
                    await UpdateSubscriptionStatusAsync(subscriptionId, SubscriptionStatus.PastDue, data.EventCreated).ConfigureAwait(false);
                }
                break;
            case "customer.subscription.created":
            case "customer.subscription.updated":
                if (_options.EnableBilling)
                {
                    if (TryMapSubscriptionStatus(data.ObjectStatus, out SubscriptionStatus status))
                    {
                        string subscriptionId = await ResolveSubscriptionIdAsync(data).ConfigureAwait(false);
                        await UpdateSubscriptionStatusAsync(subscriptionId, status, data.EventCreated).ConfigureAwait(false);
                    }
                }
                break;
            case "refund.created":
            case "refund.updated":
                if (_options.EnableRefunds)
                {
                    if (TryMapRefundStatus(data.ObjectStatus, out RefundStatus status))
                    {
                        string refundId = ResolveRefundId(data);
                        await UpdateRefundStatusAsync(refundId, status).ConfigureAwait(false);
                    }
                }
                break;
            case "refund.failed":
                if (_options.EnableRefunds)
                {
                    string refundId = ResolveRefundId(data);
                    await UpdateRefundStatusAsync(refundId, RefundStatus.Failed).ConfigureAwait(false);
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

    private async Task UpdatePaymentStatusAsync(string? paymentIntentId, PaymentStatus status, DateTimeOffset? eventCreated)
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

        if (!ShouldApplyPaymentStatus(record, status, eventCreated))
        {
            return;
        }

        PaymentRecord updated = new PaymentRecord(
            record.UserId,
            record.BusinessPaymentId,
            status,
            record.PaymentIntentId,
            record.ChargeId,
            record.PromotionOutcome,
            record.PromotionCouponId,
            record.PromotionCodeId,
            eventCreated ?? record.LastStripeEventCreated);

        StripeKitDiagnostics.SetTag(Activity.Current, StripeKitDiagnosticTags.UserId, record.UserId);
        StripeKitDiagnostics.SetTag(Activity.Current, StripeKitDiagnosticTags.BusinessPaymentId, record.BusinessPaymentId);
        StripeKitDiagnostics.SetTag(Activity.Current, StripeKitDiagnosticTags.PaymentIntentId, record.PaymentIntentId);

        await _paymentRecords.SaveAsync(updated).ConfigureAwait(false);
    }

    private async Task UpdateSubscriptionStatusAsync(string? subscriptionId, SubscriptionStatus status, DateTimeOffset? eventCreated)
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

        if (!ShouldApplySubscriptionStatus(record, status, eventCreated))
        {
            return;
        }

        SubscriptionRecord updated = new SubscriptionRecord(
            record.UserId,
            record.BusinessSubscriptionId,
            status,
            record.CustomerId,
            record.SubscriptionId,
            record.PromotionOutcome,
            record.PromotionCouponId,
            record.PromotionCodeId,
            eventCreated ?? record.LastStripeEventCreated);

        StripeKitDiagnostics.SetTag(Activity.Current, StripeKitDiagnosticTags.UserId, record.UserId);
        StripeKitDiagnostics.SetTag(Activity.Current, StripeKitDiagnosticTags.BusinessSubscriptionId, record.BusinessSubscriptionId);
        StripeKitDiagnostics.SetTag(Activity.Current, StripeKitDiagnosticTags.SubscriptionId, record.SubscriptionId);
        StripeKitDiagnostics.SetTag(Activity.Current, StripeKitDiagnosticTags.CustomerId, record.CustomerId);

        await _subscriptionRecords.SaveAsync(updated).ConfigureAwait(false);
    }

    private async Task UpdateRefundStatusAsync(string? refundId, RefundStatus status)
    {
        if (string.IsNullOrWhiteSpace(refundId))
        {
            throw new InvalidOperationException("Missing refund id.");
        }

        RefundRecord? record = await _refundRecords.GetByRefundIdAsync(refundId).ConfigureAwait(false);
        if (record == null)
        {
            throw new InvalidOperationException("Refund record not found for refund id.");
        }

        RefundRecord updated = new RefundRecord(
            record.UserId,
            record.BusinessRefundId,
            record.BusinessPaymentId,
            status,
            record.PaymentIntentId,
            record.RefundId);

        StripeKitDiagnostics.SetTag(Activity.Current, StripeKitDiagnosticTags.UserId, record.UserId);
        StripeKitDiagnostics.SetTag(Activity.Current, StripeKitDiagnosticTags.BusinessRefundId, record.BusinessRefundId);
        StripeKitDiagnostics.SetTag(Activity.Current, StripeKitDiagnosticTags.BusinessPaymentId, record.BusinessPaymentId);
        StripeKitDiagnostics.SetTag(Activity.Current, StripeKitDiagnosticTags.RefundId, record.RefundId);
        StripeKitDiagnostics.SetTag(Activity.Current, StripeKitDiagnosticTags.PaymentIntentId, record.PaymentIntentId);

        await _refundRecords.SaveAsync(updated).ConfigureAwait(false);
    }

    private static string ResolveRefundId(StripeWebhookEventData data)
    {
        if (!string.IsNullOrWhiteSpace(data.RefundId))
        {
            return data.RefundId;
        }

        if (!string.IsNullOrWhiteSpace(data.ObjectId))
        {
            return data.ObjectId;
        }

        throw new InvalidOperationException("Missing refund id.");
    }

    private static void AddDataTags(Activity? activity, StripeWebhookEventData data)
    {
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.StripeObjectId, data.ObjectId);
        if (string.Equals(data.ObjectType, "invoice", StringComparison.Ordinal))
        {
            StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.InvoiceId, data.ObjectId);
        }

        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.PaymentIntentId, data.PaymentIntentId);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.SubscriptionId, data.SubscriptionId);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.RefundId, data.RefundId);
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

    private static bool TryMapRefundStatus(string? status, out RefundStatus mapped)
    {
        mapped = default;

        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        if (string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
        {
            mapped = RefundStatus.Succeeded;
            return true;
        }

        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            mapped = RefundStatus.Failed;
            return true;
        }

        if (string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            mapped = RefundStatus.Pending;
            return true;
        }

        return false;
    }

    private static bool ShouldApplyPaymentStatus(PaymentRecord record, PaymentStatus incomingStatus, DateTimeOffset? eventCreated)
    {
        if (record.Status == PaymentStatus.Canceled && incomingStatus != PaymentStatus.Canceled)
        {
            return false;
        }

        if (record.Status == PaymentStatus.Succeeded && incomingStatus != PaymentStatus.Succeeded)
        {
            return false;
        }

        if (!record.LastStripeEventCreated.HasValue || !eventCreated.HasValue)
        {
            return true;
        }

        if (eventCreated.Value < record.LastStripeEventCreated.Value)
        {
            return false;
        }

        if (eventCreated.Value == record.LastStripeEventCreated.Value)
        {
            return GetPaymentStatusPrecedence(incomingStatus) >= GetPaymentStatusPrecedence(record.Status);
        }

        return true;
    }

    private static bool ShouldApplySubscriptionStatus(SubscriptionRecord record, SubscriptionStatus incomingStatus, DateTimeOffset? eventCreated)
    {
        if (record.Status == SubscriptionStatus.Canceled && incomingStatus != SubscriptionStatus.Canceled)
        {
            return false;
        }

        if (!record.LastStripeEventCreated.HasValue || !eventCreated.HasValue)
        {
            return true;
        }

        if (eventCreated.Value < record.LastStripeEventCreated.Value)
        {
            return false;
        }

        if (eventCreated.Value == record.LastStripeEventCreated.Value)
        {
            return GetSubscriptionStatusPrecedence(incomingStatus) >= GetSubscriptionStatusPrecedence(record.Status);
        }

        return true;
    }

    private static int GetPaymentStatusPrecedence(PaymentStatus status)
    {
        switch (status)
        {
            case PaymentStatus.Pending:
                return 0;
            case PaymentStatus.Failed:
                return 1;
            case PaymentStatus.Succeeded:
                return 2;
            case PaymentStatus.Canceled:
                return 3;
            default:
                return -1;
        }
    }

    private static int GetSubscriptionStatusPrecedence(SubscriptionStatus status)
    {
        switch (status)
        {
            case SubscriptionStatus.Incomplete:
                return 0;
            case SubscriptionStatus.PastDue:
                return 1;
            case SubscriptionStatus.Active:
                return 2;
            case SubscriptionStatus.Canceled:
                return 3;
            default:
                return -1;
        }
    }

    private static string? GetTagValue(Activity? activity, string key)
    {
        if (activity == null)
        {
            return null;
        }

        foreach (KeyValuePair<string, object?> tag in activity.TagObjects)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal))
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }

    private static bool IsTerminalDuplicate(WebhookEventOutcome? existingOutcome)
    {
        return existingOutcome != null && existingOutcome.Succeeded;
    }

    private static WebhookEventOutcome CreateRetryableDuplicateOutcome(WebhookEventOutcome? existingOutcome)
    {
        if (existingOutcome != null && !existingOutcome.Succeeded)
        {
            return existingOutcome;
        }

        return new WebhookEventOutcome(
            false,
            "Event is already processing. Retry delivery.",
            DateTimeOffset.UtcNow);
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
