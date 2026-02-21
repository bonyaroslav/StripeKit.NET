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
            if (activity.OperationName == "stripekit.webhook.process" &&
                string.Equals(GetTag(activity, "event_id"), "evt_200", StringComparison.Ordinal))
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
    public async Task ProcessAsync_SubscriptionOutOfOrderDeletedThenDelayedInvoiceSuccess_DoesNotReactivate()
    {
        const string secret = "whsec_test";

        string deletedPayload = "{\"id\":\"evt_sub_order_1\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000100,\"data\":{\"object\":{\"id\":\"sub_order_1\",\"object\":\"subscription\",\"status\":\"canceled\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"customer.subscription.deleted\"}";
        string deletedHeader = BuildSignatureHeader(deletedPayload, secret);

        string delayedInvoiceSuccessPayload = "{\"id\":\"evt_sub_order_2\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"in_order_1\",\"object\":\"invoice\",\"subscription\":\"sub_order_1\",\"status\":\"paid\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"invoice.payment_succeeded\"}";
        string delayedHeader = BuildSignatureHeader(delayedInvoiceSuccessPayload, secret);

        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

        SubscriptionRecord record = new SubscriptionRecord("user_order_1", "biz_sub_order_1", SubscriptionStatus.Active, "cus_order_1", "sub_order_1");
        await subscriptionStore.SaveAsync(record);

        WebhookProcessingResult deleted = await processor.ProcessAsync(deletedPayload, deletedHeader, secret);
        WebhookProcessingResult delayed = await processor.ProcessAsync(delayedInvoiceSuccessPayload, delayedHeader, secret);
        SubscriptionRecord? updated = await subscriptionStore.GetBySubscriptionIdAsync("sub_order_1");

        Assert.True(deleted.Outcome!.Succeeded);
        Assert.True(delayed.Outcome!.Succeeded);
        Assert.NotNull(updated);
        Assert.Equal(SubscriptionStatus.Canceled, updated!.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000100), updated.LastStripeEventCreated);
    }

    [Fact]
    public async Task ProcessAsync_PaymentOutOfOrderSucceededThenDelayedFailed_DoesNotRegress()
    {
        const string secret = "whsec_test";

        string succeededPayload = "{\"id\":\"evt_pay_order_1\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000200,\"data\":{\"object\":{\"id\":\"pi_order_1\",\"object\":\"payment_intent\",\"status\":\"succeeded\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.succeeded\"}";
        string succeededHeader = BuildSignatureHeader(succeededPayload, secret);

        string delayedFailedPayload = "{\"id\":\"evt_pay_order_2\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000100,\"data\":{\"object\":{\"id\":\"pi_order_1\",\"object\":\"payment_intent\",\"status\":\"requires_payment_method\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.payment_failed\"}";
        string delayedHeader = BuildSignatureHeader(delayedFailedPayload, secret);

        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

        PaymentRecord record = new PaymentRecord("user_order_2", "biz_pay_order_2", PaymentStatus.Pending, "pi_order_1", null);
        await paymentStore.SaveAsync(record);

        WebhookProcessingResult succeeded = await processor.ProcessAsync(succeededPayload, succeededHeader, secret);
        WebhookProcessingResult delayed = await processor.ProcessAsync(delayedFailedPayload, delayedHeader, secret);
        PaymentRecord? updated = await paymentStore.GetByPaymentIntentIdAsync("pi_order_1");

        Assert.True(succeeded.Outcome!.Succeeded);
        Assert.True(delayed.Outcome!.Succeeded);
        Assert.NotNull(updated);
        Assert.Equal(PaymentStatus.Succeeded, updated!.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000200), updated.LastStripeEventCreated);
    }

    [Fact]
    public async Task ProcessAsync_PaymentOutOfOrder_EqualCreatedSucceededBeatsFailed()
    {
        const string secret = "whsec_test";
        const long created = 1700000300;

        string failedPayload = "{\"id\":\"evt_pay_equal_1\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000300,\"data\":{\"object\":{\"id\":\"pi_equal_1\",\"object\":\"payment_intent\",\"status\":\"requires_payment_method\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.payment_failed\"}";
        string failedHeader = BuildSignatureHeader(failedPayload, secret);

        string succeededPayload = "{\"id\":\"evt_pay_equal_2\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000300,\"data\":{\"object\":{\"id\":\"pi_equal_1\",\"object\":\"payment_intent\",\"status\":\"succeeded\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.succeeded\"}";
        string succeededHeader = BuildSignatureHeader(succeededPayload, secret);

        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

        PaymentRecord record = new PaymentRecord("user_equal_1", "biz_pay_equal_1", PaymentStatus.Pending, "pi_equal_1", null);
        await paymentStore.SaveAsync(record);

        await processor.ProcessAsync(failedPayload, failedHeader, secret);
        await processor.ProcessAsync(succeededPayload, succeededHeader, secret);
        PaymentRecord? updated = await paymentStore.GetByPaymentIntentIdAsync("pi_equal_1");

        Assert.NotNull(updated);
        Assert.Equal(PaymentStatus.Succeeded, updated!.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(created), updated.LastStripeEventCreated);
    }

    [Fact]
    public async Task ProcessAsync_SubscriptionOutOfOrder_EqualCreatedCanceledBeatsActive()
    {
        const string secret = "whsec_test";
        const long created = 1700000400;

        string activePayload = "{\"id\":\"evt_sub_equal_1\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000400,\"data\":{\"object\":{\"id\":\"in_sub_equal_1\",\"object\":\"invoice\",\"subscription\":\"sub_equal_1\",\"status\":\"paid\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"invoice.payment_succeeded\"}";
        string activeHeader = BuildSignatureHeader(activePayload, secret);

        string canceledPayload = "{\"id\":\"evt_sub_equal_2\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000400,\"data\":{\"object\":{\"id\":\"sub_equal_1\",\"object\":\"subscription\",\"status\":\"canceled\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"customer.subscription.deleted\"}";
        string canceledHeader = BuildSignatureHeader(canceledPayload, secret);

        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

        SubscriptionRecord record = new SubscriptionRecord("user_equal_2", "biz_sub_equal_2", SubscriptionStatus.Incomplete, "cus_equal_2", "sub_equal_1");
        await subscriptionStore.SaveAsync(record);

        await processor.ProcessAsync(activePayload, activeHeader, secret);
        await processor.ProcessAsync(canceledPayload, canceledHeader, secret);
        SubscriptionRecord? updated = await subscriptionStore.GetBySubscriptionIdAsync("sub_equal_1");

        Assert.NotNull(updated);
        Assert.Equal(SubscriptionStatus.Canceled, updated!.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(created), updated.LastStripeEventCreated);
    }

    [Fact]
    public async Task ProcessAsync_PaymentEventWithoutCreated_UpdatesStatus()
    {
        const string secret = "whsec_test";

        string payload = "{\"id\":\"evt_no_created_1\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"data\":{\"object\":{\"id\":\"pi_no_created_1\",\"object\":\"payment_intent\",\"status\":\"requires_payment_method\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.payment_failed\"}";
        string header = BuildSignatureHeader(payload, secret);

        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

        PaymentRecord record = new PaymentRecord("user_no_created_1", "biz_pay_no_created_1", PaymentStatus.Pending, "pi_no_created_1", null);
        await paymentStore.SaveAsync(record);

        WebhookProcessingResult result = await processor.ProcessAsync(payload, header, secret);
        PaymentRecord? updated = await paymentStore.GetByPaymentIntentIdAsync("pi_no_created_1");

        Assert.True(result.Outcome!.Succeeded);
        Assert.NotNull(updated);
        Assert.Equal(PaymentStatus.Failed, updated!.Status);
        Assert.Null(updated.LastStripeEventCreated);
    }

    [Fact]
    public async Task ProcessStripeEventAsync_PaymentOutOfOrder_DelayedFailedDoesNotRegress()
    {
        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

        PaymentRecord record = new PaymentRecord("user_event_order_1", "biz_pay_event_order_1", PaymentStatus.Pending, "pi_event_order_1", null);
        await paymentStore.SaveAsync(record);

        Event succeeded = new Event
        {
            Id = "evt_event_order_1",
            Type = "payment_intent.succeeded",
            Created = DateTimeOffset.FromUnixTimeSeconds(1700000600).UtcDateTime,
            Data = new EventData
            {
                Object = new PaymentIntent
                {
                    Id = "pi_event_order_1",
                    Status = "succeeded"
                }
            }
        };

        Event delayedFailed = new Event
        {
            Id = "evt_event_order_2",
            Type = "payment_intent.payment_failed",
            Created = DateTimeOffset.FromUnixTimeSeconds(1700000500).UtcDateTime,
            Data = new EventData
            {
                Object = new PaymentIntent
                {
                    Id = "pi_event_order_1",
                    Status = "requires_payment_method"
                }
            }
        };

        await processor.ProcessStripeEventAsync(succeeded);
        await processor.ProcessStripeEventAsync(delayedFailed);
        PaymentRecord? updated = await paymentStore.GetByPaymentIntentIdAsync("pi_event_order_1");

        Assert.NotNull(updated);
        Assert.Equal(PaymentStatus.Succeeded, updated!.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000600), updated.LastStripeEventCreated);
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
    public async Task ProcessAsync_RetryAfterFailure_DoesNotReturnDuplicateAndAppliesOutcome()
    {
        string payload = "{\"id\":\"evt_retry_contract_1\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"pi_retry_contract_1\",\"object\":\"payment_intent\",\"status\":\"succeeded\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.succeeded\"}";
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
        PaymentRecord record = new PaymentRecord("user_retry_contract_1", "pay_retry_contract_1", PaymentStatus.Pending, "pi_retry_contract_1", null);
        await paymentStore.SaveAsync(record);

        WebhookProcessingResult second = await processor.ProcessAsync(payload, header, secret);
        PaymentRecord? updated = await paymentStore.GetByPaymentIntentIdAsync("pi_retry_contract_1");

        Assert.NotNull(first.Outcome);
        Assert.False(first.Outcome!.Succeeded);
        Assert.False(first.IsDuplicate);
        Assert.NotNull(second.Outcome);
        Assert.True(second.Outcome!.Succeeded);
        Assert.False(second.IsDuplicate);
        Assert.NotNull(updated);
        Assert.Equal(PaymentStatus.Succeeded, updated!.Status);
    }

    [Fact]
    public async Task ProcessAsync_StaleProcessingLease_AllowsLaterDeliveryToTakeOver()
    {
        string payload = "{\"id\":\"evt_stale_lease_1\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"pi_stale_lease_1\",\"object\":\"payment_intent\",\"status\":\"succeeded\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.succeeded\"}";
        string secret = "whsec_test";
        string header = BuildSignatureHeader(payload, secret);

        DateTimeOffset now = DateTimeOffset.Parse("2026-02-21T12:00:00Z");
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore(() => now, TimeSpan.FromMinutes(1));
        bool firstLease = await eventStore.TryBeginAsync("evt_stale_lease_1");

        StripeKitOptions options = new StripeKitOptions();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

        PaymentRecord record = new PaymentRecord("user_stale_lease_1", "pay_stale_lease_1", PaymentStatus.Pending, "pi_stale_lease_1", null);
        await paymentStore.SaveAsync(record);

        WebhookProcessingResult blocked = await processor.ProcessAsync(payload, header, secret);
        now = now.AddMinutes(2);
        WebhookProcessingResult recovered = await processor.ProcessAsync(payload, header, secret);

        Assert.True(firstLease);
        Assert.False(blocked.IsDuplicate);
        Assert.NotNull(blocked.Outcome);
        Assert.False(blocked.Outcome!.Succeeded);
        Assert.NotNull(recovered.Outcome);
        Assert.True(recovered.Outcome!.Succeeded);
        Assert.False(recovered.IsDuplicate);
    }

    [Fact]
    public async Task ProcessAsync_OutOfOrderGuard_DelayedFailureCannotRegressSucceededPayment()
    {
        const string secret = "whsec_test";

        string succeededPayload = "{\"id\":\"evt_order_guard_1\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000200,\"data\":{\"object\":{\"id\":\"pi_order_guard_1\",\"object\":\"payment_intent\",\"status\":\"succeeded\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.succeeded\"}";
        string succeededHeader = BuildSignatureHeader(succeededPayload, secret);

        string delayedFailedPayload = "{\"id\":\"evt_order_guard_2\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000100,\"data\":{\"object\":{\"id\":\"pi_order_guard_1\",\"object\":\"payment_intent\",\"status\":\"requires_payment_method\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.payment_failed\"}";
        string delayedHeader = BuildSignatureHeader(delayedFailedPayload, secret);

        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

        PaymentRecord record = new PaymentRecord("user_order_guard_1", "pay_order_guard_1", PaymentStatus.Pending, "pi_order_guard_1", null);
        await paymentStore.SaveAsync(record);

        await processor.ProcessAsync(succeededPayload, succeededHeader, secret);
        await processor.ProcessAsync(delayedFailedPayload, delayedHeader, secret);

        PaymentRecord? updated = await paymentStore.GetByPaymentIntentIdAsync("pi_order_guard_1");

        Assert.NotNull(updated);
        Assert.Equal(PaymentStatus.Succeeded, updated!.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000200), updated.LastStripeEventCreated);
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
            if (activity.OperationName == "stripekit.webhook.process" &&
                string.Equals(GetTag(activity, "event_id"), "evt_dup_1", StringComparison.Ordinal))
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
            if (activity.OperationName == "stripekit.webhook.process" &&
                string.Equals(GetTag(activity, "event_id"), "evt_obs_1", StringComparison.Ordinal))
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
            if (activity.OperationName == "stripekit.webhook.process" &&
                string.Equals(GetTag(activity, "event_id"), "evt_refund_200", StringComparison.Ordinal))
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
