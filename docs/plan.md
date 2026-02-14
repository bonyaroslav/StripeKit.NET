# StripeKit.NET — High-Level Plan

## 1) Purpose
StripeKit.NET is a reference-quality .NET toolkit for integrating Stripe **hosted Checkout** to support:
- One-time payments
- Subscriptions with invoice-driven lifecycle
- Customer-entered promotion codes
…while staying **modular, testable, and operationally safe** (idempotency, verified webhooks, reconciliation, observability).

This file is a **plan + decision log**. Detailed requirements live elsewhere (requirements.md). This plan is intended to be iterated: adjust decisions, then generate/update project structure, then implement phase-by-phase.

---

## 2) v1 decisions (locked for this plan)
### Product / Integration choices
- **Checkout-first (Stripe-hosted)**. No custom UI/Elements/PaymentIntents in v1.
- **Promotions: promo entry only** (`allow_promotion_codes`). Default discounts postponed (**placeholder only**).
- **No Customer Portal in v1** (placeholder + docs link).
- **No Connect / multi-account in v1** (placeholder + docs link).

### Reliability / operations choices
- **Webhooks are authoritative** for final state (success/fail transitions converge from events).
- **Idempotency is mandatory** for all Stripe create/update requests that could be retried.
- **Reconciliation** is exposed as an **HTTP endpoint for demo**, implemented so it can be moved to HostedService/CLI later.

### Codebase / packaging choices
- **Storage**: core ships interfaces + in-memory reference; provide a **DB-backed adapter sample** (minimal schema, demo-only).
- **Observability**: include **OpenTelemetry SDK** with minimal setup and log/trace correlation.
- **Documentation conventions** are part of DoD (see §10).

---

## 3) Payment methods stance (v1)
Default: **dynamic payment methods** (Dashboard-driven), which typically covers **cards + wallets** with minimal code.

Optional add-ons: feature-flagged and documented, not mandatory. (Exact methods are not hard-coded into v1 core; the toolkit stays Checkout-centered and configuration-friendly.)
- US-oriented add-ons (future): e.g., ACH bank payments (deferred unless explicitly needed).
- EU/UK add-ons (future): e.g., bank debits / local methods (documented and configuration-driven).

NOTE: Payment method choices are treated as **configuration and documentation**, not a core architectural axis.

---

## 4) Core architectural principles (what makes it “good code”)
- **Isolated Core**: business flows/state transitions do not depend directly on ASP.NET, DB, or Stripe SDK types; adapters wrap external concerns.
- **Low coupling / high cohesion**: each module has a narrow purpose (Webhooks, Checkout, Billing, Promotions, Reconciliation, Observability).
- **Contracts-first seams**: storage + webhook dedupe + reconciliation cursor + provisioning hooks are interfaces.
- **Idempotency everywhere it matters**: deterministic idempotency keys derived from stable business identifiers.
- **Webhook correctness**: verify signatures using raw request body; handlers are replay-safe and tolerant to duplicates/out-of-order delivery.
- **Operational transparency**: every webhook and reconciliation run produces correlated logs/traces with Stripe IDs and business IDs.

---

## 5) Modules (v1)
1) **Core**
- Options/config, ID + idempotency helpers, metadata mapping, error mapping
- Storage interfaces + in-memory reference

2) **Checkout (Payments + Subscriptions)**
- Create Checkout Session (payment mode, subscription mode)
- Promo entry toggle (`allow_promotion_codes`)

3) **Webhooks**
- Single endpoint handler: verify signature (raw body)
- Dedupe by `event.id` in storage
- Route events to small handlers that converge state

4) **Billing lifecycle**
- Subscription + invoice-driven transitions (minimal set of events; documented)
- Resilient to retries and out-of-order event delivery

5) **Promotions**
- Promo entry only in Checkout
- Placeholder: “default discount” (not implemented)

6) **Reconciliation (Demo)**
- HTTP endpoint that lists/repairs drift by replaying missed events through the same dedupe + handlers
- Commented as “extractable to HostedService/CLI”

7) **Observability**
- OpenTelemetry minimal wiring: traces + logs correlation
- Required log context fields (see §6)

---

## 6) Required correlation fields (v1 observability contract)
Every meaningful log line inside StripeKit should include:
- `trace_id`, `span_id` (when available)
- `user_id` (your system)
- `business_payment_id` / `business_subscription_id` (when relevant)
- Stripe IDs when available: `checkout_session_id`, `payment_intent_id`, `subscription_id`, `invoice_id`, `event_id`

---

## 7) Deliverables / phases (high-level, implementation later)
Each phase should result in a tangible repo artifact (code structure, tests, docs), but **implementation can be deferred**. The goal now is to define the slice boundaries so you can generate project structure next.

### Phase 1 — Repo scaffold + quality rails
- Create `dotnet/` solution + project layout (library, sample API, unit tests, integration tests)
- CI-friendly `dotnet build` / `dotnet test`
- Basic linting/formatting conventions (minimal, not heavy)

### Phase 2 — Core contracts + in-memory store
- Storage interfaces + in-memory reference
- Idempotency key strategy (deterministic, bounded length)
- Minimal domain models + mapping

### Phase 3 — Checkout flows (happy path)
- Create Checkout Session for:
  - one-time payment
  - subscription
- Promo entry enabled
- Persist “intent records” (business IDs ↔ Stripe IDs)

### Phase 4 — Webhooks correctness + state convergence
- Verified webhook receiver (raw body)
- Dedupe by `event.id`
- Minimal event handlers that converge:
  - payment success/fail
  - subscription/invoice success/fail (document which events are in scope)
- Record processing outcomes (success/failure + last error)

### Phase 5 — Reconciliation endpoint (demo-only, extractable)
- `/reconcile` endpoint that replays missed events and repairs drift
- Design documented so extraction to HostedService/CLI is trivial

### Phase 6 — Observability baseline (OTel)
- Minimal OpenTelemetry SDK setup
- Log/trace correlation validated
- “How to run locally” documented

### Phase 7 — DB adapter sample (demo-only)
- Minimal DB schema + adapter in sample area
- Explicit disclaimer: demo/reference, not a production mandate

### Phase 8 — Documentation polish + packaging
- README: “how to integrate” (copy/paste friendly)
- “Design notes”: why Checkout-first, why promo-entry only, why no portal/connect
- Versioned release notes for v1

---

## 8) Explicit non-goals (v1)
- Customer Portal (placeholder + link)
- Connect / multi-account routing (`Stripe-Account`) (placeholder + link)
- Custom payment UI / PaymentIntents / Elements
- Default discount application (placeholder only)

---

## 9) Remaining open choices (to decide before implementation)
- DB technology for demo adapter: SQLite/Dapper vs EF Core (keep dependencies minimal)
- Local telemetry backend for demo: console exporter vs OTLP
- Whether “US bank payments” (e.g., ACH) is purely documented (recommended) or included as an optional sample extension (scope risk)

---

## 10) Documentation conventions (maintainability rule)
StripeKit.NET must remain “explainable.” Use three layers:

1) Module README (best ROI)
- Each module folder includes a short `README.md` describing:
  - module purpose
  - invariants / constraints
  - what failures it prevents

2) XML docs for public surface
- Public types/methods include `/// <summary>` and `<remarks>` where needed.

3) File header comment for critical internals
- Critical files (webhook verification/routing, reconciliation, idempotency factory) start with a short header comment:
  - Purpose
  - “Must not break” constraints
  - Pointer to docs/plan.md or module README

Keep these comments brief (3–8 lines). Avoid essay-style commentary inside methods.
