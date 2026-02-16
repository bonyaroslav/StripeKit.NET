using System.Threading;
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Stripe.Checkout;

namespace StripeKit.Tests;

public class StripeCheckoutSessionCreatorTests
{
    [Fact]
    public async Task CreatePaymentSessionAsync_UsesGeneratedIdempotencyKeyAndStoresRecord()
    {
        FakeCheckoutSessionClient client = new FakeCheckoutSessionClient();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        StripeKitOptions options = new StripeKitOptions
        {
            EnablePayments = true,
            EnableBilling = true,
            EnablePromotions = true,
            EnableWebhooks = true
        };
        IPromotionEligibilityPolicy policy = new AllowAllPromotionEligibilityPolicy();
        StripeCheckoutSessionCreator creator = new StripeCheckoutSessionCreator(
            client,
            paymentStore,
            subscriptionStore,
            options,
            policy,
            null);

        CheckoutPaymentSessionRequest request = new CheckoutPaymentSessionRequest
        {
            UserId = "user_1",
            BusinessPaymentId = "pay_1",
            Amount = 1200,
            Currency = "usd",
            ItemName = "Test item",
            SuccessUrl = "https://example.com/success",
            CancelUrl = "https://example.com/cancel"
        };

        CheckoutSessionResult result = await creator.CreatePaymentSessionAsync(request);

        string expectedKey = IdempotencyKeyFactory.Create("checkout_payment", "pay_1");
        Assert.Equal(expectedKey, client.LastIdempotencyKey);
        Assert.NotNull(result.Session);

        PaymentRecord? stored = await paymentStore.GetByBusinessIdAsync("pay_1");
        Assert.NotNull(stored);
        Assert.Equal(PaymentStatus.Pending, stored!.Status);
        Assert.Equal("pi_123", stored.PaymentIntentId);
    }

    [Fact]
    public async Task CreateSubscriptionSessionAsync_BillingDisabled_Throws()
    {
        FakeCheckoutSessionClient client = new FakeCheckoutSessionClient();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        StripeKitOptions options = new StripeKitOptions
        {
            EnablePayments = true,
            EnableBilling = false,
            EnablePromotions = true,
            EnableWebhooks = true
        };
        IPromotionEligibilityPolicy policy = new AllowAllPromotionEligibilityPolicy();
        StripeCheckoutSessionCreator creator = new StripeCheckoutSessionCreator(
            client,
            paymentStore,
            subscriptionStore,
            options,
            policy,
            null);

        CheckoutSubscriptionSessionRequest request = new CheckoutSubscriptionSessionRequest
        {
            UserId = "user_2",
            BusinessSubscriptionId = "sub_1",
            PriceId = "price_123",
            SuccessUrl = "https://example.com/success",
            CancelUrl = "https://example.com/cancel"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => creator.CreateSubscriptionSessionAsync(request));
    }

    [Fact]
    public async Task CreatePaymentSessionAsync_InvalidPromotion_ReturnsNoSession()
    {
        FakeCheckoutSessionClient client = new FakeCheckoutSessionClient();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        StripeKitOptions options = new StripeKitOptions
        {
            EnablePayments = true,
            EnableBilling = true,
            EnablePromotions = true,
            EnableWebhooks = true
        };
        IPromotionEligibilityPolicy policy = new DenyPromotionEligibilityPolicy();
        StripeCheckoutSessionCreator creator = new StripeCheckoutSessionCreator(
            client,
            paymentStore,
            subscriptionStore,
            options,
            policy,
            null);

        CheckoutPaymentSessionRequest request = new CheckoutPaymentSessionRequest
        {
            UserId = "user_3",
            BusinessPaymentId = "pay_3",
            Amount = 1200,
            Currency = "usd",
            ItemName = "Test item",
            SuccessUrl = "https://example.com/success",
            CancelUrl = "https://example.com/cancel",
            Discount = new StripeDiscount
            {
                CouponId = "coupon_123"
            }
        };

        CheckoutSessionResult result = await creator.CreatePaymentSessionAsync(request);

        Assert.Null(result.Session);
        Assert.Equal(PromotionOutcome.Invalid, result.PromotionResult.Outcome);
        Assert.Null(client.LastOptions);
    }

    [Fact]
    public async Task CreatePaymentSessionAsync_EmitsCorrelationTags()
    {
        FakeCheckoutSessionClient client = new FakeCheckoutSessionClient();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        StripeKitOptions options = new StripeKitOptions();
        IPromotionEligibilityPolicy policy = new AllowAllPromotionEligibilityPolicy();
        StripeCheckoutSessionCreator creator = new StripeCheckoutSessionCreator(
            client,
            paymentStore,
            subscriptionStore,
            options,
            policy,
            null);

        CheckoutPaymentSessionRequest request = new CheckoutPaymentSessionRequest
        {
            UserId = "user_obs_1",
            BusinessPaymentId = "pay_obs_1",
            Amount = 1200,
            Currency = "usd",
            ItemName = "Test item",
            SuccessUrl = "https://example.com/success",
            CancelUrl = "https://example.com/cancel"
        };

        Activity? captured = null;
        using ActivityListener listener = CreateListener(activity =>
        {
            if (activity.OperationName == "stripekit.checkout.create_payment")
            {
                captured = activity;
            }
        });

        await creator.CreatePaymentSessionAsync(request);

        Assert.NotNull(captured);
        Assert.Equal("user_obs_1", GetTag(captured!, "user_id"));
        Assert.Equal("pay_obs_1", GetTag(captured!, "business_payment_id"));
        Assert.Equal("cs_123", GetTag(captured!, "checkout_session_id"));
        Assert.Equal("pi_123", GetTag(captured!, "payment_intent_id"));
    }

    [Fact]
    public async Task CreatePaymentSessionAsync_WithoutCustomerId_UsesResolvedCustomerId()
    {
        FakeCheckoutSessionClient client = new FakeCheckoutSessionClient();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        StripeKitOptions options = new StripeKitOptions();
        IPromotionEligibilityPolicy policy = new AllowAllPromotionEligibilityPolicy();
        FakeCustomerResolver customerResolver = new FakeCustomerResolver("cus_resolved_1");
        StripeCheckoutSessionCreator creator = new StripeCheckoutSessionCreator(
            client,
            paymentStore,
            subscriptionStore,
            options,
            policy,
            null,
            customerResolver);

        CheckoutPaymentSessionRequest request = new CheckoutPaymentSessionRequest
        {
            UserId = "user_customer_1",
            BusinessPaymentId = "pay_customer_1",
            Amount = 1500,
            Currency = "usd",
            ItemName = "Test item",
            SuccessUrl = "https://example.com/success",
            CancelUrl = "https://example.com/cancel"
        };

        CheckoutSessionResult result = await creator.CreatePaymentSessionAsync(request);

        Assert.NotNull(result.Session);
        Assert.NotNull(client.LastOptions);
        Assert.Equal("cus_resolved_1", client.LastOptions!.Customer);
    }

    [Fact]
    public async Task CreatePaymentSessionAsync_WithBackendCoupon_PersistsPromotionData()
    {
        FakeCheckoutSessionClient client = new FakeCheckoutSessionClient();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        StripeKitOptions options = new StripeKitOptions();
        IPromotionEligibilityPolicy policy = new AllowAllPromotionEligibilityPolicy();
        StripeCheckoutSessionCreator creator = new StripeCheckoutSessionCreator(
            client,
            paymentStore,
            subscriptionStore,
            options,
            policy,
            null);

        CheckoutPaymentSessionRequest request = new CheckoutPaymentSessionRequest
        {
            UserId = "user_promo_1",
            BusinessPaymentId = "pay_promo_1",
            Amount = 1200,
            Currency = "usd",
            ItemName = "Test item",
            SuccessUrl = "https://example.com/success",
            CancelUrl = "https://example.com/cancel",
            Discount = new StripeDiscount
            {
                CouponId = "coupon_promo_1"
            }
        };

        CheckoutSessionResult result = await creator.CreatePaymentSessionAsync(request);
        PaymentRecord? stored = await paymentStore.GetByBusinessIdAsync("pay_promo_1");

        Assert.NotNull(result.Session);
        Assert.NotNull(stored);
        Assert.Equal(PromotionOutcome.Applied, stored!.PromotionOutcome);
        Assert.Equal("coupon_promo_1", stored.PromotionCouponId);
        Assert.Null(stored.PromotionCodeId);
    }

    [Fact]
    public async Task CreateSubscriptionSessionAsync_WithBackendPromotionCode_PersistsPromotionData()
    {
        FakeCheckoutSessionClient client = new FakeCheckoutSessionClient("cs_sub_1", "sub_321");
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        StripeKitOptions options = new StripeKitOptions();
        IPromotionEligibilityPolicy policy = new AllowAllPromotionEligibilityPolicy();
        StripeCheckoutSessionCreator creator = new StripeCheckoutSessionCreator(
            client,
            paymentStore,
            subscriptionStore,
            options,
            policy,
            null);

        CheckoutSubscriptionSessionRequest request = new CheckoutSubscriptionSessionRequest
        {
            UserId = "user_promo_2",
            BusinessSubscriptionId = "sub_promo_2",
            PriceId = "price_123",
            SuccessUrl = "https://example.com/success",
            CancelUrl = "https://example.com/cancel",
            Discount = new StripeDiscount
            {
                PromotionCodeId = "promo_code_2"
            }
        };

        CheckoutSessionResult result = await creator.CreateSubscriptionSessionAsync(request);
        SubscriptionRecord? stored = await subscriptionStore.GetByBusinessIdAsync("sub_promo_2");

        Assert.NotNull(result.Session);
        Assert.NotNull(stored);
        Assert.Equal(PromotionOutcome.Applied, stored!.PromotionOutcome);
        Assert.Null(stored.PromotionCouponId);
        Assert.Equal("promo_code_2", stored.PromotionCodeId);
    }

    [Fact]
    public async Task CreatePaymentSessionAsync_EmitsStructuredLogWithCorrelationFields()
    {
        FakeCheckoutSessionClient client = new FakeCheckoutSessionClient();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        StripeKitOptions options = new StripeKitOptions();
        IPromotionEligibilityPolicy policy = new AllowAllPromotionEligibilityPolicy();
        StripeCheckoutSessionCreator creator = new StripeCheckoutSessionCreator(
            client,
            paymentStore,
            subscriptionStore,
            options,
            policy,
            null);

        CheckoutPaymentSessionRequest request = new CheckoutPaymentSessionRequest
        {
            UserId = "user_log_1",
            BusinessPaymentId = "pay_log_1",
            Amount = 1200,
            Currency = "usd",
            ItemName = "Test item",
            SuccessUrl = "https://example.com/success",
            CancelUrl = "https://example.com/cancel"
        };

        CapturingTraceListener listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            await creator.CreatePaymentSessionAsync(request);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            listener.Dispose();
        }

        string? json = listener.Messages
            .LastOrDefault(message => message.Contains("\"event_name\":\"checkout.payment.created\"", StringComparison.Ordinal));

        Assert.NotNull(json);

        using JsonDocument document = JsonDocument.Parse(json!);
        JsonElement root = document.RootElement;

        Assert.Equal("checkout.payment.created", root.GetProperty("event_name").GetString());
        Assert.Equal("user_log_1", root.GetProperty("user_id").GetString());
        Assert.Equal("pay_log_1", root.GetProperty("business_payment_id").GetString());
        Assert.Equal("cs_123", root.GetProperty("checkout_session_id").GetString());
        Assert.Equal("pi_123", root.GetProperty("payment_intent_id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("trace_id").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("span_id").GetString()));
    }

    [Fact]
    public async Task CreatePaymentSessionAsync_NullStripeId_WebhookCorrelationByBusinessMetadata_UpdatesRecord()
    {
        const string secret = "whsec_test";
        FakeCheckoutSessionClient client = new FakeCheckoutSessionClient(paymentIntentId: null);
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        StripeKitOptions options = new StripeKitOptions();
        IPromotionEligibilityPolicy policy = new AllowAllPromotionEligibilityPolicy();
        StripeCheckoutSessionCreator creator = new StripeCheckoutSessionCreator(
            client,
            paymentStore,
            subscriptionStore,
            options,
            policy,
            null);

        CheckoutPaymentSessionRequest request = new CheckoutPaymentSessionRequest
        {
            UserId = "user_null_pi_1",
            BusinessPaymentId = "pay_null_pi_1",
            Amount = 1200,
            Currency = "usd",
            ItemName = "Test item",
            SuccessUrl = "https://example.com/success",
            CancelUrl = "https://example.com/cancel"
        };

        await creator.CreatePaymentSessionAsync(request);

        PaymentRecord? before = await paymentStore.GetByBusinessIdAsync("pay_null_pi_1");
        Assert.NotNull(before);
        Assert.Null(before!.PaymentIntentId);

        string payload = "{\"id\":\"evt_corr_pi_1\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"pi_corr_1\",\"object\":\"payment_intent\",\"status\":\"succeeded\",\"metadata\":{\"business_payment_id\":\"pay_null_pi_1\"}}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.succeeded\"}";
        string header = BuildSignatureHeader(payload, secret);

        StripeWebhookProcessor processor = new StripeWebhookProcessor(
            new WebhookSignatureVerifier(),
            new InMemoryWebhookEventStore(),
            paymentStore,
            subscriptionStore,
            new InMemoryRefundRecordStore(),
            new FakeStripeObjectLookup(),
            options);

        WebhookProcessingResult result = await processor.ProcessAsync(payload, header, secret);
        PaymentRecord? after = await paymentStore.GetByBusinessIdAsync("pay_null_pi_1");
        PaymentRecord? byPaymentIntent = await paymentStore.GetByPaymentIntentIdAsync("pi_corr_1");

        Assert.NotNull(result.Outcome);
        Assert.True(result.Outcome!.Succeeded);
        Assert.NotNull(after);
        Assert.Equal(PaymentStatus.Succeeded, after!.Status);
        Assert.Equal("pi_corr_1", after.PaymentIntentId);
        Assert.Equal("pay_null_pi_1", byPaymentIntent!.BusinessPaymentId);
    }

    [Fact]
    public async Task CreateSubscriptionSessionAsync_NullStripeId_WebhookCorrelationByBusinessMetadata_UpdatesRecord()
    {
        const string secret = "whsec_test";
        FakeCheckoutSessionClient client = new FakeCheckoutSessionClient(subscriptionId: null);
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        StripeKitOptions options = new StripeKitOptions();
        IPromotionEligibilityPolicy policy = new AllowAllPromotionEligibilityPolicy();
        StripeCheckoutSessionCreator creator = new StripeCheckoutSessionCreator(
            client,
            paymentStore,
            subscriptionStore,
            options,
            policy,
            null);

        CheckoutSubscriptionSessionRequest request = new CheckoutSubscriptionSessionRequest
        {
            UserId = "user_null_sub_1",
            BusinessSubscriptionId = "sub_null_1",
            PriceId = "price_123",
            SuccessUrl = "https://example.com/success",
            CancelUrl = "https://example.com/cancel"
        };

        await creator.CreateSubscriptionSessionAsync(request);

        SubscriptionRecord? before = await subscriptionStore.GetByBusinessIdAsync("sub_null_1");
        Assert.NotNull(before);
        Assert.Null(before!.SubscriptionId);

        string payload = "{\"id\":\"evt_corr_sub_1\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000100,\"data\":{\"object\":{\"id\":\"sub_corr_1\",\"object\":\"subscription\",\"status\":\"active\",\"customer\":\"cus_corr_1\",\"metadata\":{\"business_subscription_id\":\"sub_null_1\"}}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"customer.subscription.updated\"}";
        string header = BuildSignatureHeader(payload, secret);

        StripeWebhookProcessor processor = new StripeWebhookProcessor(
            new WebhookSignatureVerifier(),
            new InMemoryWebhookEventStore(),
            paymentStore,
            subscriptionStore,
            new InMemoryRefundRecordStore(),
            new FakeStripeObjectLookup(),
            options);

        WebhookProcessingResult result = await processor.ProcessAsync(payload, header, secret);
        SubscriptionRecord? after = await subscriptionStore.GetByBusinessIdAsync("sub_null_1");
        SubscriptionRecord? bySubscriptionId = await subscriptionStore.GetBySubscriptionIdAsync("sub_corr_1");

        Assert.NotNull(result.Outcome);
        Assert.True(result.Outcome!.Succeeded);
        Assert.NotNull(after);
        Assert.Equal(SubscriptionStatus.Active, after!.Status);
        Assert.Equal("sub_corr_1", after.SubscriptionId);
        Assert.Equal("sub_null_1", bySubscriptionId!.BusinessSubscriptionId);
    }

    private sealed class FakeCheckoutSessionClient : ICheckoutSessionClient
    {
        private readonly string _sessionId;
        private readonly string? _subscriptionId;
        private readonly string? _paymentIntentId;
        private readonly string? _customerId;

        public FakeCheckoutSessionClient(
            string sessionId = "cs_123",
            string? subscriptionId = null,
            string? paymentIntentId = "pi_123",
            string? customerId = "cus_123")
        {
            _sessionId = sessionId;
            _subscriptionId = subscriptionId;
            _paymentIntentId = paymentIntentId;
            _customerId = customerId;
        }

        public SessionCreateOptions? LastOptions { get; private set; }
        public string? LastIdempotencyKey { get; private set; }

        public Task<StripeCheckoutSession> CreateAsync(
            SessionCreateOptions options,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            LastOptions = options;
            LastIdempotencyKey = idempotencyKey;

            StripeCheckoutSession session = new StripeCheckoutSession(
                _sessionId,
                "https://example.com/checkout",
                _customerId,
                _paymentIntentId,
                _subscriptionId);

            return Task.FromResult(session);
        }
    }

    private sealed class DenyPromotionEligibilityPolicy : IPromotionEligibilityPolicy
    {
        public Task<PromotionValidationResult> EvaluateAsync(PromotionContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(PromotionValidationResult.Invalid("Denied"));
        }
    }

    private sealed class FakeCustomerResolver : IStripeCustomerResolver
    {
        private readonly string _customerId;

        public FakeCustomerResolver(string customerId)
        {
            _customerId = customerId;
        }

        public Task<string> GetOrCreateCustomerIdAsync(
            string userId,
            string? providedCustomerId = null,
            string? idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_customerId);
        }
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

    private sealed class CapturingTraceListener : TraceListener
    {
        public List<string> Messages { get; } = new List<string>();

        public override void Write(string? message)
        {
            if (message != null)
            {
                Messages.Add(message);
            }
        }

        public override void WriteLine(string? message)
        {
            if (message != null)
            {
                Messages.Add(message);
            }
        }

        public override void WriteLine(string? message, string? category)
        {
            if (message != null)
            {
                Messages.Add(message);
            }
        }
    }

    private sealed class FakeStripeObjectLookup : IStripeObjectLookup
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
