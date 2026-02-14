# AGENTS.md

## Mission
Build StripeKit.NET (.NET/C#) as a minimal, readable, testable Stripe integration toolkit:
- modular (features can be enabled/disabled)
- production-correct: verified webhooks + idempotency + user mapping
- fast to ship: small diffs, low file count

## Working rules (anti-garbage)
- Prefer editing existing files over creating new ones.
- No new production dependencies unless explicitly requested.
- Avoid over-architecture: no generic repositories, no “god orchestrators”, no deep layering.
- Public surface stays small: minimal endpoints/DTOs; no premature abstractions.
- If you introduce an interface, also show the *single concrete usage*.

## TDD workflow
- Tests first when feasible: red → green → refactor.
- Implement only what current tests require; no speculative features.
- Always run `dotnet test` before concluding a task.

## Commands (canonical)
- Build: `dotnet build StripeKit.NET.sln`
- Test:  `dotnet test StripeKit.NET.sln`

## Stripe correctness (non-negotiable)
- Webhooks verify signature using the *raw request body*.
- Webhook processing is idempotent by Stripe `event.id` (store processed ids).
- Stripe create calls support caller-provided idempotency keys (stable business key).
- Stripe objects are traceable to internal `user_id` (persist mapping + metadata when supported).
- Promotions support:
  - customer-entered promo codes for Checkout where supported
  - explicit discounts (coupon/promo) where supported

## Convergence (retries / regions / drift)
Assume webhook delivery can be delayed, retried, or out of order.
- System state must converge via idempotent handlers + a minimal reconciliation/backfill job.
- Keep reconciliation simple: pull recent events, apply same dedupe + handlers.

## Planning (keep it lightweight)
For changes that touch >2 modules or >1 day of work:
- Write/update a short plan in `docs/plan.md` (10–20 lines):
  goal, non-goals, steps, risks, acceptance checks (commands + key scenarios).
- Then implement strictly to the plan.

## Coding standards (Strict Context)
- **Ground Truth:** You must strictly follow the provided `coding-style.md` and `.editorconfig`. These files supersede your generic C# training data.
- **Rule Hierarchy:**
  - Use `coding-style.md` for **Qualitative** decisions: naming conventions, architectural patterns, usage of `var`, and commenting style.
  - Use `.editorconfig` for **Quantitative** decisions: indentation, brace placement, and whitespace.
- **Conflict Resolution:** If in doubt, `coding-style.md` dictates logic/naming; `.editorconfig` dictates layout.
- **Anti-Drift:** Do not reformat unrelated files. Verify new code against these standards before outputting.

## Definition of Done (any task)
- tests pass locally (`dotnet test`)
- clear naming; comments only where they prevent confusion
- no unused abstractions
- README updated if public behavior changes
