# StripeKit.NET
Minimal, production-correct Stripe integration toolkit for .NET: webhooks, idempotency, billing state - packaged as a small library + a tiny sample API.

## Why
Stripe integrations usually break in 3 places:
1) duplicate operations from retries → idempotency everywhere
2) webhook signature/body handling → raw-body verification
3) billing state drift (late/out-of-order/missed events) → webhook-driven state + reconciliation

## Production Features You Get Today
StripeKit.NET gives you production-grade billing safeguards out of the box, without heavyweight architecture.

- Verified webhooks from raw body (`Stripe-Signature`) so signature checks are trustworthy.
- Idempotent webhook processing by Stripe `event.id` so retries and duplicates stay safe.
- Retry-aware dedupe semantics so failed events can be retried and successful events remain replay-safe.
- Deterministic idempotency keys for create calls so the same business operation does not double-charge.
- Checkout support for both one-time payments and subscriptions to ship common billing flows fast.
- Promotions in both modes: customer-entered promo codes and backend-applied coupon/promo discounts.
- Business-id + metadata correlation so Stripe objects remain traceable to your internal `user_id`.
- Out-of-order event guards and status precedence so delayed events cannot regress terminal state.
- Thin-event fallback lookups so missing webhook fields can be resolved from Stripe objects.
- Reconciliation pipeline that replays recent events through the same dedupe + handlers to converge state.
- Refund flow with guardrails so only valid succeeded payments are refunded, idempotently.
- Built-in observability via `ActivitySource` (`StripeKit`) with correlation tags for debugging and support.
- Store seams for both in-memory and DB-backed adapters so you can start fast and persist when ready.

## Why Teams Pick StripeKit.NET
- Engineers: fewer billing edge-case bugs, clear extension seams, and test-backed behavior.
- Product teams: faster launch with safer defaults for payments, subscriptions, and refunds.
- Clients/stakeholders: stronger revenue reliability through duplicate protection and state convergence.

## Modules (enable what you need)
- Core: Stripe client + mapping + helpers
- Webhooks: signature verify + event.id dedupe + handler routing
- Payments: Checkout sessions (one-time)
- Billing : subscriptions + invoice-driven lifecycle
- Promotions : Checkout promotion codes / discounts
- Refunds : full refunds (idempotent)
- Reconciliation : demo endpoint (extractable)
- Observability : baseline trace correlation via `ActivitySource` (`StripeKit`)
- Tests : unit + integration

## Guarantees (the “correctness defaults”)
- POST calls are idempotent (stable business key → idempotency key)
- Webhooks verified using the raw request body (no “signature failed” surprises)
- Events are replay-safe (dedupe by `event.id` after successful processing; failed events can be retried)
- Payment/subscription transitions are order-aware by Stripe event `created`; stale events are ignored
- Terminal/precedence rules prevent regressions (for example canceled subscriptions are not reactivated by delayed success events)
- State is webhook-driven; reconciliation repairs gaps

## Repo layout example
- /src/StripeKit
- /samples/StripeKit.SampleApi
- /tests/*

Sample API includes a demo DB-backed adapter at `samples/StripeKit.SampleApi/SampleStorage/DbStripeKitStore.cs` with schema at `samples/StripeKit.SampleApi/SampleStorage/schema.sql`.
This adapter is reference/demo-only and is not a production migration or durability recommendation.


## Quick start
1) Set env:
   - STRIPE_SECRET_KEY
   - STRIPE_WEBHOOK_SECRET
2) Run:
   - dotnet run --project samples/StripeKit.SampleApi
3) Forward webhooks (Stripe CLI) to /webhooks/stripe
4) Test:
   - dotnet test

## Module toggles (feature flags)
Configure module flags in `samples/StripeKit.SampleApi/appsettings.json` under `StripeKit:Modules`:
- `EnablePayments`
- `EnableBilling`
- `EnablePromotions`
- `EnableWebhooks`
- `EnableRefunds`

Example:
```json
"StripeKit": {
  "Modules": {
    "EnablePayments": true,
    "EnableBilling": true,
    "EnablePromotions": true,
    "EnableWebhooks": true,
    "EnableRefunds": true
  }
}
```

Invariant:
- If `EnablePayments` or `EnableBilling` is `true`, `EnableWebhooks` must be `true`.

## Sample DB mode (optional)
1) Apply `samples/StripeKit.SampleApi/SampleStorage/schema.sql` to your sample DB.
2) Set both app settings:
   - `StripeKit:DbProviderInvariantName`
   - `StripeKit:DbConnectionString`
3) Run sample API; it will use `DbStripeKitStore` instead of in-memory stores.
4) Optional local check:
   - `pwsh -File samples/StripeKit.SampleApi/SampleStorage/verify-schema.ps1`

## Integration model (drop-in)
StripeKit stays small by requiring only a few seams:
- Storage adapter: mappings, processed events, local billing/payment/refund records
- Domain hooks (optional): grant entitlement / email / provisioning
- Promo policy hook (optional): business rules without touching payment core

## Reconciliation (simple by design)
A scheduled job that:
- lists recent Stripe events (default: last 30 days, limit 100) and backfills anything you didn’t process
- reprocesses safely using the same `event.id` dedupe + idempotent handlers, including previously failed events

(Goal: if a region/network hiccup causes delays or retries, your system converges to the correct state.)

## Required Stripe webhook event types
Minimum events expected by StripeKit v1:
- `payment_intent.succeeded`
- `payment_intent.payment_failed`
- `customer.subscription.created`
- `customer.subscription.updated`
- `customer.subscription.deleted`
- `invoice.payment_succeeded`
- `invoice.payment_failed`
- `checkout.session.completed`
- `refund.created`
- `refund.updated`
- `refund.failed`

These should be enabled on your Stripe webhook endpoint that targets `/webhooks/stripe`.

Correlation note:
- StripeKit writes `business_payment_id` / `business_subscription_id` metadata on Checkout create paths and uses those anchors as webhook fallback correlation when Stripe IDs were initially null.

## Idempotency key strategy
StripeKit supports caller-provided idempotency keys and generates deterministic defaults when omitted.

Recommended stable business anchors:
- Checkout payment: `checkout_payment:{business_payment_id}`
- Checkout subscription: `checkout_subscription:{business_subscription_id}`
- Refund create: `refund:{business_refund_id}`
- Customer get-or-create: `customer:{user_id}`

Guidelines:
- Use a stable business ID, never a random GUID per retry.
- Reuse the same key on retries for the same business operation.
- Keep one business operation mapped to one idempotency key.

## Promotions modes
StripeKit supports both:
- Customer-entered promotions in Checkout (`allow_promotion_codes`)
- Backend-supplied discount (`coupon` or `promotion_code`)

Promotion outcomes tracked in flow:
- `Applied`
- `Invalid`
- `Expired`
- `NotApplicable`

## Observability baseline
- StripeKit emits activities from source name `StripeKit` for checkout, refunds, webhooks, and reconciliation.
- Correlation tags include `user_id`, business ids, Stripe ids, and `event_id` when available.
- Sample API logging enables `trace_id` and `span_id` correlation fields.

## Want this implemented in your app?
Share: (1) Checkout vs PaymentIntents, (2) subscriptions yes/no, (3) which events you rely on.
I’ll wire StripeKit into your codebase with tests and a minimal migration path.
