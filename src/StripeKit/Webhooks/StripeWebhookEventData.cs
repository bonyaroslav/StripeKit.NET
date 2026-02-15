using System;
using System.Text.Json;
using Stripe;

namespace StripeKit;

public sealed class StripeWebhookEventData
{
    private StripeWebhookEventData(
        string id,
        string type,
        string? objectId,
        string? objectType,
        string? objectStatus,
        string? subscriptionId,
        string? customerId,
        string? paymentIntentId)
    {
        Id = id;
        Type = type;
        ObjectId = objectId;
        ObjectType = objectType;
        ObjectStatus = objectStatus;
        SubscriptionId = subscriptionId;
        CustomerId = customerId;
        PaymentIntentId = paymentIntentId;
    }

    public string Id { get; }
    public string Type { get; }
    public string? ObjectId { get; }
    public string? ObjectType { get; }
    public string? ObjectStatus { get; }
    public string? SubscriptionId { get; }
    public string? CustomerId { get; }
    public string? PaymentIntentId { get; }

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

        string? objectId = null;
        string? objectType = null;
        string? status = null;
        string? subscriptionId = null;
        string? customerId = null;
        string? paymentIntentId = null;

        object? stripeObject = stripeEvent.Data?.Object;
        if (stripeObject is PaymentIntent paymentIntent)
        {
            objectId = paymentIntent.Id;
            objectType = "payment_intent";
            status = paymentIntent.Status;
            paymentIntentId = paymentIntent.Id;
            customerId = paymentIntent.CustomerId;
        }
        else if (stripeObject is Invoice invoice)
        {
            objectId = invoice.Id;
            objectType = "invoice";
            status = invoice.Status;
            subscriptionId = invoice.SubscriptionId;
            customerId = invoice.CustomerId;
            paymentIntentId = invoice.PaymentIntentId;
        }
        else if (stripeObject is Subscription subscription)
        {
            objectId = subscription.Id;
            objectType = "subscription";
            status = subscription.Status;
            subscriptionId = subscription.Id;
            customerId = subscription.CustomerId;
        }

        return new StripeWebhookEventData(
            stripeEvent.Id,
            stripeEvent.Type,
            objectId,
            objectType,
            status,
            subscriptionId,
            customerId,
            paymentIntentId);
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

        string? objectId = null;
        string? objectType = null;
        string? status = null;
        string? subscriptionId = null;
        string? customerId = null;
        string? paymentIntentId = null;

        if (root.TryGetProperty("data", out JsonElement dataElement) &&
            dataElement.TryGetProperty("object", out JsonElement objectElement))
        {
            objectId = GetOptionalString(objectElement, "id");
            objectType = GetOptionalString(objectElement, "object");
            status = GetOptionalString(objectElement, "status");
            subscriptionId = GetOptionalString(objectElement, "subscription");
            customerId = GetOptionalString(objectElement, "customer");
            paymentIntentId = GetOptionalString(objectElement, "payment_intent");
        }

        if (string.Equals(objectType, "payment_intent", StringComparison.Ordinal) && paymentIntentId == null)
        {
            paymentIntentId = objectId;
        }

        if (string.Equals(objectType, "subscription", StringComparison.Ordinal) && subscriptionId == null)
        {
            subscriptionId = objectId;
        }

        return new StripeWebhookEventData(id, type, objectId, objectType, status, subscriptionId, customerId, paymentIntentId);
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
}
