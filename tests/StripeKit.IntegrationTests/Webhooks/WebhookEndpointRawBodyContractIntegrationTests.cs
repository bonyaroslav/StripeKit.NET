using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;

namespace StripeKit.IntegrationTests;

public class WebhookEndpointRawBodyContractIntegrationTests
{
    [Fact]
    public async Task WebhooksStripe_RawBodyContract_AcceptsExactPayloadAndRejectsMutatedPayload()
    {
        const string secret = "whsec_test";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["STRIPE_WEBHOOK_SECRET"] = secret
            })
            .Build();

        StripeKitOptions options = new StripeKitOptions();
        IWebhookEventStore store = new InMemoryWebhookEventStore();
        IPaymentRecordStore payments = new InMemoryPaymentRecordStore();
        ISubscriptionRecordStore subscriptions = new InMemorySubscriptionRecordStore();
        IRefundRecordStore refunds = new InMemoryRefundRecordStore();
        WebhookSignatureVerifier verifier = new WebhookSignatureVerifier();
        IStripeObjectLookup objectLookup = new NoopStripeObjectLookup();
        StripeWebhookProcessor processor = new StripeWebhookProcessor(verifier, store, payments, subscriptions, refunds, objectLookup, options);

        await payments.SaveAsync(new PaymentRecord("user_raw_1", "pay_raw_1", PaymentStatus.Pending, "pi_raw_1", null));

        string exactPayload = "{\"id\":\"evt_raw_1\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"pi_raw_1\",\"object\":\"payment_intent\",\"status\":\"succeeded\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.succeeded\"}";
        string exactSignature = BuildSignatureHeader(exactPayload, secret);

        IResult validResult = await InvokeWebhookEndpointAsync(exactPayload, exactSignature, configuration, processor);
        int? validStatusCode = (validResult as IStatusCodeHttpResult)?.StatusCode;
        Assert.Equal(StatusCodes.Status200OK, validStatusCode);

        string mutatedPayload = "{ \"id\":\"evt_raw_2\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1700000000,\"data\":{\"object\":{\"id\":\"pi_raw_1\",\"object\":\"payment_intent\",\"status\":\"succeeded\"}},\"livemode\":false,\"pending_webhooks\":1,\"type\":\"payment_intent.succeeded\"}";
        string signatureFromDifferentRawBody = BuildSignatureHeader(exactPayload, secret);

        IResult invalidResult = await InvokeWebhookEndpointAsync(mutatedPayload, signatureFromDifferentRawBody, configuration, processor);
        int? invalidStatusCode = (invalidResult as IStatusCodeHttpResult)?.StatusCode;
        Assert.Equal(StatusCodes.Status400BadRequest, invalidStatusCode);
    }

    private static async Task<IResult> InvokeWebhookEndpointAsync(
        string rawPayload,
        string stripeSignature,
        IConfiguration configuration,
        StripeWebhookProcessor processor)
    {
        DefaultHttpContext context = new DefaultHttpContext();
        context.Request.Path = "/webhooks/stripe";
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers["Stripe-Signature"] = stripeSignature;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(rawPayload));

        HttpRequest request = context.Request;

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

            if (result.Outcome == null)
            {
                return Results.StatusCode(StatusCodes.Status409Conflict);
            }

            if (!result.Outcome.Succeeded)
            {
                return Results.StatusCode(StatusCodes.Status409Conflict);
            }

            return Results.Ok(new { status = "ok" });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { status = "failed", error = ex.Message });
        }
    }

    private sealed class NoopStripeObjectLookup : IStripeObjectLookup
    {
        public Task<string?> GetPaymentIntentIdAsync(string objectId)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<string?> GetSubscriptionIdAsync(string objectId)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private static string BuildSignatureHeader(string payload, string secret)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signature = ComputeSignature(timestamp, payload, secret);
        return $"t={timestamp},v1={signature}";
    }

    private static string ComputeSignature(long timestamp, string payload, string secret)
    {
        string signedPayload = timestamp + "." + payload;
        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(signedPayload);

        using HMACSHA256 hmac = new HMACSHA256(secretBytes);
        byte[] hash = hmac.ComputeHash(payloadBytes);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
