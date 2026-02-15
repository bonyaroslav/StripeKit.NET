using System;
using System.Threading;
using System.Threading.Tasks;
using Stripe;
using Stripe.Checkout;

namespace StripeKit;

public interface ICheckoutSessionClient
{
    Task<StripeCheckoutSession> CreateAsync(SessionCreateOptions options, string idempotencyKey, CancellationToken cancellationToken);
}

public sealed class StripeCheckoutSessionClient : ICheckoutSessionClient
{
    private readonly SessionService _sessionService;

    public StripeCheckoutSessionClient(SessionService sessionService)
    {
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
    }

    public async Task<StripeCheckoutSession> CreateAsync(SessionCreateOptions options, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey));
        }

        RequestOptions requestOptions = new RequestOptions
        {
            IdempotencyKey = idempotencyKey
        };

        Session session = await _sessionService.CreateAsync(options, requestOptions, cancellationToken).ConfigureAwait(false);

        return new StripeCheckoutSession(
            session.Id,
            session.Url,
            session.CustomerId,
            session.PaymentIntentId,
            session.SubscriptionId);
    }
}
