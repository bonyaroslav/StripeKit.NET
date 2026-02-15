using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Stripe;

namespace StripeKit.Tests;

public class StripeEventReconcilerTests
{
    [Fact]
    public async Task ReconcileAsync_ProcessesEventsAndUpdatesRecords()
    {
        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

        PaymentRecord payment = new PaymentRecord("user_9", "pay_900", PaymentStatus.Pending, "pi_900", null);
        SubscriptionRecord subscription = new SubscriptionRecord("user_9", "sub_901", SubscriptionStatus.Active, "cus_901", "sub_901");
        await paymentStore.SaveAsync(payment);
        await subscriptionStore.SaveAsync(subscription);

        StripeList<Event> events = new StripeList<Event>
        {
            Data = new List<Event>
            {
                new Event
                {
                    Id = "evt_900",
                    Type = "payment_intent.succeeded",
                    Data = new EventData
                    {
                        Object = new PaymentIntent
                        {
                            Id = "pi_900",
                            Status = "succeeded",
                            CustomerId = "cus_900"
                        }
                    }
                },
                new Event
                {
                    Id = "evt_901",
                    Type = "invoice.payment_failed",
                    Data = new EventData
                    {
                        Object = new Invoice
                        {
                            Id = "in_901",
                            Status = "open",
                            SubscriptionId = "sub_901",
                            CustomerId = "cus_901"
                        }
                    }
                }
            },
            HasMore = false
        };

        IStripeEventClient eventClient = new FakeStripeEventClient(events);
        StripeEventReconciler reconciler = new StripeEventReconciler(eventClient, processor);

        ReconciliationResult result = await reconciler.ReconcileAsync(new ReconciliationRequest
        {
            Limit = 2,
            CreatedAfter = DateTimeOffset.UtcNow.AddDays(-1)
        });

        PaymentRecord? updatedPayment = await paymentStore.GetByPaymentIntentIdAsync("pi_900");
        SubscriptionRecord? updatedSubscription = await subscriptionStore.GetBySubscriptionIdAsync("sub_901");

        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Processed);
        Assert.Equal(0, result.Duplicates);
        Assert.Equal(0, result.Failed);
        Assert.Equal(PaymentStatus.Succeeded, updatedPayment!.Status);
        Assert.Equal(SubscriptionStatus.PastDue, updatedSubscription!.Status);
    }

    [Fact]
    public async Task ReconcileAsync_DuplicateEvents_AreCounted()
    {
        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

        PaymentRecord payment = new PaymentRecord("user_9", "pay_900", PaymentStatus.Pending, "pi_900", null);
        await paymentStore.SaveAsync(payment);

        StripeList<Event> events = new StripeList<Event>
        {
            Data = new List<Event>
            {
                new Event
                {
                    Id = "evt_902",
                    Type = "payment_intent.succeeded",
                    Data = new EventData
                    {
                        Object = new PaymentIntent
                        {
                            Id = "pi_900",
                            Status = "succeeded"
                        }
                    }
                }
            },
            HasMore = false
        };

        IStripeEventClient eventClient = new FakeStripeEventClient(events);
        StripeEventReconciler reconciler = new StripeEventReconciler(eventClient, processor);

        await reconciler.ReconcileAsync(new ReconciliationRequest
        {
            Limit = 1,
            CreatedAfter = DateTimeOffset.UtcNow.AddDays(-1)
        });

        ReconciliationResult second = await reconciler.ReconcileAsync(new ReconciliationRequest
        {
            Limit = 1,
            CreatedAfter = DateTimeOffset.UtcNow.AddDays(-1)
        });

        Assert.Equal(1, second.Total);
        Assert.Equal(0, second.Processed);
        Assert.Equal(1, second.Duplicates);
    }

    [Fact]
    public async Task ReconcileAsync_EmitsRunActivityWithTotals()
    {
        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore eventStore = new InMemoryWebhookEventStore();
        IPaymentRecordStore paymentStore = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptionStore = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refundStore = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new FakeStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, eventStore, paymentStore, subscriptionStore, refundStore, objectLookup, options);

        PaymentRecord payment = new PaymentRecord("user_obs_4", "pay_obs_4", PaymentStatus.Pending, "pi_obs_4", null);
        await paymentStore.SaveAsync(payment);

        StripeList<Event> events = new StripeList<Event>
        {
            Data = new List<Event>
            {
                new Event
                {
                    Id = "evt_obs_4",
                    Type = "payment_intent.succeeded",
                    Data = new EventData
                    {
                        Object = new PaymentIntent
                        {
                            Id = "pi_obs_4",
                            Status = "succeeded"
                        }
                    }
                }
            },
            HasMore = false
        };

        IStripeEventClient eventClient = new FakeStripeEventClient(events);
        StripeEventReconciler reconciler = new StripeEventReconciler(eventClient, processor);

        Activity? captured = null;
        using ActivityListener listener = CreateListener(activity =>
        {
            if (activity.OperationName == "stripekit.reconcile.run")
            {
                captured = activity;
            }
        });

        await reconciler.ReconcileAsync(new ReconciliationRequest
        {
            Limit = 1,
            CreatedAfter = DateTimeOffset.UtcNow.AddDays(-1)
        });

        Assert.NotNull(captured);
        Assert.Equal("1", GetTag(captured!, "total"));
        Assert.Equal("1", GetTag(captured!, "processed"));
        Assert.Equal("0", GetTag(captured!, "duplicates"));
        Assert.Equal("0", GetTag(captured!, "failed"));
        Assert.Equal("evt_obs_4", GetTag(captured!, "last_event_id"));
    }

    private sealed class FakeStripeEventClient : IStripeEventClient
    {
        private readonly StripeList<Event> _events;

        public FakeStripeEventClient(StripeList<Event> events)
        {
            _events = events;
        }

        public Task<StripeList<Event>> ListAsync(EventListOptions options, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_events);
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
