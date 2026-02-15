# SampleStorage (Demo Adapter)

Purpose:
- Provide a minimal DB-backed implementation of StripeKit storage seams for the sample API.
- Keep the core library dependency-free while showing one concrete persistence path.

Must-not-break constraints:
- Webhook dedupe remains keyed by `event_id`.
- Business IDs remain the primary lookup keys for payment/subscription/refund records.
- Adapter behavior mirrors in-memory stores for save + lookup semantics.

What failures this prevents:
- Sample restarts losing in-memory state.
- Webhook replay drift when dedupe/outcome state is not persisted.

How to use:
1. Create tables using `samples/StripeKit.SampleApi/SampleStorage/schema.sql`.
2. Configure both settings:
   - `StripeKit:DbProviderInvariantName`
   - `StripeKit:DbConnectionString`
3. Start sample API; it switches from in-memory stores to `DbStripeKitStore`.

Notes:
- This is demo/reference code only, not a production migration strategy.
- Provider registration is environment-specific; ensure the ADO.NET provider is available at runtime.
