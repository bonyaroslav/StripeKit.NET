using System;
using System.Threading;
using System.Threading.Tasks;

namespace StripeKit;

public sealed class CheckoutPaymentSessionRequest
{
    public string UserId { get; init; } = string.Empty;
    public string BusinessPaymentId { get; init; } = string.Empty;
    public long Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string ItemName { get; init; } = string.Empty;
    public string SuccessUrl { get; init; } = string.Empty;
    public string CancelUrl { get; init; } = string.Empty;
    public bool AllowPromotionCodes { get; init; }
    public StripeDiscount? Discount { get; init; }
    public string? CustomerId { get; init; }
    public string? IdempotencyKey { get; init; }
}

public sealed class CheckoutSubscriptionSessionRequest
{
    public string UserId { get; init; } = string.Empty;
    public string BusinessSubscriptionId { get; init; } = string.Empty;
    public string PriceId { get; init; } = string.Empty;
    public string SuccessUrl { get; init; } = string.Empty;
    public string CancelUrl { get; init; } = string.Empty;
    public bool AllowPromotionCodes { get; init; }
    public StripeDiscount? Discount { get; init; }
    public string? CustomerId { get; init; }
    public string? IdempotencyKey { get; init; }
}

public sealed class StripeDiscount
{
    public string? CouponId { get; init; }
    public string? PromotionCodeId { get; init; }

    public void Validate()
    {
        bool hasCoupon = !string.IsNullOrWhiteSpace(CouponId);
        bool hasPromotionCode = !string.IsNullOrWhiteSpace(PromotionCodeId);

        if (hasCoupon == hasPromotionCode)
        {
            throw new ArgumentException("Exactly one of CouponId or PromotionCodeId must be provided.", nameof(StripeDiscount));
        }
    }
}

public enum PromotionOutcome
{
    Applied,
    Invalid,
    Expired,
    NotApplicable
}

public sealed class PromotionValidationResult
{
    public PromotionValidationResult(PromotionOutcome outcome, string? message)
    {
        Outcome = outcome;
        Message = message;
    }

    public PromotionOutcome Outcome { get; }
    public string? Message { get; }

    public static PromotionValidationResult Applied(string? message = null)
    {
        return new PromotionValidationResult(PromotionOutcome.Applied, message);
    }

    public static PromotionValidationResult Invalid(string? message = null)
    {
        return new PromotionValidationResult(PromotionOutcome.Invalid, message);
    }

    public static PromotionValidationResult Expired(string? message = null)
    {
        return new PromotionValidationResult(PromotionOutcome.Expired, message);
    }

    public static PromotionValidationResult NotApplicable(string? message = null)
    {
        return new PromotionValidationResult(PromotionOutcome.NotApplicable, message);
    }
}

public sealed class PromotionContext
{
    public PromotionContext(string userId, StripeDiscount discount)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User ID is required.", nameof(userId));
        }

        Discount = discount ?? throw new ArgumentNullException(nameof(discount));
        UserId = userId;
    }

    public string UserId { get; }
    public StripeDiscount Discount { get; }
}

public interface IPromotionEligibilityPolicy
{
    Task<PromotionValidationResult> EvaluateAsync(PromotionContext context, CancellationToken cancellationToken);
}

public sealed class AllowAllPromotionEligibilityPolicy : IPromotionEligibilityPolicy
{
    public Task<PromotionValidationResult> EvaluateAsync(PromotionContext context, CancellationToken cancellationToken)
    {
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        return Task.FromResult(PromotionValidationResult.Applied());
    }
}

public sealed class StripeCheckoutSession
{
    public StripeCheckoutSession(
        string id,
        string? url,
        string? customerId,
        string? paymentIntentId,
        string? subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Session ID is required.", nameof(id));
        }

        Id = id;
        Url = url;
        CustomerId = customerId;
        PaymentIntentId = paymentIntentId;
        SubscriptionId = subscriptionId;
    }

    public string Id { get; }
    public string? Url { get; }
    public string? CustomerId { get; }
    public string? PaymentIntentId { get; }
    public string? SubscriptionId { get; }
}

public sealed class CheckoutSessionResult
{
    public CheckoutSessionResult(StripeCheckoutSession? session, PromotionValidationResult promotionResult)
    {
        PromotionResult = promotionResult ?? throw new ArgumentNullException(nameof(promotionResult));
        Session = session;
    }

    public StripeCheckoutSession? Session { get; }
    public PromotionValidationResult PromotionResult { get; }
}
