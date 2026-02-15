using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace StripeKit;

public sealed class InMemoryCustomerMappingStore : ICustomerMappingStore
{
    private readonly ConcurrentDictionary<string, string> _customerByUserId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _userByCustomerId = new(StringComparer.Ordinal);

    public Task SaveMappingAsync(string userId, string customerId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User ID is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(customerId))
        {
            throw new ArgumentException("Customer ID is required.", nameof(customerId));
        }

        _customerByUserId[userId] = customerId;
        _userByCustomerId[customerId] = userId;

        return Task.CompletedTask;
    }

    public Task<string?> GetCustomerIdAsync(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User ID is required.", nameof(userId));
        }

        _customerByUserId.TryGetValue(userId, out string? customerId);

        return Task.FromResult(customerId);
    }

    public Task<string?> GetUserIdAsync(string customerId)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            throw new ArgumentException("Customer ID is required.", nameof(customerId));
        }

        _userByCustomerId.TryGetValue(customerId, out string? userId);

        return Task.FromResult(userId);
    }
}
