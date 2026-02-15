using System.Threading;
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

    private sealed class FakeCheckoutSessionClient : ICheckoutSessionClient
    {
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
                "cs_123",
                "https://example.com/checkout",
                "cus_123",
                "pi_123",
                null);

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
}
