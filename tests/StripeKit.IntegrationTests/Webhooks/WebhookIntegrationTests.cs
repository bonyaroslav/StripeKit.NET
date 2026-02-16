using System.Security.Cryptography;
using System.Text;
using Stripe;

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

    [Fact]
    public async Task ProcessAsync_PaymentIntentSucceeded_UpdatesPaymentState()
    {
        string payload = "{\"id\":\"evt_state_pay_1\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"pi_state_1\",\"object\":\"payment_intent\",\"status\":\"succeeded\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.succeeded\"}";
        string secret = "whsec_test";
        string header = BuildSignatureHeader(payload, secret);

        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore store = new InMemoryWebhookEventStore();
        IPaymentRecordStore payments = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptions = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refunds = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new NoopStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, store, payments, subscriptions, refunds, objectLookup, options);

        PaymentRecord record = new PaymentRecord("user_state_1", "pay_state_1", PaymentStatus.Pending, "pi_state_1", null);
        await payments.SaveAsync(record);

        WebhookProcessingResult result = await processor.ProcessAsync(payload, header, secret);
        PaymentRecord? updated = await payments.GetByPaymentIntentIdAsync("pi_state_1");

        Assert.False(result.IsDuplicate);
        Assert.NotNull(result.Outcome);
        Assert.True(result.Outcome!.Succeeded);
        Assert.NotNull(updated);
        Assert.Equal(PaymentStatus.Succeeded, updated!.Status);
    }

    [Fact]
    public async Task ProcessAsync_InvoicePaymentFailed_UpdatesSubscriptionState()
    {
        string payload = "{\"id\":\"evt_state_sub_1\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"in_state_1\",\"object\":\"invoice\",\"subscription\":\"sub_state_1\",\"status\":\"open\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"invoice.payment_failed\"}";
        string secret = "whsec_test";
        string header = BuildSignatureHeader(payload, secret);

        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore store = new InMemoryWebhookEventStore();
        IPaymentRecordStore payments = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptions = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refunds = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new NoopStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, store, payments, subscriptions, refunds, objectLookup, options);

        SubscriptionRecord record = new SubscriptionRecord("user_state_2", "biz_sub_state_1", SubscriptionStatus.Active, "cus_state_1", "sub_state_1");
        await subscriptions.SaveAsync(record);

        WebhookProcessingResult result = await processor.ProcessAsync(payload, header, secret);
        SubscriptionRecord? updated = await subscriptions.GetBySubscriptionIdAsync("sub_state_1");

        Assert.False(result.IsDuplicate);
        Assert.NotNull(result.Outcome);
        Assert.True(result.Outcome!.Succeeded);
        Assert.NotNull(updated);
        Assert.Equal(SubscriptionStatus.PastDue, updated!.Status);
    }

    [Fact]
    public async Task ReconcileAsync_FirstAttemptFailed_ReplayAppliesOnceAfterRecovery()
    {
        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore store = new InMemoryWebhookEventStore();
        IPaymentRecordStore payments = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptions = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refunds = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new NoopStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, store, payments, subscriptions, refunds, objectLookup, options);

        StripeList<Event> events = new StripeList<Event>
        {
            Data = new List<Event>
            {
                new Event
                {
                    Id = "evt_replay_1",
                    Type = "payment_intent.succeeded",
                    Data = new EventData
                    {
                        Object = new PaymentIntent
                        {
                            Id = "pi_replay_1",
                            Status = "succeeded"
                        }
                    }
                }
            },
            HasMore = false
        };

        IStripeEventClient eventClient = new StaticStripeEventClient(events);
        StripeEventReconciler reconciler = new StripeEventReconciler(eventClient, processor);

        ReconciliationResult first = await reconciler.ReconcileAsync(new ReconciliationRequest
        {
            Limit = 1,
            CreatedAfter = DateTimeOffset.UtcNow.AddDays(-1)
        });

        PaymentRecord record = new PaymentRecord("user_replay_1", "pay_replay_1", PaymentStatus.Pending, "pi_replay_1", null);
        await payments.SaveAsync(record);

        ReconciliationResult second = await reconciler.ReconcileAsync(new ReconciliationRequest
        {
            Limit = 1,
            CreatedAfter = DateTimeOffset.UtcNow.AddDays(-1)
        });

        ReconciliationResult third = await reconciler.ReconcileAsync(new ReconciliationRequest
        {
            Limit = 1,
            CreatedAfter = DateTimeOffset.UtcNow.AddDays(-1)
        });

        PaymentRecord? updated = await payments.GetByPaymentIntentIdAsync("pi_replay_1");

        Assert.Equal(1, first.Total);
        Assert.Equal(1, first.Failed);
        Assert.Equal(1, second.Total);
        Assert.Equal(1, second.Processed);
        Assert.Equal(0, second.Duplicates);
        Assert.Equal(0, second.Failed);
        Assert.Equal(1, third.Total);
        Assert.Equal(0, third.Processed);
        Assert.Equal(1, third.Duplicates);
        Assert.Equal(0, third.Failed);
        Assert.Equal(PaymentStatus.Succeeded, updated!.Status);
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

    private sealed class StaticStripeEventClient : IStripeEventClient
    {
        private readonly StripeList<Event> _events;

        public StaticStripeEventClient(StripeList<Event> events)
        {
            _events = events;
        }

        public Task<StripeList<Event>> ListAsync(EventListOptions options, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_events);
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

    private static string BuildSignatureHeader(string payload, string secret)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signature = ComputeSignature(timestamp, payload, secret);
        return $"t={timestamp},v1={signature}";
    }
}
