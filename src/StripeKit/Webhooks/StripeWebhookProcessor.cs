// Purpose: Verify + dedupe Stripe webhooks and converge payment/subscription status.
// Must-not-break: raw-body verification, event.id idempotency, replay-safe handlers.
// See: docs/plan.md and module README for invariants.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace StripeKit;

public sealed class StripeWebhookProcessor
{
    private readonly WebhookSignatureVerifier _signatureVerifier;
    private readonly IWebhookEventStore _eventStore;
    private readonly StripeKitOptions _options;
    private readonly PaymentWebhookApplicator _paymentApplicator;
    private readonly SubscriptionWebhookApplicator _subscriptionApplicator;
    private readonly RefundWebhookApplicator _refundApplicator;

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
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _paymentApplicator = new PaymentWebhookApplicator(
            paymentRecords ?? throw new ArgumentNullException(nameof(paymentRecords)),
            objectLookup ?? throw new ArgumentNullException(nameof(objectLookup)));
        _subscriptionApplicator = new SubscriptionWebhookApplicator(
            subscriptionRecords ?? throw new ArgumentNullException(nameof(subscriptionRecords)),
            objectLookup);
        _refundApplicator = new RefundWebhookApplicator(refundRecords ?? throw new ArgumentNullException(nameof(refundRecords)));
    }

    public Task<WebhookProcessingResult> ProcessAsync(
        string payload,
        string signatureHeader,
        string secret,
        CancellationToken cancellationToken = default)
    {
        EnsureWebhooksEnabled();

        StripeWebhookEvent stripeEvent = _signatureVerifier.VerifyAndParse(payload, signatureHeader, secret);

        return ProcessEventCoreAsync(
            stripeEvent.Id,
            stripeEvent.Type,
            () => StripeWebhookEventData.Parse(payload),
            cancellationToken);
    }

    public Task<WebhookProcessingResult> ProcessStripeEventAsync(
        Stripe.Event stripeEvent,
        CancellationToken cancellationToken = default)
    {
        EnsureWebhooksEnabled();

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

        return ProcessEventCoreAsync(
            stripeEvent.Id,
            stripeEvent.Type,
            () => StripeWebhookEventData.FromEvent(stripeEvent),
            cancellationToken);
    }

    private void EnsureWebhooksEnabled()
    {
        if (!_options.EnableWebhooks)
        {
            throw new InvalidOperationException("Webhooks module is disabled.");
        }
    }

    private async Task<WebhookProcessingResult> ProcessEventCoreAsync(
        string eventId,
        string eventType,
        Func<StripeWebhookEventData> dataFactory,
        CancellationToken cancellationToken)
    {
        using Activity? activity = StripeKitDiagnostics.ActivitySource.StartActivity("stripekit.webhook.process");
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.EventId, eventId);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.EventType, eventType);

        WebhookProcessingResult? duplicateResult = await TryHandleDuplicateAsync(eventId, eventType, activity).ConfigureAwait(false);
        if (duplicateResult != null)
        {
            return duplicateResult;
        }

        StripeWebhookEventData data = dataFactory();
        AddDataTags(activity, data);
        WebhookEventOutcome outcome = await ProcessInternalAsync(data, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("succeeded", outcome.Succeeded);
        StripeKitDiagnostics.SetTag(activity, "error", outcome.ErrorMessage);

        await _eventStore.RecordOutcomeAsync(eventId, outcome).ConfigureAwait(false);
        EmitProcessedLog(activity, eventId, eventType, data, outcome, duplicate: false, terminalDuplicate: null);

        return new WebhookProcessingResult(eventId, eventType, outcome, false);
    }

    private async Task<WebhookProcessingResult?> TryHandleDuplicateAsync(string eventId, string eventType, Activity? activity)
    {
        bool started = await _eventStore.TryBeginAsync(eventId).ConfigureAwait(false);
        if (started)
        {
            return null;
        }

        activity?.SetTag("duplicate", true);
        WebhookEventOutcome? existing = await _eventStore.GetOutcomeAsync(eventId).ConfigureAwait(false);
        bool terminalDuplicate = IsTerminalDuplicate(existing);
        if (!terminalDuplicate)
        {
            WebhookEventOutcome retryable = CreateRetryableDuplicateOutcome(existing);
            EmitProcessedLog(activity, eventId, eventType, null, retryable, duplicate: true, terminalDuplicate: false);
            return new WebhookProcessingResult(eventId, eventType, retryable, false);
        }

        EmitProcessedLog(activity, eventId, eventType, null, existing, duplicate: true, terminalDuplicate: true);
        return new WebhookProcessingResult(eventId, eventType, existing, true);
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
                    await _paymentApplicator.UpdateStatusAsync(data, PaymentStatus.Succeeded, data.EventCreated).ConfigureAwait(false);
                }
                break;
            case "payment_intent.payment_failed":
                if (_options.EnablePayments)
                {
                    await _paymentApplicator.UpdateStatusAsync(data, PaymentStatus.Failed, data.EventCreated).ConfigureAwait(false);
                }
                break;
            case "customer.subscription.deleted":
                if (_options.EnableBilling)
                {
                    await _subscriptionApplicator.UpdateStatusAsync(data, SubscriptionStatus.Canceled, data.EventCreated).ConfigureAwait(false);
                }
                break;
            case "invoice.payment_succeeded":
                if (_options.EnableBilling)
                {
                    await _subscriptionApplicator.UpdateStatusAsync(data, SubscriptionStatus.Active, data.EventCreated).ConfigureAwait(false);
                }
                break;
            case "invoice.payment_failed":
                if (_options.EnableBilling)
                {
                    await _subscriptionApplicator.UpdateStatusAsync(data, SubscriptionStatus.PastDue, data.EventCreated).ConfigureAwait(false);
                }
                break;
            case "customer.subscription.created":
            case "customer.subscription.updated":
                if (_options.EnableBilling &&
                    SubscriptionWebhookApplicator.TryMapSubscriptionStatus(data.ObjectStatus, out SubscriptionStatus subscriptionStatus))
                {
                    await _subscriptionApplicator.UpdateStatusAsync(data, subscriptionStatus, data.EventCreated).ConfigureAwait(false);
                }
                break;
            case "checkout.session.completed":
                await _paymentApplicator.BackfillCheckoutCorrelationAsync(
                    data,
                    _options.EnablePayments).ConfigureAwait(false);
                await _subscriptionApplicator.BackfillCheckoutCorrelationAsync(
                    data,
                    _options.EnableBilling).ConfigureAwait(false);
                break;
            case "refund.created":
            case "refund.updated":
                if (_options.EnableRefunds &&
                    RefundWebhookApplicator.TryMapRefundStatus(data.ObjectStatus, out RefundStatus refundStatus))
                {
                    await _refundApplicator.UpdateStatusAsync(data, refundStatus).ConfigureAwait(false);
                }
                break;
            case "refund.failed":
                if (_options.EnableRefunds)
                {
                    await _refundApplicator.UpdateStatusAsync(data, RefundStatus.Failed).ConfigureAwait(false);
                }
                break;
        }
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
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.BusinessPaymentId, data.BusinessPaymentId);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.BusinessSubscriptionId, data.BusinessSubscriptionId);
    }

    private static void EmitProcessedLog(
        Activity? activity,
        string eventId,
        string eventType,
        StripeWebhookEventData? data,
        WebhookEventOutcome? outcome,
        bool duplicate,
        bool? terminalDuplicate)
    {
        List<(string Key, object? Value)> fields = new List<(string Key, object? Value)>
        {
            (StripeKitDiagnosticTags.EventId, eventId),
            (StripeKitDiagnosticTags.EventType, eventType),
            (StripeKitDiagnosticTags.UserId, GetTagValue(activity, StripeKitDiagnosticTags.UserId)),
            (StripeKitDiagnosticTags.BusinessPaymentId, GetTagValue(activity, StripeKitDiagnosticTags.BusinessPaymentId)),
            (StripeKitDiagnosticTags.BusinessSubscriptionId, GetTagValue(activity, StripeKitDiagnosticTags.BusinessSubscriptionId)),
            (StripeKitDiagnosticTags.BusinessRefundId, GetTagValue(activity, StripeKitDiagnosticTags.BusinessRefundId)),
            (StripeKitDiagnosticTags.PaymentIntentId, data?.PaymentIntentId),
            (StripeKitDiagnosticTags.SubscriptionId, data?.SubscriptionId),
            (StripeKitDiagnosticTags.InvoiceId, data != null && string.Equals(data.ObjectType, "invoice", StringComparison.Ordinal) ? data.ObjectId : null),
            (StripeKitDiagnosticTags.RefundId, data?.RefundId),
            ("duplicate", duplicate),
            ("succeeded", outcome?.Succeeded),
            ("error", outcome?.ErrorMessage)
        };

        if (terminalDuplicate.HasValue)
        {
            fields.Add(("terminal_duplicate", terminalDuplicate.Value));
        }

        StripeKitDiagnostics.EmitLog("webhook.processed", fields.ToArray());
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

internal sealed class PaymentWebhookApplicator
{
    private readonly IPaymentRecordStore _paymentRecords;
    private readonly IStripeObjectLookup _objectLookup;

    public PaymentWebhookApplicator(IPaymentRecordStore paymentRecords, IStripeObjectLookup objectLookup)
    {
        _paymentRecords = paymentRecords;
        _objectLookup = objectLookup;
    }

    public async Task UpdateStatusAsync(StripeWebhookEventData data, PaymentStatus status, DateTimeOffset? eventCreated)
    {
        string? paymentIntentId = await TryResolvePaymentIntentIdAsync(data).ConfigureAwait(false);
        PaymentRecord? record = null;

        if (!string.IsNullOrWhiteSpace(paymentIntentId))
        {
            record = await _paymentRecords.GetByPaymentIntentIdAsync(paymentIntentId).ConfigureAwait(false);
        }

        if (record == null && !string.IsNullOrWhiteSpace(data.BusinessPaymentId))
        {
            record = await _paymentRecords.GetByBusinessIdAsync(data.BusinessPaymentId).ConfigureAwait(false);
        }

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
            paymentIntentId ?? record.PaymentIntentId,
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

    public async Task BackfillCheckoutCorrelationAsync(StripeWebhookEventData data, bool paymentsEnabled)
    {
        if (!paymentsEnabled ||
            !string.Equals(data.ObjectType, "checkout.session", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(data.BusinessPaymentId))
        {
            return;
        }

        PaymentRecord? paymentRecord = await _paymentRecords.GetByBusinessIdAsync(data.BusinessPaymentId).ConfigureAwait(false);
        if (paymentRecord == null ||
            string.Equals(paymentRecord.PaymentIntentId, data.PaymentIntentId, StringComparison.Ordinal))
        {
            return;
        }

        PaymentRecord updatedPayment = new PaymentRecord(
            paymentRecord.UserId,
            paymentRecord.BusinessPaymentId,
            paymentRecord.Status,
            data.PaymentIntentId,
            paymentRecord.ChargeId,
            paymentRecord.PromotionOutcome,
            paymentRecord.PromotionCouponId,
            paymentRecord.PromotionCodeId,
            paymentRecord.LastStripeEventCreated);

        await _paymentRecords.SaveAsync(updatedPayment).ConfigureAwait(false);
    }

    private async Task<string?> TryResolvePaymentIntentIdAsync(StripeWebhookEventData data)
    {
        if (!string.IsNullOrWhiteSpace(data.PaymentIntentId))
        {
            return data.PaymentIntentId;
        }

        if (string.IsNullOrWhiteSpace(data.ObjectId))
        {
            return null;
        }

        string? paymentIntentId = await _objectLookup.GetPaymentIntentIdAsync(data.ObjectId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(paymentIntentId))
        {
            return null;
        }

        return paymentIntentId;
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
}

internal sealed class SubscriptionWebhookApplicator
{
    private readonly ISubscriptionRecordStore _subscriptionRecords;
    private readonly IStripeObjectLookup _objectLookup;

    public SubscriptionWebhookApplicator(ISubscriptionRecordStore subscriptionRecords, IStripeObjectLookup objectLookup)
    {
        _subscriptionRecords = subscriptionRecords;
        _objectLookup = objectLookup;
    }

    public async Task UpdateStatusAsync(StripeWebhookEventData data, SubscriptionStatus status, DateTimeOffset? eventCreated)
    {
        string? subscriptionId = await TryResolveSubscriptionIdAsync(data).ConfigureAwait(false);
        SubscriptionRecord? record = null;

        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            record = await _subscriptionRecords.GetBySubscriptionIdAsync(subscriptionId).ConfigureAwait(false);
        }

        if (record == null && !string.IsNullOrWhiteSpace(data.BusinessSubscriptionId))
        {
            record = await _subscriptionRecords.GetByBusinessIdAsync(data.BusinessSubscriptionId).ConfigureAwait(false);
        }

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
            data.CustomerId ?? record.CustomerId,
            subscriptionId ?? record.SubscriptionId,
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

    public async Task BackfillCheckoutCorrelationAsync(StripeWebhookEventData data, bool billingEnabled)
    {
        if (!billingEnabled ||
            !string.Equals(data.ObjectType, "checkout.session", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(data.BusinessSubscriptionId))
        {
            return;
        }

        SubscriptionRecord? subscriptionRecord = await _subscriptionRecords
            .GetByBusinessIdAsync(data.BusinessSubscriptionId)
            .ConfigureAwait(false);

        if (subscriptionRecord == null ||
            (string.Equals(subscriptionRecord.SubscriptionId, data.SubscriptionId, StringComparison.Ordinal) &&
             string.Equals(subscriptionRecord.CustomerId, data.CustomerId, StringComparison.Ordinal)))
        {
            return;
        }

        SubscriptionRecord updatedSubscription = new SubscriptionRecord(
            subscriptionRecord.UserId,
            subscriptionRecord.BusinessSubscriptionId,
            subscriptionRecord.Status,
            data.CustomerId ?? subscriptionRecord.CustomerId,
            data.SubscriptionId ?? subscriptionRecord.SubscriptionId,
            subscriptionRecord.PromotionOutcome,
            subscriptionRecord.PromotionCouponId,
            subscriptionRecord.PromotionCodeId,
            subscriptionRecord.LastStripeEventCreated);

        await _subscriptionRecords.SaveAsync(updatedSubscription).ConfigureAwait(false);
    }

    public static bool TryMapSubscriptionStatus(string? status, out SubscriptionStatus mapped)
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

    private async Task<string?> TryResolveSubscriptionIdAsync(StripeWebhookEventData data)
    {
        if (!string.IsNullOrWhiteSpace(data.SubscriptionId))
        {
            return data.SubscriptionId;
        }

        if (string.IsNullOrWhiteSpace(data.ObjectId))
        {
            return null;
        }

        string? subscriptionId = await _objectLookup.GetSubscriptionIdAsync(data.ObjectId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            return null;
        }

        return subscriptionId;
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
}

internal sealed class RefundWebhookApplicator
{
    private readonly IRefundRecordStore _refundRecords;

    public RefundWebhookApplicator(IRefundRecordStore refundRecords)
    {
        _refundRecords = refundRecords;
    }

    public async Task UpdateStatusAsync(StripeWebhookEventData data, RefundStatus status)
    {
        string refundId = ResolveRefundId(data);
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

    public static bool TryMapRefundStatus(string? status, out RefundStatus mapped)
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
}
