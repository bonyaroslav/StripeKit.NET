using System;
using System.Collections.Generic;
using System.Text.Json;
using Stripe;
using Stripe.Checkout;

namespace StripeKit;

public sealed class StripeWebhookEventData
{
    private StripeWebhookEventData(
        string id,
        string type,
        DateTimeOffset? eventCreated,
        string? objectId,
        string? objectType,
        string? objectStatus,
        string? subscriptionId,
        string? customerId,
        string? paymentIntentId,
        string? refundId,
        string? businessPaymentId,
        string? businessSubscriptionId)
    {
        Id = id;
        Type = type;
        EventCreated = eventCreated;
        ObjectId = objectId;
        ObjectType = objectType;
        ObjectStatus = objectStatus;
        SubscriptionId = subscriptionId;
        CustomerId = customerId;
        PaymentIntentId = paymentIntentId;
        RefundId = refundId;
        BusinessPaymentId = businessPaymentId;
        BusinessSubscriptionId = businessSubscriptionId;
    }

    public string Id { get; }
    public string Type { get; }
    public DateTimeOffset? EventCreated { get; }
    public string? ObjectId { get; }
    public string? ObjectType { get; }
    public string? ObjectStatus { get; }
    public string? SubscriptionId { get; }
    public string? CustomerId { get; }
    public string? PaymentIntentId { get; }
    public string? RefundId { get; }
    public string? BusinessPaymentId { get; }
    public string? BusinessSubscriptionId { get; }

    public static StripeWebhookEventData FromEvent(Event stripeEvent)
    {
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

        DateTimeOffset? eventCreated = null;
        if (stripeEvent.Created != default)
        {
            DateTime created = stripeEvent.Created;
            if (created.Kind == DateTimeKind.Unspecified)
            {
                created = DateTime.SpecifyKind(created, DateTimeKind.Utc);
            }
            else
            {
                created = created.ToUniversalTime();
            }

            eventCreated = new DateTimeOffset(created);
        }

        string? objectId = null;
        string? objectType = null;
        string? status = null;
        string? subscriptionId = null;
        string? customerId = null;
        string? paymentIntentId = null;
        string? refundId = null;
        string? businessPaymentId = null;
        string? businessSubscriptionId = null;

        object? stripeObject = stripeEvent.Data?.Object;
        if (stripeObject is PaymentIntent paymentIntent)
        {
            objectId = paymentIntent.Id;
            objectType = "payment_intent";
            status = paymentIntent.Status;
            paymentIntentId = paymentIntent.Id;
            customerId = paymentIntent.CustomerId;
            businessPaymentId = GetMetadataValue(paymentIntent.Metadata, StripeKitDiagnosticTags.BusinessPaymentId);
        }
        else if (stripeObject is Invoice invoice)
        {
            objectId = invoice.Id;
            objectType = "invoice";
            status = invoice.Status;
            subscriptionId = invoice.SubscriptionId;
            customerId = invoice.CustomerId;
            paymentIntentId = invoice.PaymentIntentId;
            businessSubscriptionId = GetMetadataValue(invoice.Metadata, StripeKitDiagnosticTags.BusinessSubscriptionId);
        }
        else if (stripeObject is Subscription subscription)
        {
            objectId = subscription.Id;
            objectType = "subscription";
            status = subscription.Status;
            subscriptionId = subscription.Id;
            customerId = subscription.CustomerId;
            businessSubscriptionId = GetMetadataValue(subscription.Metadata, StripeKitDiagnosticTags.BusinessSubscriptionId);
        }
        else if (stripeObject is Refund refund)
        {
            objectId = refund.Id;
            objectType = "refund";
            status = refund.Status;
            paymentIntentId = refund.PaymentIntentId;
            refundId = refund.Id;
        }
        else if (stripeObject is Session session)
        {
            objectId = session.Id;
            objectType = "checkout.session";
            status = session.Status;
            subscriptionId = session.SubscriptionId;
            customerId = session.CustomerId;
            paymentIntentId = session.PaymentIntentId;

            string? metadataBusinessPaymentId = GetMetadataValue(session.Metadata, StripeKitDiagnosticTags.BusinessPaymentId);
            string? metadataBusinessSubscriptionId = GetMetadataValue(session.Metadata, StripeKitDiagnosticTags.BusinessSubscriptionId);

            if (string.Equals(session.Mode, "payment", StringComparison.Ordinal))
            {
                businessPaymentId = session.ClientReferenceId ?? metadataBusinessPaymentId;
                businessSubscriptionId = metadataBusinessSubscriptionId;
            }
            else if (string.Equals(session.Mode, "subscription", StringComparison.Ordinal))
            {
                businessSubscriptionId = session.ClientReferenceId ?? metadataBusinessSubscriptionId;
                businessPaymentId = metadataBusinessPaymentId;
            }
            else
            {
                businessPaymentId = metadataBusinessPaymentId;
                businessSubscriptionId = metadataBusinessSubscriptionId;
            }
        }

        return new StripeWebhookEventData(
            stripeEvent.Id,
            stripeEvent.Type,
            eventCreated,
            objectId,
            objectType,
            status,
            subscriptionId,
            customerId,
            paymentIntentId,
            refundId,
            businessPaymentId,
            businessSubscriptionId);
    }

    public static StripeWebhookEventData Parse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new ArgumentException("Payload is required.", nameof(payload));
        }

        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;

        string id = GetRequiredString(root, "id");
        string type = GetRequiredString(root, "type");
        DateTimeOffset? eventCreated = GetOptionalUnixTimestamp(root, "created");

        string? objectId = null;
        string? objectType = null;
        string? status = null;
        string? subscriptionId = null;
        string? customerId = null;
        string? paymentIntentId = null;
        string? refundId = null;
        string? businessPaymentId = null;
        string? businessSubscriptionId = null;

        if (root.TryGetProperty("data", out JsonElement dataElement) &&
            dataElement.TryGetProperty("object", out JsonElement objectElement))
        {
            objectId = GetOptionalString(objectElement, "id");
            objectType = GetOptionalString(objectElement, "object");
            status = GetOptionalString(objectElement, "status");
            subscriptionId = GetOptionalString(objectElement, "subscription");
            customerId = GetOptionalString(objectElement, "customer");
            paymentIntentId = GetOptionalString(objectElement, "payment_intent");

            string? clientReferenceId = GetOptionalString(objectElement, "client_reference_id");
            string? mode = GetOptionalString(objectElement, "mode");
            string? metadataBusinessPaymentId = GetOptionalMetadataString(objectElement, StripeKitDiagnosticTags.BusinessPaymentId);
            string? metadataBusinessSubscriptionId = GetOptionalMetadataString(objectElement, StripeKitDiagnosticTags.BusinessSubscriptionId);

            if (string.Equals(objectType, "checkout.session", StringComparison.Ordinal))
            {
                if (string.Equals(mode, "payment", StringComparison.Ordinal))
                {
                    businessPaymentId = clientReferenceId ?? metadataBusinessPaymentId;
                    businessSubscriptionId = metadataBusinessSubscriptionId;
                }
                else if (string.Equals(mode, "subscription", StringComparison.Ordinal))
                {
                    businessSubscriptionId = clientReferenceId ?? metadataBusinessSubscriptionId;
                    businessPaymentId = metadataBusinessPaymentId;
                }
                else
                {
                    businessPaymentId = metadataBusinessPaymentId;
                    businessSubscriptionId = metadataBusinessSubscriptionId;
                }
            }
            else
            {
                businessPaymentId = metadataBusinessPaymentId;
                businessSubscriptionId = metadataBusinessSubscriptionId;
            }
        }

        if (string.Equals(objectType, "payment_intent", StringComparison.Ordinal) && paymentIntentId == null)
        {
            paymentIntentId = objectId;
        }

        if (string.Equals(objectType, "subscription", StringComparison.Ordinal) && subscriptionId == null)
        {
            subscriptionId = objectId;
        }

        if (string.Equals(objectType, "refund", StringComparison.Ordinal))
        {
            refundId = objectId;
        }

        return new StripeWebhookEventData(
            id,
            type,
            eventCreated,
            objectId,
            objectType,
            status,
            subscriptionId,
            customerId,
            paymentIntentId,
            refundId,
            businessPaymentId,
            businessSubscriptionId);
    }

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Missing required webhook field: " + propertyName);
        }

        string? value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Missing required webhook field: " + propertyName);
        }

        return value;
    }

    private static string? GetOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value;
    }

    private static DateTimeOffset? GetOptionalUnixTimestamp(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (!element.TryGetInt64(out long seconds))
        {
            throw new InvalidOperationException("Invalid webhook field: " + propertyName);
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private static string? GetOptionalMetadataString(JsonElement root, string key)
    {
        if (!root.TryGetProperty("metadata", out JsonElement metadataElement) ||
            metadataElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetOptionalString(metadataElement, key);
    }

    private static string? GetMetadataValue(IReadOnlyDictionary<string, string>? metadata, string key)
    {
        if (metadata == null || !metadata.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value;
    }
}
