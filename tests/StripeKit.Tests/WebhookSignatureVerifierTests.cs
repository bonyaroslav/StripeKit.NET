using System.Security.Cryptography;
using System.Text;
using Stripe;

namespace StripeKit.Tests;

public class WebhookSignatureVerifierTests
{
    [Fact]
    public void VerifyAndParse_ValidSignature_ReturnsEvent()
    {
        string payload = "{\"id\":\"evt_123\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"pi_123\",\"object\":\"payment_intent\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.succeeded\"}";
        string secret = "whsec_test";
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signature = ComputeSignature(timestamp, payload, secret);
        string header = $"t={timestamp},v1={signature}";

        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();

        StripeWebhookEvent stripeEvent = verifier.VerifyAndParse(payload, header, secret);

        Assert.Equal("evt_123", stripeEvent.Id);
        Assert.Equal("payment_intent.succeeded", stripeEvent.Type);
        Assert.Equal("pi_123", stripeEvent.ObjectId);
        Assert.Equal("payment_intent", stripeEvent.ObjectType);
    }

    [Fact]
    public void VerifyAndParse_InvalidSignature_Throws()
    {
        string payload = "{\"id\":\"evt_456\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"pi_456\",\"object\":\"payment_intent\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.payment_failed\"}";
        string secret = "whsec_test";
        string header = "t=123,v1=invalid";

        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();

        Assert.Throws<StripeException>(() => verifier.VerifyAndParse(payload, header, secret));
    }

    [Fact]
    public void VerifyAndParse_MissingSecret_ThrowsArgumentException()
    {
        string payload = "{\"id\":\"evt_789\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"pi_789\",\"object\":\"payment_intent\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.succeeded\"}";
        string header = "t=123,v1=invalid";

        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();

        Assert.Throws<ArgumentException>(() => verifier.VerifyAndParse(payload, header, ""));
    }

    [Fact]
    public void VerifyAndParse_MissingId_ThrowsInvalidOperationException()
    {
        string payload = "{\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"pi_111\",\"object\":\"payment_intent\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.succeeded\"}";
        string secret = "whsec_test";
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signature = ComputeSignature(timestamp, payload, secret);
        string header = $"t={timestamp},v1={signature}";

        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();

        Assert.Throws<InvalidOperationException>(() => verifier.VerifyAndParse(payload, header, secret));
    }

    private static string ComputeSignature(long timestamp, string payload, string secret)
    {
        string signedPayload = timestamp + "." + payload;
        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(signedPayload);

        using HMACSHA256 hmac = new HMACSHA256(secretBytes);
        byte[] hash = hmac.ComputeHash(payloadBytes);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
