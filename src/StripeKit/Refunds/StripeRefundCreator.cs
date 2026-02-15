using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Stripe;

namespace StripeKit;

public sealed class StripeRefundCreator
{
    private readonly IRefundClient _client;
    private readonly IPaymentRecordStore _paymentRecords;
    private readonly IRefundRecordStore _refundRecords;
    private readonly StripeKitOptions _options;

    public StripeRefundCreator(
        IRefundClient client,
        IPaymentRecordStore paymentRecords,
        IRefundRecordStore refundRecords,
        StripeKitOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _paymentRecords = paymentRecords ?? throw new ArgumentNullException(nameof(paymentRecords));
        _refundRecords = refundRecords ?? throw new ArgumentNullException(nameof(refundRecords));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<RefundResult> CreateRefundAsync(
        RefundRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EnableRefunds)
        {
            throw new InvalidOperationException("Refunds module is disabled.");
        }

        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ValidateRequest(request);

        using Activity? activity = StripeKitDiagnostics.ActivitySource.StartActivity("stripekit.refund.create");
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.UserId, request.UserId);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.BusinessRefundId, request.BusinessRefundId);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.BusinessPaymentId, request.BusinessPaymentId);

        PaymentRecord? paymentRecord = await _paymentRecords.GetByBusinessIdAsync(request.BusinessPaymentId).ConfigureAwait(false);
        if (paymentRecord == null)
        {
            throw new InvalidOperationException("Payment record not found.");
        }

        if (!string.Equals(paymentRecord.UserId, request.UserId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refund user does not match payment record.");
        }

        if (paymentRecord.Status != PaymentStatus.Succeeded)
        {
            throw new InvalidOperationException("Only succeeded payments can be refunded.");
        }

        if (string.IsNullOrWhiteSpace(paymentRecord.PaymentIntentId))
        {
            throw new InvalidOperationException("Payment intent ID is required for refund.");
        }

        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.PaymentIntentId, paymentRecord.PaymentIntentId);

        RefundCreateOptions options = BuildRefundOptions(request, paymentRecord.PaymentIntentId);
        string idempotencyKey = ResolveIdempotencyKey(request.IdempotencyKey, request.BusinessRefundId);

        StripeRefund refund = await _client.CreateAsync(options, idempotencyKey, cancellationToken).ConfigureAwait(false);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.RefundId, refund.Id);

        RefundRecord record = new RefundRecord(
            request.UserId,
            request.BusinessRefundId,
            request.BusinessPaymentId,
            refund.Status,
            paymentRecord.PaymentIntentId,
            refund.Id);

        await _refundRecords.SaveAsync(record).ConfigureAwait(false);
        StripeKitDiagnostics.EmitLog(
            "refund.created",
            (StripeKitDiagnosticTags.UserId, request.UserId),
            (StripeKitDiagnosticTags.BusinessRefundId, request.BusinessRefundId),
            (StripeKitDiagnosticTags.BusinessPaymentId, request.BusinessPaymentId),
            (StripeKitDiagnosticTags.PaymentIntentId, paymentRecord.PaymentIntentId),
            (StripeKitDiagnosticTags.RefundId, refund.Id),
            ("refund_status", refund.Status.ToString()));

        return new RefundResult(refund);
    }

    private static void ValidateRequest(RefundRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            throw new ArgumentException("User ID is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.BusinessRefundId))
        {
            throw new ArgumentException("Business refund ID is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.BusinessPaymentId))
        {
            throw new ArgumentException("Business payment ID is required.", nameof(request));
        }
    }

    private static RefundCreateOptions BuildRefundOptions(RefundRequest request, string paymentIntentId)
    {
        Dictionary<string, string> metadata = new Dictionary<string, string>(StripeMetadataMapper.CreateForUser(request.UserId))
        {
            ["business_refund_id"] = request.BusinessRefundId,
            ["business_payment_id"] = request.BusinessPaymentId
        };

        return new RefundCreateOptions
        {
            PaymentIntent = paymentIntentId,
            Metadata = metadata
        };
    }

    private static string ResolveIdempotencyKey(string? providedKey, string businessRefundId)
    {
        if (!string.IsNullOrWhiteSpace(providedKey))
        {
            return providedKey;
        }

        return IdempotencyKeyFactory.Create("refund", businessRefundId);
    }
}
