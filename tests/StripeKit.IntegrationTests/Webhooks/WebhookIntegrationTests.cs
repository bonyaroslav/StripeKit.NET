using System.Security.Cryptography;
using System.Text;

namespace StripeKit.IntegrationTests;

public class WebhookProcessingIntegrationTests
{
    [Fact]
    public async Task VerifyAndDedupe_ValidSignature_RecordsOutcome()
    {
        string payload = "{\"id\":\"evt_123\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"pi_123\",\"object\":\"payment_intent\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.succeeded\"}";
        string secret = "whsec_test";
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signature = ComputeSignature(timestamp, payload, secret);
        string header = $"t={timestamp},v1={signature}";

        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore store = new InMemoryWebhookEventStore();
        IPaymentRecordStore payments = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptions = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refunds = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new NoopStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, store, payments, subscriptions, refunds, objectLookup, options);

        PaymentRecord record = new PaymentRecord("user_1", "pay_123", PaymentStatus.Pending, "pi_123", null);
        await payments.SaveAsync(record);

        WebhookProcessingResult first = await processor.ProcessAsync(payload, header, secret);
        WebhookProcessingResult second = await processor.ProcessAsync(payload, header, secret);

        Assert.False(first.IsDuplicate);
        Assert.True(second.IsDuplicate);
        Assert.NotNull(first.Outcome);
        Assert.True(first.Outcome!.Succeeded);
    }

    private sealed class NoopStripeObjectLookup : IStripeObjectLookup
    {
        public Task<string?> GetPaymentIntentIdAsync(string objectId)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<string?> GetSubscriptionIdAsync(string objectId)
        {
            return Task.FromResult<string?>(null);
        }
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
