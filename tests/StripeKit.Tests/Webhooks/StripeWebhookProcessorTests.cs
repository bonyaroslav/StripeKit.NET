using System.Security.Cryptography;
using System.Text;

namespace StripeKit.Tests;

public class StripeWebhookProcessorTests
{
    [Fact]
    public async Task ProcessAsync_PaymentIntentSucceeded_UpdatesPaymentStatus()
    {
        string payload = "{\"id\":\"evt_100\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"pi_100\",\"object\":\"payment_intent\",\"status\":\"succeeded\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.succeeded\"}";
        string secret = "whsec_test";
        string header = BuildSignatureHeader(payload, secret);

        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, objectLookup, options);

        PaymentRecord record = new PaymentRecord("user_1", "pay_100", PaymentStatus.Pending, "pi_100", null);
        await paymentStore.SaveAsync(record);

        WebhookProcessingResult result = await processor.ProcessAsync(payload, header, secret);

        PaymentRecord? updated = await paymentStore.GetByPaymentIntentIdAsync("pi_100");

        Assert.False(result.IsDuplicate);
        Assert.NotNull(result.Outcome);
        Assert.True(result.Outcome!.Succeeded);
        Assert.Equal(PaymentStatus.Succeeded, updated!.Status);
    }

    [Fact]
    public async Task ProcessAsync_InvoicePaymentFailed_UpdatesSubscriptionStatus()
    {
        string payload = "{\"id\":\"evt_200\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"in_200\",\"object\":\"invoice\",\"subscription\":\"sub_200\",\"status\":\"open\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"invoice.payment_failed\"}";
        string secret = "whsec_test";
        string header = BuildSignatureHeader(payload, secret);

        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, objectLookup, options);

        SubscriptionRecord record = new SubscriptionRecord("user_2", "sub_200", SubscriptionStatus.Active, "cus_200", "sub_200");
        await subscriptionStore.SaveAsync(record);

        WebhookProcessingResult result = await processor.ProcessAsync(payload, header, secret);

        SubscriptionRecord? updated = await subscriptionStore.GetBySubscriptionIdAsync("sub_200");

        Assert.False(result.IsDuplicate);
        Assert.NotNull(result.Outcome);
        Assert.True(result.Outcome!.Succeeded);
        Assert.Equal(SubscriptionStatus.PastDue, updated!.Status);
    }

    [Fact]
    public async Task ProcessAsync_ThinInvoiceEvent_UsesLookup()
    {
        string payload = "{\"id\":\"evt_300\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"in_300\",\"object\":\"invoice\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"invoice.payment_succeeded\"}";
        string secret = "whsec_test";
        string header = BuildSignatureHeader(payload, secret);

        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup
        {
            SubscriptionId = "sub_300"
        };
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, objectLookup, options);

        SubscriptionRecord record = new SubscriptionRecord("user_3", "sub_300", SubscriptionStatus.Incomplete, "cus_300", "sub_300");
        await subscriptionStore.SaveAsync(record);

        WebhookProcessingResult result = await processor.ProcessAsync(payload, header, secret);

        SubscriptionRecord? updated = await subscriptionStore.GetBySubscriptionIdAsync("sub_300");

        Assert.True(result.Outcome!.Succeeded);
        Assert.Equal(SubscriptionStatus.Active, updated!.Status);
    }

    [Fact]
    public async Task ProcessAsync_DuplicateEvent_ReturnsRecordedOutcome()
    {
        string payload = "{\"id\":\"evt_400\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"pi_400\",\"object\":\"payment_intent\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.succeeded\"}";
        string secret = "whsec_test";
        string header = BuildSignatureHeader(payload, secret);

        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, objectLookup, options);

        PaymentRecord record = new PaymentRecord("user_4", "pay_400", PaymentStatus.Pending, "pi_400", null);
        await paymentStore.SaveAsync(record);

        WebhookProcessingResult first = await processor.ProcessAsync(payload, header, secret);
        WebhookProcessingResult second = await processor.ProcessAsync(payload, header, secret);

        Assert.False(first.IsDuplicate);
        Assert.True(second.IsDuplicate);
        Assert.NotNull(second.Outcome);
        Assert.True(second.Outcome!.Succeeded);
    }

    private sealed class FakeStripeObjectLookup : IStripeObjectLookup
    {
        public string? PaymentIntentId { get; set; }
        public string? SubscriptionId { get; set; }

        public Task<string?> GetPaymentIntentIdAsync(string objectId)
        {
            return Task.FromResult(PaymentIntentId);
        }

        public Task<string?> GetSubscriptionIdAsync(string objectId)
        {
            return Task.FromResult(SubscriptionId);
        }
    }

    private static string BuildSignatureHeader(string payload, string secret)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signature = ComputeSignature(timestamp, payload, secret);
        return $"t={timestamp},v1={signature}";
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