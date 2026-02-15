using System;
using System.Threading.Tasks;
using Stripe;

namespace StripeKit;

public interface IStripeObjectLookup
{
    Task<string?> GetPaymentIntentIdAsync(string objectId);
    Task<string?> GetSubscriptionIdAsync(string objectId);
}

public sealed class StripeObjectLookup : IStripeObjectLookup
{
    private readonly EventService _eventService;
    private readonly PaymentIntentService _paymentIntentService;
    private readonly InvoiceService _invoiceService;
    private readonly SubscriptionService _subscriptionService;

    public StripeObjectLookup(
        EventService eventService,
        PaymentIntentService paymentIntentService,
        InvoiceService invoiceService,
        SubscriptionService subscriptionService)
    {
        _eventService = eventService ?? throw new ArgumentNullException(nameof(eventService));
        _paymentIntentService = paymentIntentService ?? throw new ArgumentNullException(nameof(paymentIntentService));
        _invoiceService = invoiceService ?? throw new ArgumentNullException(nameof(invoiceService));
        _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));
    }

    public async Task<string?> GetPaymentIntentIdAsync(string objectId)
    {
        if (string.IsNullOrWhiteSpace(objectId))
        {
            throw new ArgumentException("Object ID is required.", nameof(objectId));
        }

        if (objectId.StartsWith("pi_", StringComparison.OrdinalIgnoreCase))
        {
            return objectId;
        }

        if (objectId.StartsWith("in_", StringComparison.OrdinalIgnoreCase))
        {
            Invoice invoice = await _invoiceService.GetAsync(objectId).ConfigureAwait(false);
            return invoice.PaymentIntentId;
        }

        if (objectId.StartsWith("evt_", StringComparison.OrdinalIgnoreCase))
        {
            Event stripeEvent = await _eventService.GetAsync(objectId).ConfigureAwait(false);
            if (stripeEvent.Data?.Object == null)
            {
                return null;
            }

            if (stripeEvent.Data.Object is Invoice invoice)
            {
                return invoice.PaymentIntentId;
            }

            if (stripeEvent.Data.Object is PaymentIntent paymentIntent)
            {
                return paymentIntent.Id;
            }
        }

        return null;
    }

    public async Task<string?> GetSubscriptionIdAsync(string objectId)
    {
        if (string.IsNullOrWhiteSpace(objectId))
        {
            throw new ArgumentException("Object ID is required.", nameof(objectId));
        }

        if (objectId.StartsWith("sub_", StringComparison.OrdinalIgnoreCase))
        {
            return objectId;
        }

        if (objectId.StartsWith("in_", StringComparison.OrdinalIgnoreCase))
        {
            Invoice invoice = await _invoiceService.GetAsync(objectId).ConfigureAwait(false);
            return invoice.SubscriptionId;
        }

        if (objectId.StartsWith("evt_", StringComparison.OrdinalIgnoreCase))
        {
            Event stripeEvent = await _eventService.GetAsync(objectId).ConfigureAwait(false);
            if (stripeEvent.Data?.Object == null)
            {
                return null;
            }

            if (stripeEvent.Data.Object is Invoice invoice)
            {
                return invoice.SubscriptionId;
            }

            if (stripeEvent.Data.Object is Subscription subscription)
            {
                return subscription.Id;
            }
        }

        return null;
    }
}
