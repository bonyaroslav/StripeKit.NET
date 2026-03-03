using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stripe;

namespace StripeKit.Tests;

public class StripeWebhookProcessorTests
{
    [Fact]
    public async Task ProcessAsync_PaymentIntentSucceeded_UpdatesPaymentStatus()
    {
        WebhookProcessorTestContext context = CreateContext();
        await context.SavePaymentAsync("user_1", "pay_100", PaymentStatus.Pending, "pi_100");

        WebhookProcessingResult result = await context.ProcessAsync(
            WebhookPayloads.PaymentIntent("evt_100", "pi_100", "payment_intent.succeeded", "succeeded"));

        PaymentRecord? updated = await context.PaymentStore.GetByPaymentIntentIdAsync("pi_100");

        AssertSucceeded(result);
        Assert.Equal(PaymentStatus.Succeeded, updated!.Status);
    }

    [Fact]
    public async Task ProcessAsync_InvoicePaymentFailed_UpdatesSubscriptionStatus()
    {
        WebhookProcessorTestContext context = CreateContext();
        await context.SaveSubscriptionAsync("user_2", "sub_200", SubscriptionStatus.Active, "cus_200", "sub_200");

        (WebhookProcessingResult result, Activity? activity) = await CaptureWebhookActivityAsync(
            "evt_200",
            () => context.ProcessAsync(
                WebhookPayloads.Invoice("evt_200", "in_200", "invoice.payment_failed", "sub_200", "open")));

        SubscriptionRecord? updated = await context.SubscriptionStore.GetBySubscriptionIdAsync("sub_200");

        AssertSucceeded(result);
        Assert.Equal(SubscriptionStatus.PastDue, updated!.Status);
        Assert.Equal("in_200", GetTag(activity!, "invoice_id"));
    }

    [Fact]
    public async Task ProcessAsync_ThinInvoiceEvent_UsesLookup()
    {
        WebhookProcessorTestContext context = CreateContext(lookup: new FakeStripeObjectLookup
        {
            SubscriptionId = "sub_300"
        });
        await context.SaveSubscriptionAsync("user_3", "sub_300", SubscriptionStatus.Incomplete, "cus_300", "sub_300");

        WebhookProcessingResult result = await context.ProcessAsync(
            WebhookPayloads.Invoice("evt_300", "in_300", "invoice.payment_succeeded", null, null));

        SubscriptionRecord? updated = await context.SubscriptionStore.GetBySubscriptionIdAsync("sub_300");

        AssertSucceeded(result);
        Assert.Equal(SubscriptionStatus.Active, updated!.Status);
    }

    [Fact]
    public async Task ProcessAsync_SubscriptionOutOfOrderDeletedThenDelayedInvoiceSuccess_DoesNotReactivate()
    {
        WebhookProcessorTestContext context = CreateContext();
        await context.SaveSubscriptionAsync("user_order_1", "biz_sub_order_1", SubscriptionStatus.Active, "cus_order_1", "sub_order_1");

        WebhookProcessingResult deleted = await context.ProcessAsync(
            WebhookPayloads.Subscription("evt_sub_order_1", "sub_order_1", "customer.subscription.deleted", "canceled", 1700000100));
        WebhookProcessingResult delayed = await context.ProcessAsync(
            WebhookPayloads.Invoice("evt_sub_order_2", "in_order_1", "invoice.payment_succeeded", "sub_order_1", "paid", 1700000000));
        SubscriptionRecord? updated = await context.SubscriptionStore.GetBySubscriptionIdAsync("sub_order_1");

        AssertSucceeded(deleted);
        AssertSucceeded(delayed);
        Assert.Equal(SubscriptionStatus.Canceled, updated!.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000100), updated.LastStripeEventCreated);
    }

    [Fact]
    public async Task ProcessAsync_PaymentOutOfOrderSucceededThenDelayedFailed_DoesNotRegress()
    {
        WebhookProcessorTestContext context = CreateContext();
        await context.SavePaymentAsync("user_order_2", "biz_pay_order_2", PaymentStatus.Pending, "pi_order_1");

        WebhookProcessingResult succeeded = await context.ProcessAsync(
            WebhookPayloads.PaymentIntent("evt_pay_order_1", "pi_order_1", "payment_intent.succeeded", "succeeded", 1700000200));
        WebhookProcessingResult delayed = await context.ProcessAsync(
            WebhookPayloads.PaymentIntent("evt_pay_order_2", "pi_order_1", "payment_intent.payment_failed", "requires_payment_method", 1700000100));
        PaymentRecord? updated = await context.PaymentStore.GetByPaymentIntentIdAsync("pi_order_1");

        AssertSucceeded(succeeded);
        AssertSucceeded(delayed);
        Assert.Equal(PaymentStatus.Succeeded, updated!.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000200), updated.LastStripeEventCreated);
    }

    [Fact]
    public async Task ProcessAsync_PaymentOutOfOrder_EqualCreatedSucceededBeatsFailed()
    {
        const long created = 1700000300;
        WebhookProcessorTestContext context = CreateContext();
        await context.SavePaymentAsync("user_equal_1", "biz_pay_equal_1", PaymentStatus.Pending, "pi_equal_1");

        await context.ProcessAsync(
            WebhookPayloads.PaymentIntent("evt_pay_equal_1", "pi_equal_1", "payment_intent.payment_failed", "requires_payment_method", created));
        await context.ProcessAsync(
            WebhookPayloads.PaymentIntent("evt_pay_equal_2", "pi_equal_1", "payment_intent.succeeded", "succeeded", created));
        PaymentRecord? updated = await context.PaymentStore.GetByPaymentIntentIdAsync("pi_equal_1");

        Assert.Equal(PaymentStatus.Succeeded, updated!.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(created), updated.LastStripeEventCreated);
    }

    [Fact]
    public async Task ProcessAsync_SubscriptionOutOfOrder_EqualCreatedCanceledBeatsActive()
    {
        const long created = 1700000400;
        WebhookProcessorTestContext context = CreateContext();
        await context.SaveSubscriptionAsync("user_equal_2", "biz_sub_equal_2", SubscriptionStatus.Incomplete, "cus_equal_2", "sub_equal_1");

        await context.ProcessAsync(
            WebhookPayloads.Invoice("evt_sub_equal_1", "in_sub_equal_1", "invoice.payment_succeeded", "sub_equal_1", "paid", created));
        await context.ProcessAsync(
            WebhookPayloads.Subscription("evt_sub_equal_2", "sub_equal_1", "customer.subscription.deleted", "canceled", created));
        SubscriptionRecord? updated = await context.SubscriptionStore.GetBySubscriptionIdAsync("sub_equal_1");

        Assert.Equal(SubscriptionStatus.Canceled, updated!.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(created), updated.LastStripeEventCreated);
    }

    [Fact]
    public async Task ProcessAsync_PaymentEventWithoutCreated_UpdatesStatus()
    {
        WebhookProcessorTestContext context = CreateContext();
        await context.SavePaymentAsync("user_no_created_1", "biz_pay_no_created_1", PaymentStatus.Pending, "pi_no_created_1");

        WebhookProcessingResult result = await context.ProcessAsync(
            WebhookPayloads.PaymentIntent("evt_no_created_1", "pi_no_created_1", "payment_intent.payment_failed", "requires_payment_method", null));
        PaymentRecord? updated = await context.PaymentStore.GetByPaymentIntentIdAsync("pi_no_created_1");

        AssertSucceeded(result);
        Assert.Equal(PaymentStatus.Failed, updated!.Status);
        Assert.Null(updated.LastStripeEventCreated);
    }

    [Fact]
    public async Task ProcessStripeEventAsync_PaymentOutOfOrder_DelayedFailedDoesNotRegress()
    {
        WebhookProcessorTestContext context = CreateContext();
        await context.SavePaymentAsync("user_event_order_1", "biz_pay_event_order_1", PaymentStatus.Pending, "pi_event_order_1");

        await context.ProcessStripeEventAsync(
            StripeEvents.PaymentIntent("evt_event_order_1", "payment_intent.succeeded", "pi_event_order_1", "succeeded", 1700000600));
        await context.ProcessStripeEventAsync(
            StripeEvents.PaymentIntent("evt_event_order_2", "payment_intent.payment_failed", "pi_event_order_1", "requires_payment_method", 1700000500));
        PaymentRecord? updated = await context.PaymentStore.GetByPaymentIntentIdAsync("pi_event_order_1");

        Assert.Equal(PaymentStatus.Succeeded, updated!.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000600), updated.LastStripeEventCreated);
    }

    [Fact]
    public async Task ProcessStripeEventAsync_DuplicateEvent_ReturnsRecordedOutcome()
    {
        WebhookProcessorTestContext context = CreateContext();
        Event stripeEvent = StripeEvents.PaymentIntent("evt_event_dup_1", "payment_intent.succeeded", "pi_event_dup_1", "succeeded");
        await context.SavePaymentAsync("user_event_dup_1", "biz_pay_event_dup_1", PaymentStatus.Pending, "pi_event_dup_1");

        WebhookProcessingResult first = await context.ProcessStripeEventAsync(stripeEvent);
        WebhookProcessingResult second = await context.ProcessStripeEventAsync(stripeEvent);

        AssertSucceeded(first);
        AssertSucceeded(second, isDuplicate: true);
    }

    [Fact]
    public async Task ProcessStripeEventAsync_FirstAttemptFailed_SecondDeliveryRetriesAndSucceeds()
    {
        WebhookProcessorTestContext context = CreateContext();
        Event stripeEvent = StripeEvents.PaymentIntent("evt_event_retry_1", "payment_intent.succeeded", "pi_event_retry_1", "succeeded");

        WebhookProcessingResult first = await context.ProcessStripeEventAsync(stripeEvent);
        await context.SavePaymentAsync("user_event_retry_1", "biz_pay_event_retry_1", PaymentStatus.Pending, "pi_event_retry_1");
        WebhookProcessingResult second = await context.ProcessStripeEventAsync(stripeEvent);
        PaymentRecord? updated = await context.PaymentStore.GetByPaymentIntentIdAsync("pi_event_retry_1");

        AssertFailed(first);
        AssertSucceeded(second);
        Assert.Equal(PaymentStatus.Succeeded, updated!.Status);
    }

    [Fact]
    public async Task ProcessAsync_DuplicateEvent_ReturnsRecordedOutcome()
    {
        WebhookProcessorTestContext context = CreateContext();
        string payload = WebhookPayloads.PaymentIntent("evt_400", "pi_400", "payment_intent.succeeded", null);
        await context.SavePaymentAsync("user_4", "pay_400", PaymentStatus.Pending, "pi_400");

        WebhookProcessingResult first = await context.ProcessAsync(payload);
        WebhookProcessingResult second = await context.ProcessAsync(payload);

        AssertSucceeded(first);
        AssertSucceeded(second, isDuplicate: true);
    }

    [Fact]
    public async Task ProcessAsync_FirstAttemptFailed_SecondDeliveryRetriesAndSucceeds()
    {
        WebhookProcessorTestContext context = CreateContext();
        string payload = WebhookPayloads.PaymentIntent("evt_retry_1", "pi_retry_1", "payment_intent.succeeded", "succeeded");

        WebhookProcessingResult first = await context.ProcessAsync(payload);
        await context.SavePaymentAsync("user_retry_1", "pay_retry_1", PaymentStatus.Pending, "pi_retry_1");
        WebhookProcessingResult second = await context.ProcessAsync(payload);
        PaymentRecord? updated = await context.PaymentStore.GetByPaymentIntentIdAsync("pi_retry_1");

        AssertFailed(first);
        AssertSucceeded(second);
        Assert.Equal(PaymentStatus.Succeeded, updated!.Status);
    }

    [Fact]
    public async Task ProcessAsync_RetryAfterFailure_DoesNotReturnDuplicateAndAppliesOutcome()
    {
        WebhookProcessorTestContext context = CreateContext();
        string payload = WebhookPayloads.PaymentIntent("evt_retry_contract_1", "pi_retry_contract_1", "payment_intent.succeeded", "succeeded");

        WebhookProcessingResult first = await context.ProcessAsync(payload);
        await context.SavePaymentAsync("user_retry_contract_1", "pay_retry_contract_1", PaymentStatus.Pending, "pi_retry_contract_1");
        WebhookProcessingResult second = await context.ProcessAsync(payload);
        PaymentRecord? updated = await context.PaymentStore.GetByPaymentIntentIdAsync("pi_retry_contract_1");

        AssertFailed(first);
        AssertSucceeded(second);
        Assert.Equal(PaymentStatus.Succeeded, updated!.Status);
    }

    [Fact]
    public async Task ProcessAsync_StaleProcessingLease_AllowsLaterDeliveryToTakeOver()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-02-21T12:00:00Z");
        InMemoryWebhookEventStore eventStore = new InMemoryWebhookEventStore(() => now, TimeSpan.FromMinutes(1));
        bool firstLease = await eventStore.TryBeginAsync("evt_stale_lease_1");
        WebhookProcessorTestContext context = CreateContext(eventStore: eventStore);
        string payload = WebhookPayloads.PaymentIntent("evt_stale_lease_1", "pi_stale_lease_1", "payment_intent.succeeded", "succeeded");
        await context.SavePaymentAsync("user_stale_lease_1", "pay_stale_lease_1", PaymentStatus.Pending, "pi_stale_lease_1");

        WebhookProcessingResult blocked = await context.ProcessAsync(payload);
        now = now.AddMinutes(2);
        WebhookProcessingResult recovered = await context.ProcessAsync(payload);

        Assert.True(firstLease);
        AssertFailed(blocked);
        AssertSucceeded(recovered);
    }

    [Fact]
    public async Task ProcessAsync_OutOfOrderGuard_DelayedFailureCannotRegressSucceededPayment()
    {
        WebhookProcessorTestContext context = CreateContext();
        await context.SavePaymentAsync("user_order_guard_1", "pay_order_guard_1", PaymentStatus.Pending, "pi_order_guard_1");

        await context.ProcessAsync(
            WebhookPayloads.PaymentIntent("evt_order_guard_1", "pi_order_guard_1", "payment_intent.succeeded", "succeeded", 1700000200));
        await context.ProcessAsync(
            WebhookPayloads.PaymentIntent("evt_order_guard_2", "pi_order_guard_1", "payment_intent.payment_failed", "requires_payment_method", 1700000100));

        PaymentRecord? updated = await context.PaymentStore.GetByPaymentIntentIdAsync("pi_order_guard_1");

        Assert.Equal(PaymentStatus.Succeeded, updated!.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000200), updated.LastStripeEventCreated);
    }

    [Fact]
    public async Task ProcessAsync_DuplicateEvent_EmitsDuplicateTag()
    {
        WebhookProcessorTestContext context = CreateContext();
        string payload = WebhookPayloads.PaymentIntent("evt_dup_1", "pi_dup_1", "payment_intent.succeeded", null);
        await context.SavePaymentAsync("user_dup_1", "pay_dup_1", PaymentStatus.Pending, "pi_dup_1");
        await context.ProcessAsync(payload);

        (WebhookProcessingResult result, Activity? activity) = await CaptureWebhookActivityAsync(
            "evt_dup_1",
            () => context.ProcessAsync(payload));

        AssertSucceeded(result, isDuplicate: true);
        Assert.Equal("True", GetTag(activity!, "duplicate"));
    }

    [Fact]
    public async Task ProcessAsync_EmitsCorrelationTags()
    {
        WebhookProcessorTestContext context = CreateContext();
        await context.SavePaymentAsync("user_obs_3", "pay_obs_3", PaymentStatus.Pending, "pi_obs_1");

        (WebhookProcessingResult result, Activity? activity) = await CaptureWebhookActivityAsync(
            "evt_obs_1",
            () => context.ProcessAsync(
                WebhookPayloads.PaymentIntent("evt_obs_1", "pi_obs_1", "payment_intent.succeeded", "succeeded")));

        AssertSucceeded(result);
        Assert.NotNull(activity);
        Assert.Equal("evt_obs_1", GetTag(activity, "event_id"));
        Assert.Equal("payment_intent.succeeded", GetTag(activity, "event_type"));
        Assert.Equal("user_obs_3", GetTag(activity, "user_id"));
        Assert.Equal("pay_obs_3", GetTag(activity, "business_payment_id"));
        Assert.Equal("pi_obs_1", GetTag(activity, "payment_intent_id"));
    }

    [Fact]
    public async Task ProcessAsync_RefundUpdated_UpdatesRefundStatus()
    {
        WebhookProcessorTestContext context = CreateContext(new StripeKitOptions
        {
            EnableRefunds = true
        });
        await context.SaveRefundAsync("user_9", "refund_100", "pay_100", RefundStatus.Pending, "pi_100", "re_100");

        WebhookProcessingResult result = await context.ProcessAsync(
            WebhookPayloads.Refund("evt_refund_1", "re_100", "refund.updated", "failed", "pi_100"));
        RefundRecord? updated = await context.RefundStore.GetByRefundIdAsync("re_100");

        AssertSucceeded(result);
        Assert.Equal(RefundStatus.Failed, updated!.Status);
    }

    [Fact]
    public async Task ProcessStripeEventAsync_RefundUpdated_UpdatesStatusAndEmitsTags()
    {
        WebhookProcessorTestContext context = CreateContext(new StripeKitOptions
        {
            EnableRefunds = true
        });
        await context.SaveRefundAsync("user_10", "refund_200", "pay_200", RefundStatus.Pending, "pi_200", "re_200");

        (WebhookProcessingResult result, Activity? activity) = await CaptureWebhookActivityAsync(
            "evt_refund_200",
            () => context.ProcessStripeEventAsync(
                StripeEvents.Refund("evt_refund_200", "refund.updated", "re_200", "succeeded", "pi_200")));
        RefundRecord? updated = await context.RefundStore.GetByRefundIdAsync("re_200");

        AssertSucceeded(result);
        Assert.Equal(RefundStatus.Succeeded, updated!.Status);
        Assert.NotNull(activity);
        Assert.Equal("evt_refund_200", GetTag(activity, "event_id"));
        Assert.Equal("refund.updated", GetTag(activity, "event_type"));
        Assert.Equal("re_200", GetTag(activity, "refund_id"));
        Assert.Equal("pi_200", GetTag(activity, "payment_intent_id"));
    }

    private static WebhookProcessorTestContext CreateContext(
        StripeKitOptions? options = null,
        IWebhookEventStore? eventStore = null,
        FakeStripeObjectLookup? lookup = null)
    {
        return new WebhookProcessorTestContext(options, eventStore, lookup);
    }

    private static void AssertSucceeded(WebhookProcessingResult result, bool isDuplicate = false)
    {
        Assert.Equal(isDuplicate, result.IsDuplicate);
        Assert.NotNull(result.Outcome);
        Assert.True(result.Outcome!.Succeeded);
    }

    private static void AssertFailed(WebhookProcessingResult result, bool isDuplicate = false)
    {
        Assert.Equal(isDuplicate, result.IsDuplicate);
        Assert.NotNull(result.Outcome);
        Assert.False(result.Outcome!.Succeeded);
    }

    private static async Task<(WebhookProcessingResult Result, Activity? Activity)> CaptureWebhookActivityAsync(
        string eventId,
        Func<Task<WebhookProcessingResult>> action)
    {
        Activity? captured = null;
        using ActivityListener listener = CreateListener(activity =>
        {
            if (activity.OperationName == "stripekit.webhook.process" &&
                string.Equals(GetTag(activity, "event_id"), eventId, StringComparison.Ordinal))
            {
                captured = activity;
            }
        });

        WebhookProcessingResult result = await action();
        return (result, captured);
    }

    private sealed class WebhookProcessorTestContext
    {
        private const string Secret = "whsec_test";

        public WebhookProcessorTestContext(
            StripeKitOptions? options = null,
            IWebhookEventStore? eventStore = null,
            FakeStripeObjectLookup? lookup = null)
        {
            Options = options ?? new StripeKitOptions();
            EventStore = eventStore ?? new InMemoryWebhookEventStore();
            PaymentStore = new InMemoryPaymentRecordStore();
            SubscriptionStore = new InMemorySubscriptionRecordStore();
            RefundStore = new InMemoryRefundRecordStore();
            Lookup = lookup ?? new FakeStripeObjectLookup();
            Processor = new StripeWebhookProcessor(
                new WebhookSignatureVerifier(),
                EventStore,
                PaymentStore,
                SubscriptionStore,
                RefundStore,
                Lookup,
                Options);
        }

        public StripeKitOptions Options { get; }
        public IWebhookEventStore EventStore { get; }
        public InMemoryPaymentRecordStore PaymentStore { get; }
        public InMemorySubscriptionRecordStore SubscriptionStore { get; }
        public InMemoryRefundRecordStore RefundStore { get; }
        public FakeStripeObjectLookup Lookup { get; }
        public StripeWebhookProcessor Processor { get; }

        public Task<WebhookProcessingResult> ProcessAsync(string payload)
        {
            string header = BuildSignatureHeader(payload, Secret);
            return Processor.ProcessAsync(payload, header, Secret);
        }

        public Task<WebhookProcessingResult> ProcessStripeEventAsync(Event stripeEvent)
        {
            return Processor.ProcessStripeEventAsync(stripeEvent);
        }

        public Task SavePaymentAsync(string userId, string businessPaymentId, PaymentStatus status, string? paymentIntentId)
        {
            return PaymentStore.SaveAsync(new PaymentRecord(userId, businessPaymentId, status, paymentIntentId, null));
        }

        public Task SaveSubscriptionAsync(
            string userId,
            string businessSubscriptionId,
            SubscriptionStatus status,
            string? customerId,
            string? subscriptionId)
        {
            return SubscriptionStore.SaveAsync(new SubscriptionRecord(
                userId,
                businessSubscriptionId,
                status,
                customerId,
                subscriptionId));
        }

        public Task SaveRefundAsync(
            string userId,
            string businessRefundId,
            string businessPaymentId,
            RefundStatus status,
            string? paymentIntentId,
            string? refundId)
        {
            return RefundStore.SaveAsync(new RefundRecord(
                userId,
                businessRefundId,
                businessPaymentId,
                status,
                paymentIntentId,
                refundId));
        }
    }

    private static class WebhookPayloads
    {
        public static string PaymentIntent(
            string eventId,
            string paymentIntentId,
            string eventType,
            string? status,
            long? created = 1700000000,
            string? businessPaymentId = null)
        {
            return Serialize(new
            {
                id = eventId,
                @object = "event",
                api_version = "2022-11-15",
                created,
                data = new
                {
                    @object = new
                    {
                        id = paymentIntentId,
                        @object = "payment_intent",
                        status,
                        metadata = businessPaymentId == null
                            ? null
                            : new Dictionary<string, string>
                            {
                                ["business_payment_id"] = businessPaymentId
                            }
                    }
                },
                livemode = false,
                pending_webhooks = 1,
                type = eventType
            });
        }

        public static string Invoice(
            string eventId,
            string invoiceId,
            string eventType,
            string? subscriptionId,
            string? status,
            long? created = 1700000000)
        {
            return Serialize(new
            {
                id = eventId,
                @object = "event",
                api_version = "2022-11-15",
                created,
                data = new
                {
                    @object = new
                    {
                        id = invoiceId,
                        @object = "invoice",
                        subscription = subscriptionId,
                        status
                    }
                },
                livemode = false,
                pending_webhooks = 1,
                type = eventType
            });
        }

        public static string Subscription(
            string eventId,
            string subscriptionId,
            string eventType,
            string? status,
            long? created = 1700000000,
            string? customerId = null,
            string? businessSubscriptionId = null)
        {
            return Serialize(new
            {
                id = eventId,
                @object = "event",
                api_version = "2022-11-15",
                created,
                data = new
                {
                    @object = new
                    {
                        id = subscriptionId,
                        @object = "subscription",
                        status,
                        customer = customerId,
                        metadata = businessSubscriptionId == null
                            ? null
                            : new Dictionary<string, string>
                            {
                                ["business_subscription_id"] = businessSubscriptionId
                            }
                    }
                },
                livemode = false,
                pending_webhooks = 1,
                type = eventType
            });
        }

        public static string Refund(
            string eventId,
            string refundId,
            string eventType,
            string? status,
            string? paymentIntentId,
            long? created = 1700000000)
        {
            return Serialize(new
            {
                id = eventId,
                @object = "event",
                api_version = "2022-11-15",
                created,
                data = new
                {
                    @object = new
                    {
                        id = refundId,
                        @object = "refund",
                        status,
                        payment_intent = paymentIntentId
                    }
                },
                livemode = false,
                pending_webhooks = 1,
                type = eventType
            });
        }

        private static string Serialize(object payload)
        {
            return JsonSerializer.Serialize(payload, SerializerOptions);
        }
    }

    private static class StripeEvents
    {
        public static Event PaymentIntent(
            string eventId,
            string eventType,
            string paymentIntentId,
            string? status,
            long? created = null)
        {
            Event stripeEvent = new Event
            {
                Id = eventId,
                Type = eventType,
                Data = new EventData
                {
                    Object = new PaymentIntent
                    {
                        Id = paymentIntentId,
                        Status = status
                    }
                }
            };

            if (created.HasValue)
            {
                stripeEvent.Created = DateTimeOffset.FromUnixTimeSeconds(created.Value).UtcDateTime;
            }

            return stripeEvent;
        }

        public static Event Refund(
            string eventId,
            string eventType,
            string refundId,
            string? status,
            string? paymentIntentId)
        {
            return new Event
            {
                Id = eventId,
                Type = eventType,
                Data = new EventData
                {
                    Object = new Refund
                    {
                        Id = refundId,
                        Status = status,
                        PaymentIntentId = paymentIntentId
                    }
                }
            };
        }
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

    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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
