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
    private readonly IStripeCustomerResolver? _customerResolver;

    public StripeCheckoutSessionCreator(
        ICheckoutSessionClient client,
        IPaymentRecordStore paymentRecords,
        ISubscriptionRecordStore subscriptionRecords,
        StripeKitOptions options,
        IPromotionEligibilityPolicy promotionPolicy,
        ICustomerMappingStore? customerMappingStore,
        IStripeCustomerResolver? customerResolver = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _paymentRecords = paymentRecords ?? throw new ArgumentNullException(nameof(paymentRecords));
        _subscriptionRecords = subscriptionRecords ?? throw new ArgumentNullException(nameof(subscriptionRecords));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _promotionPolicy = promotionPolicy ?? throw new ArgumentNullException(nameof(promotionPolicy));
        _customerMappingStore = customerMappingStore;
        _customerResolver = customerResolver;
    }

    public Task<CheckoutSessionResult> CreatePaymentSessionAsync(
        CheckoutPaymentSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        CheckoutSessionWorkflow<CheckoutPaymentSessionRequest> workflow = new CheckoutSessionWorkflow<CheckoutPaymentSessionRequest>(
            EnsurePaymentsEnabled,
            ValidatePaymentRequest,
            "stripekit.checkout.create_payment",
            static request => request.UserId,
            static request => request.CustomerId,
            static request => request.Discount,
            static request => request.BusinessPaymentId,
            static request => request.IdempotencyKey,
            "checkout_payment",
            TagPaymentRequest,
            BuildPaymentOptions,
            TagPaymentSession,
            PersistPaymentSessionAsync,
            EmitPaymentCreatedLog);

        return CreateSessionAsync(request, workflow, cancellationToken);
    }

    public Task<CheckoutSessionResult> CreateSubscriptionSessionAsync(
        CheckoutSubscriptionSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        CheckoutSessionWorkflow<CheckoutSubscriptionSessionRequest> workflow = new CheckoutSessionWorkflow<CheckoutSubscriptionSessionRequest>(
            EnsureBillingEnabled,
            ValidateSubscriptionRequest,
            "stripekit.checkout.create_subscription",
            static request => request.UserId,
            static request => request.CustomerId,
            static request => request.Discount,
            static request => request.BusinessSubscriptionId,
            static request => request.IdempotencyKey,
            "checkout_subscription",
            TagSubscriptionRequest,
            BuildSubscriptionOptions,
            TagSubscriptionSession,
            PersistSubscriptionSessionAsync,
            EmitSubscriptionCreatedLog);

        return CreateSessionAsync(request, workflow, cancellationToken);
    }

    private void EnsurePaymentsEnabled()
    {
        if (!_options.EnablePayments)
        {
            throw new InvalidOperationException("Payments module is disabled.");
        }
    }

    private void EnsureBillingEnabled()
    {
        if (!_options.EnableBilling)
        {
            throw new InvalidOperationException("Billing module is disabled.");
        }
    }

    private async Task<CheckoutSessionResult> CreateSessionAsync<TRequest>(
        TRequest request,
        CheckoutSessionWorkflow<TRequest> workflow,
        CancellationToken cancellationToken)
        where TRequest : class
    {
        workflow.EnsureEnabled();

        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        workflow.Validate(request);

        using Activity? activity = StripeKitDiagnostics.ActivitySource.StartActivity(workflow.ActivityName);
        workflow.TagRequest(activity, request);

        StripeDiscount? discount = workflow.GetDiscount(request);
        string userId = workflow.GetUserId(request);
        PromotionValidationResult promotionResult = await EvaluatePromotionAsync(userId, discount, cancellationToken)
            .ConfigureAwait(false);

        if (discount != null && promotionResult.Outcome != PromotionOutcome.Applied)
        {
            StripeKitDiagnostics.SetTag(activity, "promotion_outcome", promotionResult.Outcome.ToString());
            return new CheckoutSessionResult(null, promotionResult);
        }

        string? customerId = await ResolveCustomerIdAsync(
            userId,
            workflow.GetProvidedCustomerId(request),
            cancellationToken).ConfigureAwait(false);
        SessionCreateOptions options = workflow.BuildOptions(request, customerId);
        string idempotencyKey = ResolveIdempotencyKey(
            workflow.GetProvidedIdempotencyKey(request),
            workflow.IdempotencyScope,
            workflow.GetBusinessId(request));

        StripeCheckoutSession session = await _client.CreateAsync(options, idempotencyKey, cancellationToken).ConfigureAwait(false);
        string? persistedCustomerId = session.CustomerId ?? customerId;

        workflow.TagSession(activity, session, persistedCustomerId);
        await workflow.PersistAsync(request, session, promotionResult, persistedCustomerId).ConfigureAwait(false);
        workflow.EmitLog(request, session, promotionResult, persistedCustomerId);

        return new CheckoutSessionResult(session, promotionResult);
    }

    private async Task PersistPaymentSessionAsync(
        CheckoutPaymentSessionRequest request,
        StripeCheckoutSession session,
        PromotionValidationResult promotionResult,
        string? customerId)
    {
        PaymentRecord record = new PaymentRecord(
            request.UserId,
            request.BusinessPaymentId,
            PaymentStatus.Pending,
            session.PaymentIntentId,
            null,
            request.Discount == null ? null : promotionResult.Outcome,
            request.Discount?.CouponId,
            request.Discount?.PromotionCodeId);

        await _paymentRecords.SaveAsync(record).ConfigureAwait(false);
        await SaveCustomerMappingAsync(request.UserId, customerId).ConfigureAwait(false);
    }

    private async Task PersistSubscriptionSessionAsync(
        CheckoutSubscriptionSessionRequest request,
        StripeCheckoutSession session,
        PromotionValidationResult promotionResult,
        string? customerId)
    {
        SubscriptionRecord record = new SubscriptionRecord(
            request.UserId,
            request.BusinessSubscriptionId,
            SubscriptionStatus.Incomplete,
            customerId,
            session.SubscriptionId,
            request.Discount == null ? null : promotionResult.Outcome,
            request.Discount?.CouponId,
            request.Discount?.PromotionCodeId);

        await _subscriptionRecords.SaveAsync(record).ConfigureAwait(false);
        await SaveCustomerMappingAsync(request.UserId, customerId).ConfigureAwait(false);
    }

    private static void TagPaymentRequest(Activity? activity, CheckoutPaymentSessionRequest request)
    {
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.UserId, request.UserId);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.BusinessPaymentId, request.BusinessPaymentId);
        StripeKitDiagnostics.SetTag(activity, "currency", request.Currency);
    }

    private static void TagSubscriptionRequest(Activity? activity, CheckoutSubscriptionSessionRequest request)
    {
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.UserId, request.UserId);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.BusinessSubscriptionId, request.BusinessSubscriptionId);
        StripeKitDiagnostics.SetTag(activity, "price_id", request.PriceId);
    }

    private static void TagPaymentSession(Activity? activity, StripeCheckoutSession session, string? customerId)
    {
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.CheckoutSessionId, session.Id);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.PaymentIntentId, session.PaymentIntentId);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.CustomerId, customerId);
    }

    private static void TagSubscriptionSession(Activity? activity, StripeCheckoutSession session, string? customerId)
    {
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.CheckoutSessionId, session.Id);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.SubscriptionId, session.SubscriptionId);
        StripeKitDiagnostics.SetTag(activity, StripeKitDiagnosticTags.CustomerId, customerId);
    }

    private static void EmitPaymentCreatedLog(
        CheckoutPaymentSessionRequest request,
        StripeCheckoutSession session,
        PromotionValidationResult promotionResult,
        string? customerId)
    {
        StripeKitDiagnostics.EmitLog(
            "checkout.payment.created",
            (StripeKitDiagnosticTags.UserId, request.UserId),
            (StripeKitDiagnosticTags.BusinessPaymentId, request.BusinessPaymentId),
            (StripeKitDiagnosticTags.CheckoutSessionId, session.Id),
            (StripeKitDiagnosticTags.PaymentIntentId, session.PaymentIntentId),
            (StripeKitDiagnosticTags.CustomerId, customerId),
            ("promotion_outcome", request.Discount == null ? null : promotionResult.Outcome.ToString()),
            ("promotion_coupon_id", request.Discount?.CouponId),
            ("promotion_code_id", request.Discount?.PromotionCodeId));
    }

    private static void EmitSubscriptionCreatedLog(
        CheckoutSubscriptionSessionRequest request,
        StripeCheckoutSession session,
        PromotionValidationResult promotionResult,
        string? customerId)
    {
        StripeKitDiagnostics.EmitLog(
            "checkout.subscription.created",
            (StripeKitDiagnosticTags.UserId, request.UserId),
            (StripeKitDiagnosticTags.BusinessSubscriptionId, request.BusinessSubscriptionId),
            (StripeKitDiagnosticTags.CheckoutSessionId, session.Id),
            (StripeKitDiagnosticTags.SubscriptionId, session.SubscriptionId),
            (StripeKitDiagnosticTags.CustomerId, customerId),
            ("promotion_outcome", request.Discount == null ? null : promotionResult.Outcome.ToString()),
            ("promotion_coupon_id", request.Discount?.CouponId),
            ("promotion_code_id", request.Discount?.PromotionCodeId));
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

    private async Task<string?> ResolveCustomerIdAsync(
        string userId,
        string? providedCustomerId,
        CancellationToken cancellationToken)
    {
        if (_customerResolver == null)
        {
            return providedCustomerId;
        }

        return await _customerResolver
            .GetOrCreateCustomerIdAsync(userId, providedCustomerId, null, cancellationToken)
            .ConfigureAwait(false);
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

    private SessionCreateOptions BuildPaymentOptions(CheckoutPaymentSessionRequest request, string? customerId)
    {
        Dictionary<string, string> metadata = CreateMetadata(request.UserId, StripeKitDiagnosticTags.BusinessPaymentId, request.BusinessPaymentId);
        SessionCreateOptions options = CreateBaseOptions(
            mode: "payment",
            successUrl: request.SuccessUrl,
            cancelUrl: request.CancelUrl,
            customerId: customerId,
            clientReferenceId: request.BusinessPaymentId,
            allowPromotionCodes: request.AllowPromotionCodes,
            metadata: metadata,
            discount: request.Discount);

        options.LineItems = new List<SessionLineItemOptions>
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
        };
        options.PaymentIntentData = new SessionPaymentIntentDataOptions
        {
            Metadata = metadata
        };

        return options;
    }

    private SessionCreateOptions BuildSubscriptionOptions(CheckoutSubscriptionSessionRequest request, string? customerId)
    {
        Dictionary<string, string> metadata = CreateMetadata(request.UserId, StripeKitDiagnosticTags.BusinessSubscriptionId, request.BusinessSubscriptionId);
        SessionCreateOptions options = CreateBaseOptions(
            mode: "subscription",
            successUrl: request.SuccessUrl,
            cancelUrl: request.CancelUrl,
            customerId: customerId,
            clientReferenceId: request.BusinessSubscriptionId,
            allowPromotionCodes: request.AllowPromotionCodes,
            metadata: metadata,
            discount: request.Discount);

        options.LineItems = new List<SessionLineItemOptions>
        {
            new SessionLineItemOptions
            {
                Quantity = 1,
                Price = request.PriceId
            }
        };
        options.SubscriptionData = new SessionSubscriptionDataOptions
        {
            Metadata = metadata
        };

        return options;
    }

    private Dictionary<string, string> CreateMetadata(string userId, string businessKey, string businessId)
    {
        Dictionary<string, string> metadata = new Dictionary<string, string>(StripeMetadataMapper.CreateForUser(userId));
        metadata[businessKey] = businessId;
        return metadata;
    }

    private SessionCreateOptions CreateBaseOptions(
        string mode,
        string successUrl,
        string cancelUrl,
        string? customerId,
        string clientReferenceId,
        bool allowPromotionCodes,
        Dictionary<string, string> metadata,
        StripeDiscount? discount)
    {
        SessionCreateOptions options = new SessionCreateOptions
        {
            Mode = mode,
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            Customer = customerId,
            ClientReferenceId = clientReferenceId,
            AllowPromotionCodes = _options.EnablePromotions && allowPromotionCodes,
            Metadata = metadata
        };

        ApplyDiscount(options, discount);

        return options;
    }

    private void ApplyDiscount(SessionCreateOptions options, StripeDiscount? discount)
    {
        if (!_options.EnablePromotions || discount == null)
        {
            return;
        }

        options.Discounts = new List<SessionDiscountOptions>
        {
            BuildDiscountOption(discount)
        };
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

    private sealed class CheckoutSessionWorkflow<TRequest>
        where TRequest : class
    {
        public CheckoutSessionWorkflow(
            Action ensureEnabled,
            Action<TRequest> validate,
            string activityName,
            Func<TRequest, string> getUserId,
            Func<TRequest, string?> getProvidedCustomerId,
            Func<TRequest, StripeDiscount?> getDiscount,
            Func<TRequest, string> getBusinessId,
            Func<TRequest, string?> getProvidedIdempotencyKey,
            string idempotencyScope,
            Action<Activity?, TRequest> tagRequest,
            Func<TRequest, string?, SessionCreateOptions> buildOptions,
            Action<Activity?, StripeCheckoutSession, string?> tagSession,
            Func<TRequest, StripeCheckoutSession, PromotionValidationResult, string?, Task> persistAsync,
            Action<TRequest, StripeCheckoutSession, PromotionValidationResult, string?> emitLog)
        {
            EnsureEnabled = ensureEnabled;
            Validate = validate;
            ActivityName = activityName;
            GetUserId = getUserId;
            GetProvidedCustomerId = getProvidedCustomerId;
            GetDiscount = getDiscount;
            GetBusinessId = getBusinessId;
            GetProvidedIdempotencyKey = getProvidedIdempotencyKey;
            IdempotencyScope = idempotencyScope;
            TagRequest = tagRequest;
            BuildOptions = buildOptions;
            TagSession = tagSession;
            PersistAsync = persistAsync;
            EmitLog = emitLog;
        }

        public Action EnsureEnabled { get; }
        public Action<TRequest> Validate { get; }
        public string ActivityName { get; }
        public Func<TRequest, string> GetUserId { get; }
        public Func<TRequest, string?> GetProvidedCustomerId { get; }
        public Func<TRequest, StripeDiscount?> GetDiscount { get; }
        public Func<TRequest, string> GetBusinessId { get; }
        public Func<TRequest, string?> GetProvidedIdempotencyKey { get; }
        public string IdempotencyScope { get; }
        public Action<Activity?, TRequest> TagRequest { get; }
        public Func<TRequest, string?, SessionCreateOptions> BuildOptions { get; }
        public Action<Activity?, StripeCheckoutSession, string?> TagSession { get; }
        public Func<TRequest, StripeCheckoutSession, PromotionValidationResult, string?, Task> PersistAsync { get; }
        public Action<TRequest, StripeCheckoutSession, PromotionValidationResult, string?> EmitLog { get; }
    }
}
