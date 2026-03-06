# 1. Project summary

`StripeKit.NET` appears to be a small .NET 8 Stripe integration library plus a minimal ASP.NET Core sample API. The implemented scope is centered on hosted Stripe Checkout session creation, webhook verification and processing, local payment/subscription/refund state tracking, reconciliation of recent Stripe events, customer-to-Stripe mapping, and a demo DB-backed storage adapter. Evidence comes from the library code in `src/StripeKit`, the runnable sample in `samples/StripeKit.SampleApi`, and the unit/integration tests in `tests/`.

# 2. Implemented features

## Feature: Hosted Checkout session creation for one-time payments
- What it does: Creates Stripe Checkout sessions in `payment` mode, persists a local pending payment record, adds correlation metadata, and uses an idempotency key.
- Why it matters: Gives callers a concrete Stripe-hosted payment entry point while preserving local business IDs and replay safety.
- Evidence: `src/StripeKit/Checkout/StripeCheckoutSessionCreator.cs` (`CreatePaymentSessionAsync`, `BuildPaymentOptions`, `PersistPaymentSessionAsync`), `src/StripeKit/Checkout/StripeCheckoutSessionClient.cs`, `src/StripeKit/Payments/PaymentRecords.cs`, `samples/StripeKit.SampleApi/Program.cs` (`/checkout/payment`), `tests/StripeKit.Tests/Checkout/StripeCheckoutSessionCreatorTests.cs` (`CreatePaymentSessionAsync_UsesGeneratedIdempotencyKeyAndStoresRecord`).
- Confidence: high

## Feature: Hosted Checkout session creation for subscriptions
- What it does: Creates Stripe Checkout sessions in `subscription` mode, persists a local incomplete subscription record, adds metadata, and uses an idempotency key.
- Why it matters: Supports a subscription start flow without exposing direct Stripe SDK usage to callers.
- Evidence: `src/StripeKit/Checkout/StripeCheckoutSessionCreator.cs` (`CreateSubscriptionSessionAsync`, `BuildSubscriptionOptions`, `PersistSubscriptionSessionAsync`), `src/StripeKit/Billing/SubscriptionRecords.cs`, `samples/StripeKit.SampleApi/Program.cs` (`/checkout/subscription`), `tests/StripeKit.Tests/Checkout/StripeCheckoutSessionCreatorTests.cs` (`CreateSubscriptionSessionAsync_UsesGeneratedIdempotencyKeyAndStoresRecord`).
- Confidence: high

## Feature: Promotion support for Checkout
- What it does: Supports customer-entered promo codes via `AllowPromotionCodes` and backend-supplied coupon or promotion-code discounts, with a policy hook and persisted promotion outcome/IDs.
- Why it matters: Covers common Stripe Checkout discount paths while keeping business-rule checks injectable.
- Evidence: `src/StripeKit/Checkout/CheckoutSessions.cs` (`StripeDiscount`, `PromotionValidationResult`, `IPromotionEligibilityPolicy`), `src/StripeKit/Checkout/StripeCheckoutSessionCreator.cs` (`EvaluatePromotionAsync`, `CreateBaseOptions`, `ApplyDiscount`), `src/StripeKit/Payments/PaymentRecords.cs`, `src/StripeKit/Billing/SubscriptionRecords.cs`, `tests/StripeKit.Tests/Checkout/StripeCheckoutSessionCreatorTests.cs` (`CreatePaymentSessionAsync_InvalidPromotion_ReturnsNoSession`, `CreatePaymentSessionAsync_WithBackendCoupon_PersistsPromotionData`, `CreateSubscriptionSessionAsync_WithBackendPromotionCode_PersistsPromotionData`).
- Confidence: high

## Feature: Deterministic idempotency key generation
- What it does: Builds stable idempotency keys from scope + business ID, and hashes overlong business IDs to stay within Stripe's key length limit.
- Why it matters: Prevents duplicate create operations across retries while keeping keys deterministic.
- Evidence: `src/StripeKit/Core/IdempotencyKeyFactory.cs`, `tests/StripeKit.Tests/Core/IdempotencyKeyFactoryTests.cs`.
- Confidence: high

## Feature: Customer mapping and get-or-create Stripe customer resolution
- What it does: Stores `user_id` <-> Stripe `customer_id` mappings and can create a Stripe customer with user metadata when no mapping exists.
- Why it matters: Preserves correlation between internal users and Stripe customer objects across flows.
- Evidence: `src/StripeKit/Core/ICustomerMappingStore.cs`, `src/StripeKit/Core/InMemoryCustomerMappingStore.cs`, `src/StripeKit/Core/StripeObjectLookup.cs` (`StripeCustomerResolver`), `samples/StripeKit.SampleApi/Program.cs` (DI for `ICustomerMappingStore`, `IStripeCustomerResolver`), `tests/StripeKit.Tests/Core/InMemoryCustomerMappingStoreTests.cs`, `tests/StripeKit.Tests/Checkout/StripeCheckoutSessionCreatorTests.cs` (`CreatePaymentSessionAsync_WithoutCustomerId_UsesResolvedCustomerId`).
- Confidence: medium

## Feature: Webhook signature verification from raw payload
- What it does: Validates Stripe webhook signatures before parsing the event and expects the exact raw request payload.
- Why it matters: Stripe webhook trust depends on verifying the unmodified raw request body.
- Evidence: `src/StripeKit/Webhooks/WebhookSignatureVerifier.cs` (`EventUtility.ValidateSignature`), `samples/StripeKit.SampleApi/Program.cs` (`request.EnableBuffering`, raw body read, `/webhooks/stripe`), `tests/StripeKit.Tests/Webhooks/WebhookSignatureVerifierTests.cs`, `tests/StripeKit.IntegrationTests/Webhooks/WebhookEndpointRawBodyContractIntegrationTests.cs`.
- Confidence: high

## Feature: Idempotent webhook processing by Stripe event ID
- What it does: Uses an event store to lease/start processing, record outcomes, treat successful prior deliveries as terminal duplicates, and allow retries for failed or stale in-progress events.
- Why it matters: Stripe retries webhook deliveries; safe dedupe is foundational for correctness.
- Evidence: `src/StripeKit/Webhooks/IWebhookEventStore.cs`, `src/StripeKit/Webhooks/InMemoryWebhookEventStore.cs`, `src/StripeKit/Webhooks/StripeWebhookProcessor.cs` (`TryHandleDuplicateAsync`), `samples/StripeKit.SampleApi/SampleStorage/DbStripeKitStore.cs` (`TryBeginAsync`, `RecordOutcomeAsync`, `GetOutcomeAsync`), `tests/StripeKit.Tests/Webhooks/InMemoryWebhookEventStoreTests.cs`, `tests/StripeKit.Tests/Webhooks/StripeWebhookProcessorTests.cs` (`ProcessAsync_DuplicateEvent_ReturnsRecordedOutcome`, `ProcessAsync_FirstAttemptFailed_SecondDeliveryRetriesAndSucceeds`, `ProcessAsync_StaleProcessingLease_AllowsLaterDeliveryToTakeOver`), `tests/StripeKit.IntegrationTests/Webhooks/WebhookProcessingIntegrationTests.cs` (`VerifyAndDedupe_ValidSignature_RecordsOutcome`).
- Confidence: high

## Feature: Webhook-driven payment state updates
- What it does: Updates local payment records for `payment_intent.succeeded` and `payment_intent.payment_failed`, with fallback lookup/backfill behavior.
- Why it matters: Final payment state is updated from Stripe events rather than only from create-time assumptions.
- Evidence: `src/StripeKit/Webhooks/StripeWebhookProcessor.cs` (`ApplyEventAsync`, `PaymentWebhookApplicator`), `src/StripeKit/Payments/PaymentRecords.cs`, `tests/StripeKit.Tests/Webhooks/StripeWebhookProcessorTests.cs` (`ProcessAsync_PaymentIntentSucceeded_UpdatesPaymentStatus`, `CreatePaymentSessionAsync_NullStripeId_WebhookCorrelationByBusinessMetadata_UpdatesRecord`), `tests/StripeKit.IntegrationTests/Webhooks/WebhookProcessingIntegrationTests.cs` (`ProcessAsync_PaymentIntentSucceeded_UpdatesPaymentState`).
- Confidence: high

## Feature: Webhook-driven subscription state updates
- What it does: Updates local subscription records for subscription and invoice events and maps Stripe subscription statuses into a smaller local status model.
- Why it matters: Keeps internal subscription state aligned with Stripe billing events.
- Evidence: `src/StripeKit/Webhooks/StripeWebhookProcessor.cs` (`SubscriptionWebhookApplicator`, `TryMapSubscriptionStatus`), `src/StripeKit/Billing/SubscriptionRecords.cs`, `tests/StripeKit.Tests/Webhooks/StripeWebhookProcessorTests.cs` (`ProcessAsync_InvoicePaymentFailed_UpdatesSubscriptionStatus`, `ProcessAsync_SubscriptionOutOfOrderDeletedThenDelayedInvoiceSuccess_DoesNotReactivate`, `CreateSubscriptionSessionAsync_NullStripeId_WebhookCorrelationByBusinessMetadata_UpdatesRecord`), `tests/StripeKit.IntegrationTests/Webhooks/WebhookProcessingIntegrationTests.cs` (`ProcessAsync_InvoicePaymentFailed_UpdatesSubscriptionState`).
- Confidence: high

## Feature: Out-of-order event guards and status precedence
- What it does: Stores last-applied Stripe event timestamps and refuses stale regressions; equal timestamps use explicit status precedence.
- Why it matters: Stripe deliveries can arrive late or out of order; this reduces state rollback risk.
- Evidence: `src/StripeKit/Payments/PaymentRecords.cs` (`LastStripeEventCreated`), `src/StripeKit/Billing/SubscriptionRecords.cs` (`LastStripeEventCreated`), `src/StripeKit/Webhooks/StripeWebhookProcessor.cs` (`ShouldApplyPaymentStatus`, `ShouldApplySubscriptionStatus`, precedence helpers), `samples/StripeKit.SampleApi/SampleStorage/schema.sql` (`last_stripe_event_created_utc` columns), `tests/StripeKit.Tests/Webhooks/StripeWebhookProcessorTests.cs` (`ProcessAsync_PaymentOutOfOrderSucceededThenDelayedFailed_DoesNotRegress`, `ProcessAsync_SubscriptionOutOfOrder_EqualCreatedCanceledBeatsActive`), `tests/StripeKit.Tests/Webhooks/DbStripeKitStoreWebhookEventStoreTests.cs` (timestamp persistence/hydration tests).
- Confidence: high

## Feature: Checkout correlation fallback and backfill
- What it does: Writes business IDs into Checkout/session metadata and uses `checkout.session.completed` plus metadata/client-reference parsing to backfill missing Stripe object IDs into local records.
- Why it matters: Supports cases where create-time records exist before Stripe object IDs are known.
- Evidence: `src/StripeKit/Checkout/StripeCheckoutSessionCreator.cs` (`CreateMetadata`, `BuildPaymentOptions`, `BuildSubscriptionOptions`), `src/StripeKit/Webhooks/StripeWebhookEventData.cs` (metadata parsing), `src/StripeKit/Webhooks/StripeWebhookProcessor.cs` (`checkout.session.completed`, `BackfillCheckoutCorrelationAsync`), `tests/StripeKit.Tests/Checkout/StripeCheckoutSessionCreatorTests.cs` (`CreatePaymentSessionAsync_NullStripeId_WebhookCorrelationByBusinessMetadata_UpdatesRecord`, `CreateSubscriptionSessionAsync_NullStripeId_WebhookCorrelationByBusinessMetadata_UpdatesRecord`).
- Confidence: high

## Feature: Refund creation with local validation and persistence
- What it does: Creates Stripe refunds for succeeded payments only, validates user/payment consistency, applies idempotency, persists refund records, and updates refund status from webhook events.
- Why it matters: Refund flows are financially sensitive; local checks reduce accidental or mismatched refund attempts.
- Evidence: `src/StripeKit/Refunds/StripeRefundCreator.cs`, `src/StripeKit/Refunds/StripeRefundClient.cs`, `src/StripeKit/Refunds/RefundRecords.cs`, `samples/StripeKit.SampleApi/Program.cs` (`/refunds`), `tests/StripeKit.Tests/Refunds/StripeRefundCreatorTests.cs`, `tests/StripeKit.Tests/Webhooks/StripeWebhookProcessorTests.cs` (`ProcessAsync_RefundUpdated_UpdatesRefundStatus`), `tests/StripeKit.Tests/Webhooks/StripeEventReconcilerTests.cs` (`ReconcileAsync_RefundUpdated_UpdatesStatusAndEmitsWebhookTags`).
- Confidence: high

## Feature: Reconciliation of recent Stripe events
- What it does: Lists recent Stripe events, replays them through the same webhook processor, and returns counts for processed, duplicate, failed, last event ID, and pagination state.
- Why it matters: Provides a convergence/backfill path when webhook delivery or processing was missed or delayed.
- Evidence: `src/StripeKit/Webhooks/StripeEventReconciler.cs`, `samples/StripeKit.SampleApi/Program.cs` (`/reconcile`), `tests/StripeKit.Tests/Webhooks/StripeEventReconcilerTests.cs`, `tests/StripeKit.IntegrationTests/Webhooks/WebhookProcessingIntegrationTests.cs` (`ReconcileAsync_FirstAttemptFailed_ReplayAppliesOnceAfterRecovery`).
- Confidence: high

## Feature: Activity-based tracing and structured trace logging
- What it does: Emits `ActivitySource` spans and JSON trace logs with business and Stripe correlation tags across checkout, refunds, webhooks, and reconciliation.
- Why it matters: Payment debugging usually depends on joining local records, Stripe IDs, and request traces.
- Evidence: `src/StripeKit/Core/StripeKitOptions.cs` (`StripeKitDiagnostics`, `ActivitySourceName`, `EmitLog`), `src/StripeKit/Checkout/StripeCheckoutSessionCreator.cs`, `src/StripeKit/Refunds/StripeRefundCreator.cs`, `src/StripeKit/Webhooks/StripeWebhookProcessor.cs`, `src/StripeKit/Webhooks/StripeEventReconciler.cs`, `samples/StripeKit.SampleApi/Program.cs` (`ActivityTrackingOptions`), `tests/StripeKit.Tests/Checkout/StripeCheckoutSessionCreatorTests.cs` (`CreatePaymentSessionAsync_EmitsCorrelationTags`, `CreatePaymentSessionAsync_EmitsStructuredLogWithCorrelationFields`), `tests/StripeKit.Tests/Refunds/StripeRefundCreatorTests.cs` (`CreateRefundAsync_EmitsCorrelationTags`), `tests/StripeKit.Tests/Webhooks/StripeEventReconcilerTests.cs` (`ReconcileAsync_EmitsRunActivityWithTotals`).
- Confidence: high

## Feature: In-memory and demo DB-backed storage implementations
- What it does: Provides in-memory stores in the library and a sample `DbStripeKitStore` implementing all persistence seams through ADO.NET.
- Why it matters: Makes the core library usable without infrastructure while showing one persistence path for sample deployments.
- Evidence: `src/StripeKit/Core/InMemoryCustomerMappingStore.cs`, `src/StripeKit/Payments/PaymentRecords.cs`, `src/StripeKit/Billing/SubscriptionRecords.cs`, `src/StripeKit/Refunds/RefundRecords.cs`, `src/StripeKit/Webhooks/InMemoryWebhookEventStore.cs`, `samples/StripeKit.SampleApi/SampleStorage/DbStripeKitStore.cs`, `samples/StripeKit.SampleApi/SampleStorage/schema.sql`, `samples/StripeKit.SampleApi/SampleStorage/README.md`, `tests/StripeKit.Tests/Webhooks/DbStripeKitStoreWebhookEventStoreTests.cs`.
- Confidence: high

# 3. Important integration flows

## Flow: Payment Checkout session creation
- Short description: Caller posts payment details, StripeKit validates input and promotion policy, resolves customer if configured, creates a Checkout session, and stores a pending payment record.
- Main components involved: `StripeCheckoutSessionCreator`, `ICheckoutSessionClient`, `IPaymentRecordStore`, `IStripeCustomerResolver`, `IPromotionEligibilityPolicy`.
- Evidence: `src/StripeKit/Checkout/StripeCheckoutSessionCreator.cs`, `src/StripeKit/Checkout/StripeCheckoutSessionClient.cs`, `samples/StripeKit.SampleApi/Program.cs` (`/checkout/payment`), `tests/StripeKit.Tests/Checkout/StripeCheckoutSessionCreatorTests.cs`.
- Whether it is a good candidate for a Mermaid diagram: yes

## Flow: Subscription Checkout session creation
- Short description: Caller posts subscription request data, StripeKit creates a subscription-mode Checkout session, stores a local incomplete subscription record, and preserves business/subscription metadata for later webhook correlation.
- Main components involved: `StripeCheckoutSessionCreator`, `ISubscriptionRecordStore`, `ICheckoutSessionClient`.
- Evidence: `src/StripeKit/Checkout/StripeCheckoutSessionCreator.cs`, `src/StripeKit/Billing/SubscriptionRecords.cs`, `samples/StripeKit.SampleApi/Program.cs` (`/checkout/subscription`), `tests/StripeKit.Tests/Checkout/StripeCheckoutSessionCreatorTests.cs`.
- Whether it is a good candidate for a Mermaid diagram: yes

## Flow: Verified webhook intake and deduplicated processing
- Short description: Sample API reads the raw request body, verifies the Stripe signature, checks event dedupe state, parses event data, applies handlers, records outcome, and returns duplicate/conflict responses based on processing state.
- Main components involved: sample `/webhooks/stripe` endpoint, `WebhookSignatureVerifier`, `StripeWebhookProcessor`, `IWebhookEventStore`, payment/subscription/refund applicators.
- Evidence: `samples/StripeKit.SampleApi/Program.cs` (`/webhooks/stripe`), `src/StripeKit/Webhooks/WebhookSignatureVerifier.cs`, `src/StripeKit/Webhooks/StripeWebhookProcessor.cs`, `tests/StripeKit.IntegrationTests/Webhooks/WebhookEndpointRawBodyContractIntegrationTests.cs`, `tests/StripeKit.Tests/Webhooks/StripeWebhookProcessorTests.cs`.
- Whether it is a good candidate for a Mermaid diagram: yes

## Flow: Webhook correlation fallback after Checkout create
- Short description: If local records exist without Stripe object IDs, webhook metadata and `checkout.session.completed` backfill the missing Stripe IDs using stored business IDs.
- Main components involved: `StripeCheckoutSessionCreator`, `StripeWebhookEventData`, `PaymentWebhookApplicator`, `SubscriptionWebhookApplicator`.
- Evidence: `src/StripeKit/Checkout/StripeCheckoutSessionCreator.cs`, `src/StripeKit/Webhooks/StripeWebhookEventData.cs`, `src/StripeKit/Webhooks/StripeWebhookProcessor.cs`, `tests/StripeKit.Tests/Checkout/StripeCheckoutSessionCreatorTests.cs`.
- Whether it is a good candidate for a Mermaid diagram: yes

## Flow: Refund creation and refund-status convergence
- Short description: Caller requests a refund by business payment/refund IDs; StripeKit validates the local payment record, creates the Stripe refund idempotently, stores a refund record, and later updates refund status from refund webhook events.
- Main components involved: sample `/refunds` endpoint, `StripeRefundCreator`, `IRefundClient`, `IRefundRecordStore`, `StripeWebhookProcessor`.
- Evidence: `samples/StripeKit.SampleApi/Program.cs` (`/refunds`), `src/StripeKit/Refunds/StripeRefundCreator.cs`, `src/StripeKit/Refunds/StripeRefundClient.cs`, `src/StripeKit/Webhooks/StripeWebhookProcessor.cs`, `tests/StripeKit.Tests/Refunds/StripeRefundCreatorTests.cs`.
- Whether it is a good candidate for a Mermaid diagram: yes

## Flow: Event reconciliation/backfill
- Short description: Sample API triggers reconciliation, which lists recent supported Stripe events and replays them through the same webhook processor to update missing state while honoring duplicate semantics.
- Main components involved: sample `/reconcile` endpoint, `StripeEventReconciler`, `IStripeEventClient`, `StripeWebhookProcessor`.
- Evidence: `src/StripeKit/Webhooks/StripeEventReconciler.cs`, `samples/StripeKit.SampleApi/Program.cs` (`/reconcile`), `tests/StripeKit.Tests/Webhooks/StripeEventReconcilerTests.cs`, `tests/StripeKit.IntegrationTests/Webhooks/WebhookProcessingIntegrationTests.cs`.
- Whether it is a good candidate for a Mermaid diagram: yes

# 4. Payments / fintech credibility signals

## Signal: Stripe POST idempotency is explicit, deterministic, and caller-overridable
- Why it matters: Financial create calls are retry-prone; deterministic idempotency is a core payment-integration safeguard.
- Evidence: `src/StripeKit/Core/IdempotencyKeyFactory.cs`, `src/StripeKit/Checkout/StripeCheckoutSessionClient.cs`, `src/StripeKit/Refunds/StripeRefundClient.cs`, `src/StripeKit/Core/StripeObjectLookup.cs` (`StripeCustomerResolver`), `tests/StripeKit.Tests/Core/IdempotencyKeyFactoryTests.cs`, `tests/StripeKit.Tests/Refunds/StripeRefundCreatorTests.cs` (`CreateRefundAsync_ProvidedIdempotencyKey_UsesProvidedValue`).
- Should this be highlighted in the README: yes

## Signal: Webhook signature verification uses the raw request payload
- Why it matters: This is a known Stripe correctness requirement; mutating JSON before verification can invalidate signatures.
- Evidence: `samples/StripeKit.SampleApi/Program.cs` (`EnableBuffering`, raw body read), `src/StripeKit/Webhooks/WebhookSignatureVerifier.cs`, `tests/StripeKit.IntegrationTests/Webhooks/WebhookEndpointRawBodyContractIntegrationTests.cs`.
- Should this be highlighted in the README: yes

## Signal: Webhook dedupe distinguishes terminal success from retryable failure/in-progress
- Why it matters: Treating all duplicates as terminal can permanently drop recoverable events.
- Evidence: `src/StripeKit/Webhooks/InMemoryWebhookEventStore.cs`, `src/StripeKit/Webhooks/StripeWebhookProcessor.cs` (`IsTerminalDuplicate`, `CreateRetryableDuplicateOutcome`), `samples/StripeKit.SampleApi/SampleStorage/DbStripeKitStore.cs`, `tests/StripeKit.Tests/Webhooks/InMemoryWebhookEventStoreTests.cs`, `tests/StripeKit.Tests/Webhooks/StripeWebhookProcessorTests.cs`, `tests/StripeKit.IntegrationTests/Webhooks/WebhookProcessingIntegrationTests.cs`.
- Should this be highlighted in the README: yes

## Signal: Stale processing lease takeover exists in event stores
- Why it matters: A crashed worker can otherwise leave an event stuck in `processing` indefinitely.
- Evidence: `src/StripeKit/Webhooks/InMemoryWebhookEventStore.cs`, `samples/StripeKit.SampleApi/SampleStorage/DbStripeKitStore.cs`, `tests/StripeKit.Tests/Webhooks/InMemoryWebhookEventStoreTests.cs` (`TryBeginAsync_StaleProcessingLease_AllowsTakeover`), `tests/StripeKit.Tests/Webhooks/DbStripeKitStoreWebhookEventStoreTests.cs` (`DbStripeKitStore_TryBeginAsync_StaleProcessingLease_AllowsTakeover`).
- Should this be highlighted in the README: yes

## Signal: Payment/subscription state updates guard against out-of-order regressions
- Why it matters: Stripe events can be delayed or reordered; naive handlers can revert already-terminal state.
- Evidence: `src/StripeKit/Webhooks/StripeWebhookProcessor.cs` (`ShouldApplyPaymentStatus`, `ShouldApplySubscriptionStatus`), `src/StripeKit/Payments/PaymentRecords.cs`, `src/StripeKit/Billing/SubscriptionRecords.cs`, `tests/StripeKit.Tests/Webhooks/StripeWebhookProcessorTests.cs`.
- Should this be highlighted in the README: yes

## Signal: Local records preserve internal business IDs and `user_id` alongside Stripe IDs
- Why it matters: Traceability between internal domain records and Stripe objects is important for support, reconciliation, and refunds.
- Evidence: `src/StripeKit/Checkout/StripeCheckoutSessionCreator.cs` (`CreateMetadata`), `src/StripeKit/Core/StripeMetadataMapper.cs`, `src/StripeKit/Core/StripeObjectLookup.cs` (`StripeCustomerResolver`), record types in `src/StripeKit/Payments/PaymentRecords.cs`, `src/StripeKit/Billing/SubscriptionRecords.cs`, `src/StripeKit/Refunds/RefundRecords.cs`.
- Should this be highlighted in the README: yes

## Signal: Reconciliation reuses the same webhook processor rather than a separate path
- Why it matters: Reusing the same dedupe + handler logic reduces drift between normal and backfill processing.
- Evidence: `src/StripeKit/Webhooks/StripeEventReconciler.cs` (`_processor.ProcessStripeEventAsync`), `tests/StripeKit.IntegrationTests/Webhooks/WebhookProcessingIntegrationTests.cs` (`ReconcileAsync_FirstAttemptFailed_ReplayAppliesOnceAfterRecovery`).
- Should this be highlighted in the README: yes

## Signal: Refund creation validates local ownership and payment success first
- Why it matters: Refunding the wrong payment or a non-succeeded payment is a material correctness risk.
- Evidence: `src/StripeKit/Refunds/StripeRefundCreator.cs`, `tests/StripeKit.Tests/Refunds/StripeRefundCreatorTests.cs` (`CreateRefundAsync_PaymentNotFound_Throws`, `CreateRefundAsync_PaymentNotSucceeded_Throws`, `CreateRefundAsync_PaymentUserMismatch_Throws`).
- Should this be highlighted in the README: yes

## Signal: Secrets/config are externalized, not hard-coded
- Why it matters: Payment credentials and webhook secrets should come from environment/config.
- Evidence: `samples/StripeKit.SampleApi/Program.cs` (`STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET` lookup), `samples/StripeKit.SampleApi/appsettings.json`.
- Should this be highlighted in the README: yes

## Signal: Raw-body contract is integration-tested, not only documented
- Why it matters: The repo contains executable proof for a subtle but important Stripe webhook requirement.
- Evidence: `tests/StripeKit.IntegrationTests/Webhooks/WebhookEndpointRawBodyContractIntegrationTests.cs`.
- Should this be highlighted in the README: yes

# 5. Engineering quality signals

## Signal: Narrow library surface organized by feature folders
- Evidence: `src/StripeKit/Core`, `src/StripeKit/Checkout`, `src/StripeKit/Webhooks`, `src/StripeKit/Refunds`, `src/StripeKit/Payments`, `src/StripeKit/Billing`.
- Why it matters for a hiring manager: Suggests deliberate modularity without excessive layering.

## Signal: Small explicit interfaces at external seams
- Evidence: `ICheckoutSessionClient`, `IRefundClient`, `IStripeEventClient`, `IStripeObjectLookup`, `ICustomerMappingStore`, `IWebhookEventStore`, `IPaymentRecordStore`, `ISubscriptionRecordStore`, `IRefundRecordStore`.
- Why it matters for a hiring manager: Shows testability and dependency isolation without a large abstraction graph.

## Signal: Configuration is typed and validated
- Evidence: `src/StripeKit/Core/StripeKitOptions.cs` (`Validate`), `samples/StripeKit.SampleApi/Program.cs` (binds `StripeKit:Modules` and validates), `tests/StripeKit.Tests/Core/StripeKitOptionsTests.cs`.
- Why it matters for a hiring manager: Indicates explicit operational invariants instead of undocumented configuration coupling.

## Signal: The sample app wires the same interfaces used in tests
- Evidence: `samples/StripeKit.SampleApi/Program.cs` DI registration for in-memory or DB store implementations; tests consume the same interfaces and linked sample DB store (`tests/StripeKit.Tests/StripeKit.Tests.csproj` includes `DbStripeKitStore.cs`).
- Why it matters for a hiring manager: Shows the design is meant to be swappable and exercised beyond one concrete environment.

## Signal: Deterministic tests target edge cases, not only happy paths
- Evidence: webhook duplicate, retry, stale lease, out-of-order timestamp, raw body mutation, null Stripe ID backfill, and refund guardrail tests across `tests/StripeKit.Tests` and `tests/StripeKit.IntegrationTests`.
- Why it matters for a hiring manager: Demonstrates awareness of failure modes common in real payment integrations.

## Signal: Observability concerns are centralized
- Evidence: `src/StripeKit/Core/StripeKitOptions.cs` centralizes activity source name, tags, and structured trace logging helpers.
- Why it matters for a hiring manager: Reflects intentional instrumentation rather than ad hoc logging scattered through flows.

## Signal: Demo DB adapter keeps the core dependency-light
- Evidence: `src/StripeKit/StripeKit.csproj` has only `Stripe.net`; DB code lives under `samples/StripeKit.SampleApi/SampleStorage/DbStripeKitStore.cs`.
- Why it matters for a hiring manager: Shows pragmatic boundary setting between reusable core code and sample infrastructure.

## Signal: Persistence schema mirrors domain invariants directly
- Evidence: `samples/StripeKit.SampleApi/SampleStorage/schema.sql` includes unique keys for `event_id`, `payment_intent_id`, `subscription_id`, and `refund_id`, plus explicit promotion and timestamp columns.
- Why it matters for a hiring manager: Suggests the author designed data layout around lookup and dedupe needs, not generic CRUD tables.

## Signal: Critical webhook/reconciliation files carry intent comments and plan references
- Evidence: header comments in `src/StripeKit/Webhooks/StripeWebhookProcessor.cs` and `src/StripeKit/Webhooks/StripeEventReconciler.cs`, plus decision log in `docs/plan.md`.
- Why it matters for a hiring manager: Indicates maintainability and explicit invariants in higher-risk code paths.

# 6. Proof assets

## Unit tests
- Present / partial / absent: present
- Evidence: `tests/StripeKit.Tests` contains focused tests for core helpers, checkout, refunds, webhook stores, webhook processor, and reconciler.
- Whether it is useful for the future README: yes

## Integration tests
- Present / partial / absent: present
- Evidence: `tests/StripeKit.IntegrationTests/Webhooks/WebhookProcessingIntegrationTests.cs`, `tests/StripeKit.IntegrationTests/Webhooks/WebhookEndpointRawBodyContractIntegrationTests.cs`.
- Whether it is useful for the future README: yes

## CI
- Present / partial / absent: absent
- Evidence: no `.github` directory in repo root.
- Whether it is useful for the future README: no

## Package/version metadata
- Present / partial / absent: partial
- Evidence: `src/StripeKit/StripeKit.csproj` defines target framework and `Stripe.net` dependency, but no package ID, version, authors, description, repository URL, or NuGet packaging metadata were found.
- Whether it is useful for the future README: yes

## Sample app
- Present / partial / absent: present
- Evidence: `samples/StripeKit.SampleApi/Program.cs`, `samples/StripeKit.SampleApi/appsettings.json`, `samples/StripeKit.SampleApi/SampleStorage/*`.
- Whether it is useful for the future README: yes

## Usage examples
- Present / partial / absent: partial
- Evidence: sample API endpoints in `samples/StripeKit.SampleApi/Program.cs`; there is also `samples/StripeKit.SampleApi/StripeKit.SampleApi.http`, but it still points to `/weatherforecast/` rather than current StripeKit endpoints.
- Whether it is useful for the future README: yes

## Screenshots
- Present / partial / absent: absent
- Evidence: no UI screenshots found; `docs/diagrams` contains image assets, but they are architecture/process diagrams rather than app screenshots.
- Whether it is useful for the future README: maybe

## Docs
- Present / partial / absent: present
- Evidence: `README.md`, `docs/plan.md`, `docs/requirement.md`, `docs/stripe-integration-best-practices.md`, `samples/StripeKit.SampleApi/SampleStorage/README.md`.
- Whether it is useful for the future README: yes

## Coverage reports
- Present / partial / absent: partial
- Evidence: `coverlet.collector` is referenced in both test projects, but no committed coverage output/report files were found.
- Whether it is useful for the future README: maybe

## Sample DB schema validation helper
- Present / partial / absent: present
- Evidence: `samples/StripeKit.SampleApi/SampleStorage/verify-schema.ps1`.
- Whether it is useful for the future README: yes

# 7. Safe README claims

1. `.NET 8 Stripe integration library with a minimal ASP.NET Core sample API.`
2. `Implements hosted Stripe Checkout flows for one-time payments and subscriptions.`
3. `Verifies webhook signatures before processing events.`
4. `Reads the raw webhook request body in the sample endpoint before signature verification.`
5. `Deduplicates webhook processing by Stripe event ID.`
6. `Allows retry of previously failed webhook events instead of treating every duplicate as terminal.`
7. `Includes stale-processing lease takeover logic in both in-memory and sample DB event stores.`
8. `Persists local payment, subscription, and refund records keyed by business IDs.`
9. `Uses deterministic idempotency keys for Checkout, refund, and customer creation flows.`
10. `Stores user_id metadata on Stripe objects where supported by the implemented flows.`
11. `Guards payment and subscription state against out-of-order Stripe events.`
12. `Backfills missing Stripe IDs from Checkout/webhook metadata when business IDs are available.`
13. `Includes a reconciliation flow that replays recent Stripe events through the same processor.`
14. `Exposes observability hooks via ActivitySource and structured trace logging.`
15. `Provides in-memory stores in the library and a demo ADO.NET-backed store in the sample app.`

# 8. Mermaid candidates

## Candidate 1
- Diagram type: sequence diagram
- What it would show: Payment Checkout creation from API request through local persistence and Stripe session creation.
- Why it helps: Explains the core "create checkout + save local record + keep business correlation" path quickly.
- Rough nodes/steps to include: Client -> Sample API `/checkout/payment` -> `StripeCheckoutSessionCreator` -> promotion policy -> customer resolver -> `StripeCheckoutSessionClient`/Stripe -> `IPaymentRecordStore` -> response with session URL/ID.

## Candidate 2
- Diagram type: sequence diagram
- What it would show: Webhook intake from raw body through signature verification, event dedupe, handler application, outcome recording, and HTTP response.
- Why it helps: Makes the repo's main correctness story visible without overselling it.
- Rough nodes/steps to include: Stripe -> `/webhooks/stripe` -> raw body read -> `WebhookSignatureVerifier` -> `IWebhookEventStore.TryBeginAsync` -> `StripeWebhookProcessor` -> payment/subscription/refund applicator -> `RecordOutcomeAsync` -> 200/409/400 response.

## Candidate 3
- Diagram type: flowchart
- What it would show: Reconciliation replay path and duplicate-safe convergence.
- Why it helps: Clarifies why reconciliation is not a separate code path and how retries/duplicates are counted.
- Rough nodes/steps to include: Trigger `/reconcile` -> `StripeEventReconciler` -> list recent supported events -> loop event -> `ProcessStripeEventAsync` -> duplicate? processed? failed? -> aggregate totals -> result payload.

# 9. Missing gaps

- No CI workflow files are present. A future README would be stronger with an actual build/test workflow badge source.
- `src/StripeKit/StripeKit.csproj` lacks package metadata such as package ID, version, description, authors, and repository URL.
- The sample HTTP file at `samples/StripeKit.SampleApi/StripeKit.SampleApi.http` still targets `/weatherforecast/` instead of the implemented StripeKit endpoints.
- There is no end-to-end local run guide for the current sample API routes beyond the existing README text.
- There is no committed example request/response payload set for `/checkout/payment`, `/checkout/subscription`, `/refunds`, `/webhooks/stripe`, and `/reconcile`.
- There is no screenshot or terminal transcript showing the sample API in use.
- The repo has architecture/process PNGs in `docs/diagrams`, but no concise diagram source files or markdown embeds for README reuse were found.
- There is no explicit list of supported public API types/methods for the library itself; the sample app is currently the clearest usage surface.
- Coverage tooling is referenced, but no published coverage summary/report artifact is present.
- The sample DB adapter is documented as demo-only, but there is no worked local setup example showing a concrete provider registration path.
