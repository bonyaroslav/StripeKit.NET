using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace StripeKit;

public enum SubscriptionStatus
{
    Incomplete,
    Active,
    PastDue,
    Canceled
}

public sealed class SubscriptionRecord
{
    public SubscriptionRecord(
        string userId,
        string businessSubscriptionId,
        SubscriptionStatus status,
        string? customerId,
        string? subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User ID is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(businessSubscriptionId))
        {
            throw new ArgumentException("Business subscription ID is required.", nameof(businessSubscriptionId));
        }

        UserId = userId;
        BusinessSubscriptionId = businessSubscriptionId;
        Status = status;
        CustomerId = NormalizeOptionalId(customerId);
        SubscriptionId = NormalizeOptionalId(subscriptionId);
    }

    public string UserId { get; }
    public string BusinessSubscriptionId { get; }
    public SubscriptionStatus Status { get; }
    public string? CustomerId { get; }
    public string? SubscriptionId { get; }

    private static string? NormalizeOptionalId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value;
    }
}

public interface ISubscriptionRecordStore
{
    Task SaveAsync(SubscriptionRecord record);
    Task<SubscriptionRecord?> GetByBusinessIdAsync(string businessSubscriptionId);
    Task<SubscriptionRecord?> GetBySubscriptionIdAsync(string subscriptionId);
}

public sealed class InMemorySubscriptionRecordStore : ISubscriptionRecordStore
{
    private readonly ConcurrentDictionary<string, SubscriptionRecord> _recordsByBusinessId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _businessIdBySubscriptionId = new(StringComparer.Ordinal);

    public Task SaveAsync(SubscriptionRecord record)
    {
        if (record == null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        _recordsByBusinessId.TryGetValue(record.BusinessSubscriptionId, out SubscriptionRecord? existing);
        _recordsByBusinessId[record.BusinessSubscriptionId] = record;

        UpdateSubscriptionMapping(existing?.SubscriptionId, record.SubscriptionId, record.BusinessSubscriptionId);

        return Task.CompletedTask;
    }

    public Task<SubscriptionRecord?> GetByBusinessIdAsync(string businessSubscriptionId)
    {
        if (string.IsNullOrWhiteSpace(businessSubscriptionId))
        {
            throw new ArgumentException("Business subscription ID is required.", nameof(businessSubscriptionId));
        }

        _recordsByBusinessId.TryGetValue(businessSubscriptionId, out SubscriptionRecord? record);

        return Task.FromResult(record);
    }

    public Task<SubscriptionRecord?> GetBySubscriptionIdAsync(string subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            throw new ArgumentException("Subscription ID is required.", nameof(subscriptionId));
        }

        if (_businessIdBySubscriptionId.TryGetValue(subscriptionId, out string? businessSubscriptionId))
        {
            return GetByBusinessIdAsync(businessSubscriptionId);
        }

        return Task.FromResult<SubscriptionRecord?>(null);
    }

    private void UpdateSubscriptionMapping(string? previousSubscriptionId, string? newSubscriptionId, string businessSubscriptionId)
    {
        if (!string.IsNullOrWhiteSpace(previousSubscriptionId) &&
            !string.Equals(previousSubscriptionId, newSubscriptionId, StringComparison.Ordinal))
        {
            _businessIdBySubscriptionId.TryRemove(previousSubscriptionId, out _);
        }

        if (!string.IsNullOrWhiteSpace(newSubscriptionId))
        {
            _businessIdBySubscriptionId[newSubscriptionId] = businessSubscriptionId;
        }
    }
}
