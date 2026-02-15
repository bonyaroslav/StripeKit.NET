# StripeKit.NET – Requirements

## 1) Goal

Build a **drop-in Stripe integration toolkit for .NET** that demonstrates production-grade patterns with minimal code:

* One-time payments
* Subscriptions (Billing)
* Webhooks (verified + idempotent)
* Promotions/discounts
* Refunds (optional)
* Observability + tests (required)

## 2) Modules and toggles

Toolkit is split into feature modules. Each module can be **enabled/disabled by configuration** (no code deletion).

**Modules**

* `Core` (required)
* `Webhooks` (required when Payments or Billing enabled)
* `Payments` (optional)
* `Billing` (optional)
* `Promotions` (optional but expected for your use-case)
* `Refunds` (optional)
* `Observability + Tests` (required)

## 3) Global invariants (apply everywhere)

### 3.1 Idempotency (no duplicates)

* Every Stripe “create” call **must use a caller-controlled idempotency key** derived from a stable business ID (not a random GUID per retry).
* Webhook processing is idempotent via **`event.id` deduplication** (store processed events; skip reprocessing).

### 3.2 Security

* Webhook signature verification **must use the raw request body**.
* Secrets come from env/secret store; never committed.
* No card data stored outside Stripe.

### 3.3 Traceability / supportability

* Every Stripe object created/processed must be linkable to an internal `user_id`:

  * Persist mapping internally (`user_id` ↔ Stripe IDs)
  * Write `user_id` into Stripe object metadata (where supported)

### 3.4 Observability

* Structured logs include: `user_id`, correlation id, Stripe IDs, and `event.id` for webhooks.
* Webhook outcomes are recorded: success/failure + last error (for retry/debug).

### 3.5 Forward compatibility

* Webhook handlers must tolerate **“thin” events** (if required fields are missing, fetch the object by ID).

---

## 4) Module contracts (what each must do)

### A) Core (required)

**Responsibilities**

* Get-or-create Stripe Customer for `user_id`
* Retrieve Stripe objects by ID (for reconciliation + thin events)
* Provide shared helpers for idempotent Stripe calls + metadata mapping

**Guarantees**

* All create operations accept an explicit idempotency key.
* All created Stripe objects include `metadata.user_id` (or equivalent mapping metadata).

---

### B) Payments (optional)

**Start payment**
Input: `{ user_id, amount, currency, business_payment_id }`
Output: minimal client artifact to complete payment (e.g., client secret or session reference).

**Persistence**
Store payment record:

* `user_id`
* `business_payment_id` (the idempotency anchor)
* Stripe IDs (PaymentIntent ID; Charge ID if available)
* Status: `Pending | Succeeded | Failed | Canceled`

**Finalization**

* Final payment status is set via **webhooks**, not only the synchronous API response.

---

### C) Billing / Subscriptions (optional)

**Start subscription**
Input: `{ user_id, price_id, business_subscription_id }`

**Persistence**
Store subscription record:

* `user_id`
* `business_subscription_id`
* Stripe IDs: customer, subscription
* Status: `Incomplete | Active | PastDue | Canceled`

**Lifecycle updates (webhook-driven)**

* Subscription created/updated/deleted
* Renewal success/failure based on invoice events

---

### D) Webhooks (required when Payments or Billing enabled)

**Receiver must**

* Verify signature using raw body
* Dedupe by `event.id`
* Route events deterministically by type
* Record processing result (success/failure + last error)

**Minimum events**
Payments:

* `payment_intent.succeeded`
* `payment_intent.payment_failed`

Billing:

* `customer.subscription.created`
* `customer.subscription.updated`
* `customer.subscription.deleted`
* `invoice.payment_succeeded`
* `invoice.payment_failed`

---

### E) Promotions / Discounts (optional; recommended)

Support two application modes:

1. **Customer-entered promo codes**

* Allow promo-code entry for flows that support it.

2. **Backend-supplied discounts**

* Apply coupon/promo/discount explicitly where supported.

**Validation result**
If promo is provided, return one of:

* `Applied | Invalid | Expired | NotApplicable`

**Persistence**

* Record which promotion was applied (internal record + Stripe IDs used)

**Pluggability**

* Provide a replaceable hook for “promotion eligibility” so a client can enforce rules (per-plan, first-time, geo, allowlist) without modifying core payment logic.

---

### F) Refunds (optional)

**Create refund**

* Refund a previously successful payment (full refund is enough for v1).
* Refund requests are anchored on `business_refund_id` and should be idempotent.

**Persistence**
Refund record linked to:

* `user_id`
* `business_refund_id`
* `business_payment_id`
* Stripe IDs: payment intent, refund
* Status: `Pending | Succeeded | Failed`

**Idempotency**

* Repeated refund intent does not create multiple refunds.

---

## 5) Testing and “definition of done” (required)

### Tests (single command)

* Unit tests: idempotency behavior, mapping, promotion validation outcomes
* Integration tests: webhook signature verification + event dedupe + state updates
* Must run via `dotnet test`

### README must include

* Config: Stripe keys + webhook secret
* Required event types
* How idempotency keys are chosen (which business IDs to use)
* How to enable/disable modules (feature toggles)
* Promotions: customer-entered vs backend-supplied

---