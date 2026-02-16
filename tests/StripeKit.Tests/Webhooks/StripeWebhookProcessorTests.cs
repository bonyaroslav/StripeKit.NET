using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using Stripe;

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
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

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
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

        SubscriptionRecord record = new SubscriptionRecord("user_2", "sub_200", SubscriptionStatus.Active, "cus_200", "sub_200");
        await subscriptionStore.SaveAsync(record);

        Activity? captured = null;
        using ActivityListener listener = CreateListener(activity =>
        {
            if (activity.OperationName == "stripekit.webhook.process")
            {
                captured = activity;
            }
        });

        WebhookProcessingResult result = await processor.ProcessAsync(payload, header, secret);

        SubscriptionRecord? updated = await subscriptionStore.GetBySubscriptionIdAsync("sub_200");

        Assert.False(result.IsDuplicate);
        Assert.NotNull(result.Outcome);
        Assert.True(result.Outcome!.Succeeded);
        Assert.Equal(SubscriptionStatus.PastDue, updated!.Status);
        Assert.Equal("in_200", GetTag(captured!, "invoice_id"));
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
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup
        {
            SubscriptionId = "sub_300"
        };
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

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
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

        PaymentRecord record = new PaymentRecord("user_4", "pay_400", PaymentStatus.Pending, "pi_400", null);
        await paymentStore.SaveAsync(record);

        WebhookProcessingResult first = await processor.ProcessAsync(payload, header, secret);
        WebhookProcessingResult second = await processor.ProcessAsync(payload, header, secret);

        Assert.False(first.IsDuplicate);
        Assert.True(second.IsDuplicate);
        Assert.NotNull(second.Outcome);
        Assert.True(second.Outcome!.Succeeded);
    }

    [Fact]
    public async Task ProcessAsync_FirstAttemptFailed_SecondDeliveryRetriesAndSucceeds()
    {
        string payload = "{\"id\":\"evt_retry_1\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"pi_retry_1\",\"object\":\"payment_intent\",\"status\":\"succeeded\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.succeeded\"}";
        string secret = "whsec_test";
        string header = BuildSignatureHeader(payload, secret);

        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

        WebhookProcessingResult first = await processor.ProcessAsync(payload, header, secret);
        PaymentRecord record = new PaymentRecord("user_retry_1", "pay_retry_1", PaymentStatus.Pending, "pi_retry_1", null);
        await paymentStore.SaveAsync(record);

        WebhookProcessingResult second = await processor.ProcessAsync(payload, header, secret);
        PaymentRecord? updated = await paymentStore.GetByPaymentIntentIdAsync("pi_retry_1");

        Assert.NotNull(first.Outcome);
        Assert.False(first.Outcome!.Succeeded);
        Assert.False(first.IsDuplicate);
        Assert.NotNull(second.Outcome);
        Assert.True(second.Outcome!.Succeeded);
        Assert.False(second.IsDuplicate);
        Assert.Equal(PaymentStatus.Succeeded, updated!.Status);
    }

    [Fact]
    public async Task ProcessAsync_DuplicateEvent_EmitsDuplicateTag()
    {
        string payload = "{\"id\":\"evt_dup_1\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"pi_dup_1\",\"object\":\"payment_intent\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.succeeded\"}";
        string secret = "whsec_test";
        string header = BuildSignatureHeader(payload, secret);

        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

        PaymentRecord record = new PaymentRecord("user_dup_1", "pay_dup_1", PaymentStatus.Pending, "pi_dup_1", null);
        await paymentStore.SaveAsync(record);

        await processor.ProcessAsync(payload, header, secret);

        Activity? captured = null;
        using ActivityListener listener = CreateListener(activity =>
        {
            if (activity.OperationName == "stripekit.webhook.process")
            {
                captured = activity;
            }
        });

        WebhookProcessingResult duplicate = await processor.ProcessAsync(payload, header, secret);

        Assert.True(duplicate.IsDuplicate);
        Assert.Equal("True", GetTag(captured!, "duplicate"));
    }

    [Fact]
    public async Task ProcessAsync_EmitsCorrelationTags()
    {
        string payload = "{\"id\":\"evt_obs_1\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"pi_obs_1\",\"object\":\"payment_intent\",\"status\":\"succeeded\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.succeeded\"}";
        string secret = "whsec_test";
        string header = BuildSignatureHeader(payload, secret);

        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

        PaymentRecord record = new PaymentRecord("user_obs_3", "pay_obs_3", PaymentStatus.Pending, "pi_obs_1", null);
        await paymentStore.SaveAsync(record);

        Activity? captured = null;
        using ActivityListener listener = CreateListener(activity =>
        {
            if (activity.OperationName == "stripekit.webhook.process")
            {
                captured = activity;
            }
        });

        await processor.ProcessAsync(payload, header, secret);

        Assert.NotNull(captured);
        Assert.Equal("evt_obs_1", GetTag(captured!, "event_id"));
        Assert.Equal("payment_intent.succeeded", GetTag(captured!, "event_type"));
        Assert.Equal("user_obs_3", GetTag(captured!, "user_id"));
        Assert.Equal("pay_obs_3", GetTag(captured!, "business_payment_id"));
        Assert.Equal("pi_obs_1", GetTag(captured!, "payment_intent_id"));
    }

    [Fact]
    public async Task ProcessAsync_RefundUpdated_UpdatesRefundStatus()
    {
        string payload = "{\"id\":\"evt_refund_1\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"re_100\",\"object\":\"refund\",\"status\":\"failed\",\"payment_intent\":\"pi_100\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"refund.updated\"}";
        string secret = "whsec_test";
        string header = BuildSignatureHeader(payload, secret);

        StripeKitOptions options = new StripeKitOptions
        {
            EnableRefunds = true
        };
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

        RefundRecord record = new RefundRecord("user_9", "refund_100", "pay_100", RefundStatus.Pending, "pi_100", "re_100");
        await refundStore.SaveAsync(record);

        WebhookProcessingResult result = await processor.ProcessAsync(payload, header, secret);

        RefundRecord? updated = await refundStore.GetByRefundIdAsync("re_100");

        Assert.False(result.IsDuplicate);
        Assert.NotNull(result.Outcome);
        Assert.True(result.Outcome!.Succeeded);
        Assert.Equal(RefundStatus.Failed, updated!.Status);
    }

    [Fact]
    public async Task ProcessStripeEventAsync_RefundUpdated_UpdatesStatusAndEmitsTags()
    {
        StripeKitOptions options = new StripeKitOptions
        {
            EnableRefunds = true
        };
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

        RefundRecord record = new RefundRecord("user_10", "refund_200", "pay_200", RefundStatus.Pending, "pi_200", "re_200");
        await refundStore.SaveAsync(record);

        Event stripeEvent = new Event
        {
            Id = "evt_refund_200",
            Type = "refund.updated",
            Data = new EventData
            {
                Object = new Refund
                {
                    Id = "re_200",
                    Status = "succeeded",
                    PaymentIntentId = "pi_200"
                }
            }
        };

        Activity? captured = null;
        using ActivityListener listener = CreateListener(activity =>
        {
            if (activity.OperationName == "stripekit.webhook.process")
            {
                captured = activity;
            }
        });

        WebhookProcessingResult result = await processor.ProcessStripeEventAsync(stripeEvent);

        RefundRecord? updated = await refundStore.GetByRefundIdAsync("re_200");

        Assert.False(result.IsDuplicate);
        Assert.NotNull(result.Outcome);
        Assert.True(result.Outcome!.Succeeded);
        Assert.Equal(RefundStatus.Succeeded, updated!.Status);
        Assert.Equal("evt_refund_200", GetTag(captured!, "event_id"));
        Assert.Equal("refund.updated", GetTag(captured!, "event_type"));
        Assert.Equal("re_200", GetTag(captured!, "refund_id"));
        Assert.Equal("pi_200", GetTag(captured!, "payment_intent_id"));
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

    private static ActivityListener CreateListener(Action<Activity> onStopped)
    {
        ActivityListener listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "StripeKit",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = onStopped
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static string? GetTag(Activity activity, string key)
    {
        foreach (KeyValuePair<string, object?> item in activity.TagObjects)
        {
            if (string.Equals(item.Key, key, StringComparison.Ordinal))
            {
                return item.Value?.ToString();
            }
        }

        return null;
    }
}
