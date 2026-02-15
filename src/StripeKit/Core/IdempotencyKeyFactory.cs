using System;
using System.Security.Cryptography;
using System.Text;

namespace StripeKit;

public static class IdempotencyKeyFactory
{
    private const int MaxLength = 255;

    public static string Create(string scope, string businessId)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("Scope is required.", nameof(scope));
        }

        if (string.IsNullOrWhiteSpace(businessId))
        {
            throw new ArgumentException("Business ID is required.", nameof(businessId));
        }

        string rawKey = scope + ":" + businessId;
        if (rawKey.Length <= MaxLength)
        {
            return rawKey;
        }

        string hash = ComputeSha256Hex(businessId);
        string trimmedScope = TrimScope(scope, hash.Length);

        return trimmedScope + ":" + hash;
    }

    private static string TrimScope(string scope, int hashLength)
    {
        int maxScopeLength = MaxLength - hashLength - 1;
        if (scope.Length <= maxScopeLength)
        {
            return scope;
        }

        return scope.Substring(0, maxScopeLength);
    }

    private static string ComputeSha256Hex(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        byte[] hashBytes = SHA256.HashData(bytes);
        string hex = Convert.ToHexString(hashBytes);

        return hex.ToLowerInvariant();
    }
}
