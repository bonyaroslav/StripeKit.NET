using System.Text;
using Stripe;
using Stripe.Checkout;
using StripeKit;

var builder = WebApplication.CreateBuilder(args);

string? stripeApiKey = builder.Configuration["STRIPE_SECRET_KEY"] ?? builder.Configuration["Stripe:SecretKey"];
if (string.IsNullOrWhiteSpace(stripeApiKey))
{
    throw new InvalidOperationException("Stripe API key is required. Set STRIPE_SECRET_KEY or Stripe:SecretKey.");
}

StripeConfiguration.ApiKey = stripeApiKey;

builder.Services.AddSingleton(new StripeKitOptions
{
    EnablePayments = true,
    EnableBilling = true,
    EnablePromotions = true,
    EnableWebhooks = true,
    EnableRefunds = true
});
builder.Services.AddSingleton<ICustomerMappingStore, InMemoryCustomerMappingStore>();
builder.Services.AddSingleton<IWebhookEventStore, InMemoryWebhookEventStore>();
builder.Services.AddSingleton<IPaymentRecordStore, InMemoryPaymentRecordStore>();
builder.Services.AddSingleton<ISubscriptionRecordStore, InMemorySubscriptionRecordStore>();
builder.Services.AddSingleton<IRefundRecordStore, InMemoryRefundRecordStore>();
builder.Services.AddSingleton<IPromotionEligibilityPolicy, AllowAllPromotionEligibilityPolicy>();
builder.Services.AddSingleton<WebhookSignatureVerifier>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<ICheckoutSessionClient, StripeCheckoutSessionClient>();
builder.Services.AddSingleton<StripeCheckoutSessionCreator>();
builder.Services.AddSingleton<EventService>();
builder.Services.AddSingleton<IStripeEventClient, StripeEventClient>();
builder.Services.AddSingleton<PaymentIntentService>();
builder.Services.AddSingleton<InvoiceService>();
builder.Services.AddSingleton<SubscriptionService>();
builder.Services.AddSingleton<IStripeObjectLookup, StripeObjectLookup>();
builder.Services.AddSingleton<RefundService>();
builder.Services.AddSingleton<IRefundClient, StripeRefundClient>();
builder.Services.AddSingleton<StripeRefundCreator>();
builder.Services.AddSingleton<StripeWebhookProcessor>();
builder.Services.AddSingleton<StripeEventReconciler>();

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapPost("/checkout/payment", async (
    CheckoutPaymentSessionRequest request,
    StripeCheckoutSessionCreator creator) =>
{
    CheckoutSessionResult result = await creator.CreatePaymentSessionAsync(request);

    if (result.Session == null)
    {
        return Results.BadRequest(new
        {
            status = "promotion_failed",
            promotion = result.PromotionResult.Outcome.ToString(),
            message = result.PromotionResult.Message
        });
    }

    return Results.Ok(new
    {
        session_id = result.Session.Id,
        url = result.Session.Url,
        promotion = result.PromotionResult.Outcome.ToString()
    });
});

app.MapPost("/checkout/subscription", async (
    CheckoutSubscriptionSessionRequest request,
    StripeCheckoutSessionCreator creator) =>
{
    CheckoutSessionResult result = await creator.CreateSubscriptionSessionAsync(request);

    if (result.Session == null)
    {
        return Results.BadRequest(new
        {
            status = "promotion_failed",
            promotion = result.PromotionResult.Outcome.ToString(),
            message = result.PromotionResult.Message
        });
    }

    return Results.Ok(new
    {
        session_id = result.Session.Id,
        url = result.Session.Url,
        promotion = result.PromotionResult.Outcome.ToString()
    });
});

app.MapPost("/refunds", async (
    RefundRequest request,
    StripeRefundCreator creator) =>
{
    RefundResult result = await creator.CreateRefundAsync(request);

    return Results.Ok(new
    {
        refund_id = result.Refund.Id,
        status = result.Refund.Status.ToString()
    });
});

// Stripe webhook endpoint placeholder.
// IMPORTANT: real implementation must verify Stripe signature using the RAW request body.
app.MapPost("/webhooks/stripe", async (
    HttpRequest request,
    IConfiguration configuration,
    StripeWebhookProcessor processor) =>
{
    string? secret = configuration["STRIPE_WEBHOOK_SECRET"] ?? configuration["Stripe:WebhookSecret"];
    if (string.IsNullOrWhiteSpace(secret))
    {
        return Results.Problem("Stripe webhook secret is required.");
    }

    request.EnableBuffering();
    using StreamReader reader = new StreamReader(request.Body, Encoding.UTF8, false, 1024, true);
    string payload = await reader.ReadToEndAsync();
    request.Body.Position = 0;

    string signature = request.Headers["Stripe-Signature"].ToString();
    if (string.IsNullOrWhiteSpace(signature))
    {
        return Results.BadRequest(new { status = "failed", error = "Stripe-Signature header is required." });
    }

    try
    {
        WebhookProcessingResult result = await processor.ProcessAsync(payload, signature, secret);

        if (result.IsDuplicate)
        {
            return Results.Ok(new { status = "duplicate" });
        }

        if (result.Outcome == null || !result.Outcome.Succeeded)
        {
            return Results.BadRequest(new { status = "failed", error = result.Outcome?.ErrorMessage });
        }

        return Results.Ok(new { status = "ok" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { status = "failed", error = ex.Message });
    }
});

// Reconciliation endpoint (demo-only; extractable to HostedService/CLI later).
app.MapPost("/reconcile", async (
    ReconciliationRequest? request,
    StripeEventReconciler reconciler,
    CancellationToken cancellationToken) =>
{
    ReconciliationResult result = await reconciler.ReconcileAsync(request, cancellationToken);

    return Results.Ok(new
    {
        total = result.Total,
        processed = result.Processed,
        duplicates = result.Duplicates,
        failed = result.Failed,
        has_more = result.HasMore,
        last_event_id = result.LastEventId
    });
});

app.Run();
