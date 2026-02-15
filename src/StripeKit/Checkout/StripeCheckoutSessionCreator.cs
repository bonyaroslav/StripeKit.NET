using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Stripe.Checkout;

namespace StripeKit;

public sealed class StripeCheckoutSessionCreator
{
    private readonly ICheckoutSessionClient _client;
    private readonly IPaymentRecordStore _paymentRecords;
    private readonly ISubscriptionRecordStore _subscriptionRecords;
    private readonly StripeKitOptions _options;
    private readonly IPromotionEligibilityPolicy _promotionPolicy;
    private readonly ICustomerMappingStore? _customerMappingStore;

    public StripeCheckoutSessionCreator(
        ICheckoutSessionClient client,
        IPaymentRecordStore paymentRecords,
        ISubscriptionRecordStore subscriptionRecords,
        StripeKitOptions options,
        IPromotionEligibilityPolicy promotionPolicy,
        ICustomerMappingStore? customerMappingStore)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _paymentRecords = paymentRecords ?? throw new ArgumentNullException(nameof(paymentRecords));
        _subscriptionRecords = subscriptionRecords ?? throw new ArgumentNullException(nameof(subscriptionRecords));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _promotionPolicy = promotionPolicy ?? throw new ArgumentNullException(nameof(promotionPolicy));
        _customerMappingStore = customerMappingStore;
    }

    public async Task<CheckoutSessionResult> CreatePaymentSessionAsync(
        CheckoutPaymentSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EnablePayments)
        {
            throw new InvalidOperationException("Payments module is disabled.");
        }

        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ValidatePaymentRequest(request);

        using Activity? activity = StripeKitDiagnostics.ActivitySource.StartActivity("stripekit.checkout.create_payment");
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.UserId, request.UserId);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.BusinessPaymentId, request.BusinessPaymentId);
        StripeKitDiagnostics.SetTag(activity, "currency", request.Currency);

        PromotionValidationResult promotionResult = await EvaluatePromotionAsync(request.UserId, request.Discount, cancellationToken)
            .ConfigureAwait(false);

        if (request.Discount != null && promotionResult.Outcome != PromotionOutcome.Applied)
        {
            StripeKitDiagnostics.SetTag(activity, "promotion_outcome", promotionResult.Outcome.ToString());
            return new CheckoutSessionResult(null, promotionResult);
        }

        SessionCreateOptions options = BuildPaymentOptions(request);
        string idempotencyKey = ResolveIdempotencyKey(request.IdempotencyKey, "checkout_payment", request.BusinessPaymentId);

        StripeCheckoutSession session = await _client.CreateAsync(options, idempotencyKey, cancellationToken).ConfigureAwait(false);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.CheckoutSessionId, session.Id);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.PaymentIntentId, session.PaymentIntentId);

        PaymentRecord record = new PaymentRecord(
            request.UserId,
            request.BusinessPaymentId,
            PaymentStatus.Pending,
            session.PaymentIntentId,
            null);

        await _paymentRecords.SaveAsync(record).ConfigureAwait(false);
        await SaveCustomerMappingAsync(request.UserId, session.CustomerId).ConfigureAwait(false);

        return new CheckoutSessionResult(session, promotionResult);
    }

    public async Task<CheckoutSessionResult> CreateSubscriptionSessionAsync(
        CheckoutSubscriptionSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EnableBilling)
        {
            throw new InvalidOperationException("Billing module is disabled.");
        }

        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ValidateSubscriptionRequest(request);

        using Activity? activity = StripeKitDiagnostics.ActivitySource.StartActivity("stripekit.checkout.create_subscription");
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.UserId, request.UserId);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.BusinessSubscriptionId, request.BusinessSubscriptionId);
        StripeKitDiagnostics.SetTag(activity, "price_id", request.PriceId);

        PromotionValidationResult promotionResult = await EvaluatePromotionAsync(request.UserId, request.Discount, cancellationToken)
            .ConfigureAwait(false);

        if (request.Discount != null && promotionResult.Outcome != PromotionOutcome.Applied)
        {
            StripeKitDiagnostics.SetTag(activity, "promotion_outcome", promotionResult.Outcome.ToString());
            return new CheckoutSessionResult(null, promotionResult);
        }

        SessionCreateOptions options = BuildSubscriptionOptions(request);
        string idempotencyKey = ResolveIdempotencyKey(request.IdempotencyKey, "checkout_subscription", request.BusinessSubscriptionId);

        StripeCheckoutSession session = await _client.CreateAsync(options, idempotencyKey, cancellationToken).ConfigureAwait(false);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.CheckoutSessionId, session.Id);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.SubscriptionId, session.SubscriptionId);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.CustomerId, session.CustomerId);

        SubscriptionRecord record = new SubscriptionRecord(
            request.UserId,
            request.BusinessSubscriptionId,
            SubscriptionStatus.Incomplete,
            session.CustomerId ?? request.CustomerId,
            session.SubscriptionId);

        await _subscriptionRecords.SaveAsync(record).ConfigureAwait(false);
        await SaveCustomerMappingAsync(request.UserId, session.CustomerId ?? request.CustomerId).ConfigureAwait(false);

        return new CheckoutSessionResult(session, promotionResult);
    }

    private async Task SaveCustomerMappingAsync(string userId, string? customerId)
    {
        if (_customerMappingStore == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(customerId))
        {
            return;
        }

        await _customerMappingStore.SaveMappingAsync(userId, customerId).ConfigureAwait(false);
    }

    private async Task<PromotionValidationResult> EvaluatePromotionAsync(
        string userId,
        StripeDiscount? discount,
        CancellationToken cancellationToken)
    {
        if (!_options.EnablePromotions || discount == null)
        {
            return PromotionValidationResult.NotApplicable();
        }

        discount.Validate();
        PromotionContext context = new PromotionContext(userId, discount);

        return await _promotionPolicy.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidatePaymentRequest(CheckoutPaymentSessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            throw new ArgumentException("User ID is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.BusinessPaymentId))
        {
            throw new ArgumentException("Business payment ID is required.", nameof(request));
        }

        if (request.Amount <= 0)
        {
            throw new ArgumentException("Amount must be greater than zero.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Currency))
        {
            throw new ArgumentException("Currency is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ItemName))
        {
            throw new ArgumentException("Item name is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.SuccessUrl))
        {
            throw new ArgumentException("Success URL is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.CancelUrl))
        {
            throw new ArgumentException("Cancel URL is required.", nameof(request));
        }
    }

    private static void ValidateSubscriptionRequest(CheckoutSubscriptionSessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            throw new ArgumentException("User ID is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.BusinessSubscriptionId))
        {
            throw new ArgumentException("Business subscription ID is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.PriceId))
        {
            throw new ArgumentException("Price ID is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.SuccessUrl))
        {
            throw new ArgumentException("Success URL is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.CancelUrl))
        {
            throw new ArgumentException("Cancel URL is required.", nameof(request));
        }
    }

    private SessionCreateOptions BuildPaymentOptions(CheckoutPaymentSessionRequest request)
    {
        Dictionary<string, string> metadata = new Dictionary<string, string>(StripeMetadataMapper.CreateForUser(request.UserId));
        SessionCreateOptions options = new SessionCreateOptions
        {
            Mode = "payment",
            SuccessUrl = request.SuccessUrl,
            CancelUrl = request.CancelUrl,
            Customer = request.CustomerId,
            ClientReferenceId = request.BusinessPaymentId,
            AllowPromotionCodes = _options.EnablePromotions && request.AllowPromotionCodes,
            Metadata = metadata,
            LineItems = new List<SessionLineItemOptions>
            {
                new SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = request.Currency.ToLowerInvariant(),
                        UnitAmount = request.Amount,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = request.ItemName
                        }
                    }
                }
            },
            PaymentIntentData = new SessionPaymentIntentDataOptions
            {
                Metadata = metadata
            }
        };

        StripeDiscount? discount = request.Discount;
        if (_options.EnablePromotions && discount != null)
        {
            options.Discounts = new List<SessionDiscountOptions>
            {
                BuildDiscountOption(discount)
            };
        }

        return options;
    }

    private SessionCreateOptions BuildSubscriptionOptions(CheckoutSubscriptionSessionRequest request)
    {
        Dictionary<string, string> metadata = new Dictionary<string, string>(StripeMetadataMapper.CreateForUser(request.UserId));
        SessionCreateOptions options = new SessionCreateOptions
        {
            Mode = "subscription",
            SuccessUrl = request.SuccessUrl,
            CancelUrl = request.CancelUrl,
            Customer = request.CustomerId,
            ClientReferenceId = request.BusinessSubscriptionId,
            AllowPromotionCodes = _options.EnablePromotions && request.AllowPromotionCodes,
            Metadata = metadata,
            LineItems = new List<SessionLineItemOptions>
            {
                new SessionLineItemOptions
                {
                    Quantity = 1,
                    Price = request.PriceId
                }
            },
            SubscriptionData = new SessionSubscriptionDataOptions
            {
                Metadata = metadata
            }
        };

        StripeDiscount? discount = request.Discount;
        if (_options.EnablePromotions && discount != null)
        {
            options.Discounts = new List<SessionDiscountOptions>
            {
                BuildDiscountOption(discount)
            };
        }

        return options;
    }

    private static SessionDiscountOptions BuildDiscountOption(StripeDiscount discount)
    {
        if (!string.IsNullOrWhiteSpace(discount.CouponId))
        {
            return new SessionDiscountOptions
            {
                Coupon = discount.CouponId
            };
        }

        return new SessionDiscountOptions
        {
            PromotionCode = discount.PromotionCodeId
        };
    }

    private static string ResolveIdempotencyKey(string? providedKey, string scope, string businessId)
    {
        if (!string.IsNullOrWhiteSpace(providedKey))
        {
            return providedKey;
        }

        return IdempotencyKeyFactory.Create(scope, businessId);
    }
}
