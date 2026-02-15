using System.Diagnostics;

namespace StripeKit;

public sealed class StripeKitOptions
{
    public bool EnablePayments { get; init; } = true;
    public bool EnableBilling { get; init; } = true;
    public bool EnablePromotions { get; init; } = true;
    public bool EnableRefunds { get; init; } = false;
    public bool EnableWebhooks { get; init; } = true;
}

internal static class StripeKitDiagnostics
{
    internal const string ActivitySourceName = "StripeKit";
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    internal static void SetTag(Activity? activity, string key, string? value)
    {
        if (activity == null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        activity.SetTag(key, value);
    }
}

internal static class StripeKitDiagnosticTags
{
    internal const string UserId = "user_id";
    internal const string BusinessPaymentId = "business_payment_id";
    internal const string BusinessSubscriptionId = "business_subscription_id";
    internal const string BusinessRefundId = "business_refund_id";
    internal const string EventId = "event_id";
    internal const string EventType = "event_type";
    internal const string StripeObjectId = "stripe_object_id";
    internal const string CheckoutSessionId = "checkout_session_id";
    internal const string PaymentIntentId = "payment_intent_id";
    internal const string SubscriptionId = "subscription_id";
    internal const string InvoiceId = "invoice_id";
    internal const string RefundId = "refund_id";
    internal const string CustomerId = "customer_id";
    internal const string LastEventId = "last_event_id";
    internal const string StartingAfterEventId = "starting_after_event_id";
}
