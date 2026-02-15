using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace StripeKit;

public enum RefundStatus
{
    Pending,
    Succeeded,
    Failed
}

public sealed class RefundRecord
{
    public RefundRecord(
        string userId,
        string businessRefundId,
        string businessPaymentId,
        RefundStatus status,
        string? paymentIntentId,
        string? refundId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User ID is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(businessRefundId))
        {
            throw new ArgumentException("Business refund ID is required.", nameof(businessRefundId));
        }

        if (string.IsNullOrWhiteSpace(businessPaymentId))
        {
            throw new ArgumentException("Business payment ID is required.", nameof(businessPaymentId));
        }

        UserId = userId;
        BusinessRefundId = businessRefundId;
        BusinessPaymentId = businessPaymentId;
        Status = status;
        PaymentIntentId = NormalizeOptionalId(paymentIntentId);
        RefundId = NormalizeOptionalId(refundId);
    }

    public string UserId { get; }
    public string BusinessRefundId { get; }
    public string BusinessPaymentId { get; }
    public RefundStatus Status { get; }
    public string? PaymentIntentId { get; }
    public string? RefundId { get; }

    private static string? NormalizeOptionalId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value;
    }
}

public interface IRefundRecordStore
{
    Task SaveAsync(RefundRecord record);
    Task<RefundRecord?> GetByBusinessIdAsync(string businessRefundId);
    Task<RefundRecord?> GetByRefundIdAsync(string refundId);
}

public sealed class InMemoryRefundRecordStore : IRefundRecordStore
{
    private readonly ConcurrentDictionary<string, RefundRecord> _recordsByBusinessId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _businessIdByRefundId = new(StringComparer.Ordinal);

    public Task SaveAsync(RefundRecord record)
    {
        if (record == null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        _recordsByBusinessId.TryGetValue(record.BusinessRefundId, out RefundRecord? existing);
        _recordsByBusinessId[record.BusinessRefundId] = record;

        UpdateRefundMapping(existing?.RefundId, record.RefundId, record.BusinessRefundId);

        return Task.CompletedTask;
    }

    public Task<RefundRecord?> GetByBusinessIdAsync(string businessRefundId)
    {
        if (string.IsNullOrWhiteSpace(businessRefundId))
        {
            throw new ArgumentException("Business refund ID is required.", nameof(businessRefundId));
        }

        _recordsByBusinessId.TryGetValue(businessRefundId, out RefundRecord? record);

        return Task.FromResult(record);
    }

    public Task<RefundRecord?> GetByRefundIdAsync(string refundId)
    {
        if (string.IsNullOrWhiteSpace(refundId))
        {
            throw new ArgumentException("Refund ID is required.", nameof(refundId));
        }

        if (_businessIdByRefundId.TryGetValue(refundId, out string? businessRefundId))
        {
            return GetByBusinessIdAsync(businessRefundId);
        }

        return Task.FromResult<RefundRecord?>(null);
    }

    private void UpdateRefundMapping(string? previousRefundId, string? newRefundId, string businessRefundId)
    {
        if (!string.IsNullOrWhiteSpace(previousRefundId) &&
            !string.Equals(previousRefundId, newRefundId, StringComparison.Ordinal))
        {
            _businessIdByRefundId.TryRemove(previousRefundId, out _);
        }

        if (!string.IsNullOrWhiteSpace(newRefundId))
        {
            _businessIdByRefundId[newRefundId] = businessRefundId;
        }
    }
}