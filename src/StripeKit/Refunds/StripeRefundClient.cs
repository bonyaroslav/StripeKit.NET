using System;
using System.Threading;
using System.Threading.Tasks;
using Stripe;

namespace StripeKit;

public interface IRefundClient
{
    Task<StripeRefund> CreateAsync(RefundCreateOptions options, string idempotencyKey, CancellationToken cancellationToken);
}

public sealed class StripeRefundClient : IRefundClient
{
    private readonly RefundService _refundService;

    public StripeRefundClient(RefundService refundService)
    {
        _refundService = refundService ?? throw new ArgumentNullException(nameof(refundService));
    }

    public async Task<StripeRefund> CreateAsync(
        RefundCreateOptions options,
        string idempotencyKey,
        CancellationToken cancellationToken)
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

        Refund refund = await _refundService.CreateAsync(options, requestOptions, cancellationToken).ConfigureAwait(false);

        return new StripeRefund(refund.Id, refund.PaymentIntentId, MapStatus(refund.Status));
    }

    private static RefundStatus MapStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return RefundStatus.Pending;
        }

        if (string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
        {
            return RefundStatus.Succeeded;
        }

        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return RefundStatus.Failed;
        }

        return RefundStatus.Pending;
    }
}