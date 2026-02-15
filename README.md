# StripeKit.NET
Minimal, production-correct Stripe integration toolkit for .NET: webhooks, idempotency, billing state — packaged as a small library + a tiny sample API.

## Why
Stripe integrations usually break in 3 places:
1) duplicate operations from retries → idempotency everywhere
2) webhook signature/body handling → raw-body verification
3) billing state drift (late/out-of-order/missed events) → webhook-driven state + reconciliation

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
- Events are replay-safe (dedupe by event.id)
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
- reprocesses safely using the same event.id dedupe + idempotent handlers

(Goal: if a region/network hiccup causes delays or retries, your system converges to the correct state.)

## Observability baseline
- StripeKit emits activities from source name `StripeKit` for checkout, refunds, webhooks, and reconciliation.
- Correlation tags include `user_id`, business ids, Stripe ids, and `event_id` when available.
- Sample API logging enables `trace_id` and `span_id` correlation fields.

## Want this implemented in your app?
Share: (1) Checkout vs PaymentIntents, (2) subscriptions yes/no, (3) which events you rely on.
I’ll wire StripeKit into your codebase with tests and a minimal migration path.
