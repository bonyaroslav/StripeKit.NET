using System;
using System.Collections.Generic;

namespace StripeKit;

public static class StripeMetadataMapper
{
    public static IReadOnlyDictionary<string, string> CreateForUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User ID is required.", nameof(userId));
        }

        Dictionary<string, string> metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["user_id"] = userId
        };

        return metadata;
    }

    public static bool TryGetUserId(IReadOnlyDictionary<string, string>? metadata, out string userId)
    {
        userId = string.Empty;
        if (metadata == null)
        {
            return false;
        }

        if (!metadata.TryGetValue("user_id", out string? value))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        userId = value;
        return true;
    }
}
