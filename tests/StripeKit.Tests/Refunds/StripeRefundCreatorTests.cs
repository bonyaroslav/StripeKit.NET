using System.Threading;
using System.Diagnostics;
using Stripe;

namespace StripeKit.Tests;

public class StripeRefundCreatorTests
{
    [Fact]
    public async Task CreateRefundAsync_UsesGeneratedIdempotencyKeyAndStoresRecord()
    {
        FakeRefundClient client = new FakeRefundClient();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        StripeKitOptions options = new StripeKitOptions
        {
            EnablePayments = true,
            EnableBilling = true,
            EnablePromotions = true,
            EnableWebhooks = true,
            EnableRefunds = true
        };
        StripeRefundCreator creator = new StripeRefundCreator(client, paymentStore, refundStore, options);

        PaymentRecord payment = new PaymentRecord("user_1", "pay_1", PaymentStatus.Succeeded, "pi_123", null);
        await paymentStore.SaveAsync(payment);

        RefundRequest request = new RefundRequest
        {
            UserId = "user_1",
            BusinessRefundId = "refund_1",
            BusinessPaymentId = "pay_1"
        };

        RefundResult result = await creator.CreateRefundAsync(request);

        string expectedKey = IdempotencyKeyFactory.Create("refund", "refund_1");
        Assert.Equal(expectedKey, client.LastIdempotencyKey);
        Assert.NotNull(result.Refund);
        Assert.Equal("re_123", result.Refund.Id);

        RefundRecord? stored = await refundStore.GetByBusinessIdAsync("refund_1");
        Assert.NotNull(stored);
        Assert.Equal(RefundStatus.Succeeded, stored!.Status);
        Assert.Equal("re_123", stored.RefundId);
        Assert.Equal("pi_123", stored.PaymentIntentId);
        Assert.Equal("pay_1", stored.BusinessPaymentId);

        Assert.NotNull(client.LastOptions);
        Assert.Equal("pi_123", client.LastOptions!.PaymentIntent);
        Assert.True(client.LastOptions.Metadata!.ContainsKey("business_refund_id"));
    }

    [Fact]
    public async Task CreateRefundAsync_ProvidedIdempotencyKey_UsesProvidedValue()
    {
        FakeRefundClient client = new FakeRefundClient();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        StripeKitOptions options = new StripeKitOptions
        {
            EnableRefunds = true
        };
        StripeRefundCreator creator = new StripeRefundCreator(client, paymentStore, refundStore, options);

        PaymentRecord payment = new PaymentRecord("user_1", "pay_10", PaymentStatus.Succeeded, "pi_10", null);
        await paymentStore.SaveAsync(payment);

        RefundRequest request = new RefundRequest
        {
            UserId = "user_1",
            BusinessRefundId = "refund_10",
            BusinessPaymentId = "pay_10",
            IdempotencyKey = "custom-key-10"
        };

        await creator.CreateRefundAsync(request);

        Assert.Equal("custom-key-10", client.LastIdempotencyKey);
    }

    [Fact]
    public async Task CreateRefundAsync_PaymentNotFound_Throws()
    {
        FakeRefundClient client = new FakeRefundClient();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        StripeKitOptions options = new StripeKitOptions
        {
            EnableRefunds = true
        };
        StripeRefundCreator creator = new StripeRefundCreator(client, paymentStore, refundStore, options);

        RefundRequest request = new RefundRequest
        {
            UserId = "user_2",
            BusinessRefundId = "refund_2",
            BusinessPaymentId = "pay_missing"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => creator.CreateRefundAsync(request));
    }

    [Fact]
    public async Task CreateRefundAsync_PaymentNotSucceeded_Throws()
    {
        FakeRefundClient client = new FakeRefundClient();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        StripeKitOptions options = new StripeKitOptions
        {
            EnableRefunds = true
        };
        StripeRefundCreator creator = new StripeRefundCreator(client, paymentStore, refundStore, options);

        PaymentRecord payment = new PaymentRecord("user_2", "pay_2", PaymentStatus.Pending, "pi_456", null);
        await paymentStore.SaveAsync(payment);

        RefundRequest request = new RefundRequest
        {
            UserId = "user_2",
            BusinessRefundId = "refund_2",
            BusinessPaymentId = "pay_2"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => creator.CreateRefundAsync(request));
    }

    [Fact]
    public async Task CreateRefundAsync_PaymentUserMismatch_Throws()
    {
        FakeRefundClient client = new FakeRefundClient();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        StripeKitOptions options = new StripeKitOptions
        {
            EnableRefunds = true
        };
        StripeRefundCreator creator = new StripeRefundCreator(client, paymentStore, refundStore, options);

        PaymentRecord payment = new PaymentRecord("user_3", "pay_3", PaymentStatus.Succeeded, "pi_789", null);
        await paymentStore.SaveAsync(payment);

        RefundRequest request = new RefundRequest
        {
            UserId = "user_4",
            BusinessRefundId = "refund_3",
            BusinessPaymentId = "pay_3"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => creator.CreateRefundAsync(request));
    }

    [Fact]
    public async Task CreateRefundAsync_RefundsDisabled_Throws()
    {
        FakeRefundClient client = new FakeRefundClient();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        StripeKitOptions options = new StripeKitOptions
        {
            EnableRefunds = false
        };
        StripeRefundCreator creator = new StripeRefundCreator(client, paymentStore, refundStore, options);

        RefundRequest request = new RefundRequest
        {
            UserId = "user_5",
            BusinessRefundId = "refund_5",
            BusinessPaymentId = "pay_5"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => creator.CreateRefundAsync(request));
    }

    [Fact]
    public async Task CreateRefundAsync_EmitsCorrelationTags()
    {
        FakeRefundClient client = new FakeRefundClient();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        StripeKitOptions options = new StripeKitOptions
        {
            EnableRefunds = true
        };
        StripeRefundCreator creator = new StripeRefundCreator(client, paymentStore, refundStore, options);

        PaymentRecord payment = new PaymentRecord("user_obs_2", "pay_obs_2", PaymentStatus.Succeeded, "pi_obs_2", null);
        await paymentStore.SaveAsync(payment);

        RefundRequest request = new RefundRequest
        {
            UserId = "user_obs_2",
            BusinessRefundId = "refund_obs_2",
            BusinessPaymentId = "pay_obs_2"
        };

        Activity? captured = null;
        using ActivityListener listener = CreateListener(activity =>
        {
            if (activity.OperationName == "stripekit.refund.create")
            {
                captured = activity;
            }
        });

        await creator.CreateRefundAsync(request);

        Assert.NotNull(captured);
        Assert.Equal("user_obs_2", GetTag(captured!, "user_id"));
        Assert.Equal("refund_obs_2", GetTag(captured!, "business_refund_id"));
        Assert.Equal("pay_obs_2", GetTag(captured!, "business_payment_id"));
        Assert.Equal("pi_obs_2", GetTag(captured!, "payment_intent_id"));
        Assert.Equal("re_123", GetTag(captured!, "refund_id"));
    }

    private sealed class FakeRefundClient : IRefundClient
    {
        public RefundCreateOptions? LastOptions { get; private set; }
        public string? LastIdempotencyKey { get; private set; }

        public Task<StripeRefund> CreateAsync(
            RefundCreateOptions options,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            LastOptions = options;
            LastIdempotencyKey = idempotencyKey;

            StripeRefund refund = new StripeRefund("re_123", "pi_123", RefundStatus.Succeeded);
            return Task.FromResult(refund);
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
}
