using System;

namespace StripeKit;

public sealed class RefundRequest
{
    public string UserId { get; init; } = string.Empty;
    public string BusinessRefundId { get; init; } = string.Empty;
    public string BusinessPaymentId { get; init; } = string.Empty;
    public string? IdempotencyKey { get; init; }
}

public sealed class StripeRefund
{
    public StripeRefund(string id, string? paymentIntentId, RefundStatus status)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Refund ID is required.", nameof(id));
        }

        Id = id;
        PaymentIntentId = paymentIntentId;
        Status = status;
    }

    public string Id { get; }
    public string? PaymentIntentId { get; }
    public RefundStatus Status { get; }
}

public sealed class RefundResult
{
    public RefundResult(StripeRefund refund)
    {
        Refund = refund ?? throw new ArgumentNullException(nameof(refund));
    }

    public StripeRefund Refund { get; }
}