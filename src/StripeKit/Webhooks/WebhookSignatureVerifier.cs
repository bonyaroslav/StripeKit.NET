using System;
using System.Text.Json;
using Stripe;

namespace StripeKit;

public sealed class WebhookSignatureVerifier
{
    // TODO: Expose tolerance/config via options when introducing module configuration.
    public StripeWebhookEvent VerifyAndParse(string payload, string signatureHeader, string secret)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new ArgumentException("Payload is required.", nameof(payload));
        }

        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            throw new ArgumentException("Signature header is required.", nameof(signatureHeader));
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("Secret is required.", nameof(secret));
        }

        // TODO: Allow tolerance override; 300 seconds matches Stripe SDK default guidance.
        EventUtility.ValidateSignature(payload, signatureHeader, secret, 300);

        using JsonDocument document = JsonDocument.Parse(payload);

        return StripeWebhookEvent.FromJsonElement(document.RootElement);
    }
}
