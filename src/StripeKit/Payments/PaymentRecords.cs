using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace StripeKit;

public enum PaymentStatus
{
    Pending,
    Succeeded,
    Failed,
    Canceled
}

public sealed class PaymentRecord
{
    public PaymentRecord(
        string userId,
        string businessPaymentId,
        PaymentStatus status,
        string? paymentIntentId,
        string? chargeId,
        PromotionOutcome? promotionOutcome = null,
        string? promotionCouponId = null,
        string? promotionCodeId = null,
        DateTimeOffset? lastStripeEventCreated = null)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User ID is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(businessPaymentId))
        {
            throw new ArgumentException("Business payment ID is required.", nameof(businessPaymentId));
        }

        UserId = userId;
        BusinessPaymentId = businessPaymentId;
        Status = status;
        PaymentIntentId = NormalizeOptionalId(paymentIntentId);
        ChargeId = NormalizeOptionalId(chargeId);
        PromotionOutcome = promotionOutcome;
        PromotionCouponId = NormalizeOptionalId(promotionCouponId);
        PromotionCodeId = NormalizeOptionalId(promotionCodeId);
        LastStripeEventCreated = lastStripeEventCreated;
    }

    public string UserId { get; }
    public string BusinessPaymentId { get; }
    public PaymentStatus Status { get; }
    public string? PaymentIntentId { get; }
    public string? ChargeId { get; }
    public PromotionOutcome? PromotionOutcome { get; }
    public string? PromotionCouponId { get; }
    public string? PromotionCodeId { get; }
    public DateTimeOffset? LastStripeEventCreated { get; }

    private static string? NormalizeOptionalId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value;
    }
}

public interface IPaymentRecordStore
{
    Task SaveAsync(PaymentRecord record);
    Task<PaymentRecord?> GetByBusinessIdAsync(string businessPaymentId);
    Task<PaymentRecord?> GetByPaymentIntentIdAsync(string paymentIntentId);
}

public sealed class InMemoryPaymentRecordStore : IPaymentRecordStore
{
    private readonly ConcurrentDictionary<string, PaymentRecord> _recordsByBusinessId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _businessIdByPaymentIntentId = new(StringComparer.Ordinal);

    public Task SaveAsync(PaymentRecord record)
    {
        if (record == null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        _recordsByBusinessId.TryGetValue(record.BusinessPaymentId, out PaymentRecord? existing);
        _recordsByBusinessId[record.BusinessPaymentId] = record;

        UpdatePaymentIntentMapping(existing?.PaymentIntentId, record.PaymentIntentId, record.BusinessPaymentId);

        return Task.CompletedTask;
    }

    public Task<PaymentRecord?> GetByBusinessIdAsync(string businessPaymentId)
    {
        if (string.IsNullOrWhiteSpace(businessPaymentId))
        {
            throw new ArgumentException("Business payment ID is required.", nameof(businessPaymentId));
        }

        _recordsByBusinessId.TryGetValue(businessPaymentId, out PaymentRecord? record);

        return Task.FromResult(record);
    }

    public Task<PaymentRecord?> GetByPaymentIntentIdAsync(string paymentIntentId)
    {
        if (string.IsNullOrWhiteSpace(paymentIntentId))
        {
            throw new ArgumentException("Payment intent ID is required.", nameof(paymentIntentId));
        }

        if (_businessIdByPaymentIntentId.TryGetValue(paymentIntentId, out string? businessPaymentId))
        {
            return GetByBusinessIdAsync(businessPaymentId);
        }

        return Task.FromResult<PaymentRecord?>(null);
    }

    private void UpdatePaymentIntentMapping(string? previousPaymentIntentId, string? newPaymentIntentId, string businessPaymentId)
    {
        if (!string.IsNullOrWhiteSpace(previousPaymentIntentId) &&
            !string.Equals(previousPaymentIntentId, newPaymentIntentId, StringComparison.Ordinal))
        {
            _businessIdByPaymentIntentId.TryRemove(previousPaymentIntentId, out _);
        }

        if (!string.IsNullOrWhiteSpace(newPaymentIntentId))
        {
            _businessIdByPaymentIntentId[newPaymentIntentId] = businessPaymentId;
        }
    }
}
