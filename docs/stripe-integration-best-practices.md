# StripeKit.NET – Reliability & Best Practices Knowledge Base (SMB-focused)

Goal: a small/medium business Stripe integration that is production-correct under retries, delayed webhooks, and partial failures.
Design bias: minimal surface area, readable code, test-first, and “correctness defaults”.

---

## 0) The invariants (non-negotiable)

1) **All write operations are idempotent**
- Outbound (your server → Stripe): use Stripe idempotency keys on POST requests to prevent duplicate objects/charges when you retry.  
  Source: Stripe “Idempotent requests”.  
- Inbound (Stripe → your server via webhooks): process events idempotently (at least event.id dedupe), because Stripe retries deliveries on failure.

2) **Webhooks are verified using the raw request body**
- Signature verification requires the exact raw payload bytes. Any body parsing/mutation before verification can break signatures.
- Verify first, then parse/route/handle.

3) **System converges**
- Assume webhook delivery can be delayed, retried, and out-of-order.
- Your state must converge via idempotent handlers + a minimal reconciliation/backfill job.

---

## 1) Stripe idempotency (server → Stripe)

### 1.1 What Stripe guarantees
- Stripe accepts idempotency keys for POST requests; you can retry safely without performing the operation twice.

### 1.2 Best practice rules for keys
- **Use stable, business-derived keys** (NOT random GUID per retry).
- **One business operation ↔ one idempotency key**
- If you have “create-or-get” flows, use deterministic keys like `customer:{user_id}`.

Recommended anchors (examples):
- `checkout_payment:{business_payment_id}`
- `checkout_subscription:{business_subscription_id}`
- `refund:{business_refund_id}`
- `customer:{user_id}`

### 1.3 Where teams usually fail
- Using a new key on each retry → duplicates are back.
- Not persisting the local “operation record” → you can’t prove whether you already executed.

---

## 2) Webhooks (Stripe → server)

### 2.1 Security basics
- Always verify the webhook signature with the endpoint signing secret.
- Use HTTPS.
- Track processed events to prevent replay/double-processing.

### 2.2 Raw-body verification (ASP.NET Core guidance)
- Ensure you can read the raw body without it being altered.
- If middleware/controllers need the body more than once, enable buffering at the right layer.

Pitfall: model-binding/JSON parsing before signature verification.

### 2.3 Reliability: retries, timeouts, and “fast ack”
- Stripe retries undelivered events for up to ~3 days (live mode).
- Webhook handlers should be:
  - **quick** (avoid long-running work in the request thread)
  - **idempotent**
  - **observable** (log event_id + outcome)
- Pattern that scales well:
  1) verify signature
  2) store the event id + payload hash (optional) + enqueue work
  3) return 2xx
  4) process asynchronously with retry/DLQ

(If you process inline, you must still handle duplicates and partial failures.)

### 2.4 Dedupe strategy
Minimum viable:
- Store `event.id` with a unique constraint.
- If insert fails because it already exists → treat as success and return 2xx.

---

## 3) Billing state correctness (subscriptions/invoices)

### 3.1 Webhook-driven state
- Treat webhooks as the source of truth for “what happened”.
- Persist a local state model (subscription, entitlement, invoices) updated by webhook events.

### 3.2 Minimum event set (typical SMB SaaS)
- payment_intent.succeeded / payment_intent.payment_failed
- customer.subscription.created / updated / deleted
- invoice.payment_succeeded / invoice.payment_failed
- refund.created / refund.updated / refund.failed

(Exact set depends on whether you use Checkout vs direct PaymentIntents and whether you sell subscriptions.)

---

## 4) Reconciliation / backfill

Why: even with retries, gaps happen (downtime, networking, deploy issues). You need convergence.

Approach:
- Periodically list recent Stripe events and backfill anything not processed.
- Reprocess using the same pipeline: verify → dedupe → handle.
- Stripe Events API supports access over a rolling window (e.g., “retrieve event” for recent events; list events).

Operational note:
- Add rate limiting / batching.
- Maintain a cursor/watermark if possible.

---

## 5) Stripe API versioning and upgrades

- Stripe supports API versioning; upgrades should be tested before rollout.
- Recommend: pin an API version in your Stripe account, and only upgrade intentionally after running integration tests (incl. webhook handling).
- For some scenarios (org keys / consistency), Stripe expects a Stripe-Version header to ensure predictable behavior.

---

## 6) Testing strategy (must-have)

### 6.1 Local webhook testing (official)
- Use Stripe CLI to forward webhooks to localhost.
- Use Stripe CLI to trigger events in a sandbox.

### 6.2 What to test (minimum)
Critical:
- Signature verification uses raw body (valid + invalid signature).
- event.id dedupe works (deliver same event twice → only one effect).
- Idempotency keys on POST calls (same key retried → no duplicates).
- Subscription payment failure path (invoice.payment_failed) doesn’t grant entitlement.
- Reconciliation can backfill missed events without double-applying.

### 6.3 Test style
- Unit tests for pure domain logic (state transitions).
- Integration tests around Stripe client wrappers + webhook pipeline (can be “recorded”/mocked at boundary if needed).
- Keep PRs small, prefer editing existing files, avoid new dependencies unless justified.

---

## 7) Observability (debuggability is part of correctness)

Minimum telemetry per request/event:
- trace_id / span_id
- event_id (for webhooks)
- idempotency_key (for POST writes)
- user_id + business ids (avoid PII in logs)
- outcome: processed/duplicate/rejected + reason

Add counters:
- webhook_signature_failures
- webhook_processing_failures
- reconciliation_backfill_count
- idempotency_conflicts

---

## 8) Code review checklist (quick)

Webhooks:
- [ ] raw body used for signature verification
- [ ] signature verified before parsing
- [ ] event.id stored with unique constraint
- [ ] handler is idempotent (duplicate events are safe)
- [ ] returns 2xx quickly (or has strong timeout handling)

Writes to Stripe:
- [ ] POST calls always include idempotency key
- [ ] key is stable business anchor (not per-retry random)
- [ ] operation mapped/persisted locally

Convergence:
- [ ] reconciliation exists and reuses the same dedupe + handlers
- [ ] backfill is bounded (time window, batching)

Testing:
- [ ] tests cover critical paths listed above
- [ ] Stripe CLI instructions are documented

---

## Sources (URLs)
- https://docs.stripe.com/api/idempotent_requests
- https://stripe.com/blog/idempotency
- https://docs.stripe.com/webhooks
- https://docs.stripe.com/webhooks/signature
- https://docs.stripe.com/webhooks/process-undelivered-events
- https://docs.stripe.com/cli/listen
- https://docs.stripe.com/stripe-cli/triggers
- https://docs.stripe.com/api/versioning
- https://docs.stripe.com/sdks/versioning
- https://docs.stripe.com/api/events/retrieve
- https://devblogs.microsoft.com/dotnet/re-reading-asp-net-core-request-bodies-with-enablebuffering/
