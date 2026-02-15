-- Demo-only schema for DbStripeKitStore.
-- This is a sample adapter baseline and not a production migration strategy.

create table if not exists customer_mappings (
    user_id text not null primary key,
    customer_id text not null unique
);

create table if not exists webhook_events (
    event_id text not null primary key,
    started_at_utc text not null,
    succeeded integer null,
    error_message text null,
    recorded_at_utc text null
);

create table if not exists payment_records (
    business_payment_id text not null primary key,
    user_id text not null,
    status text not null,
    payment_intent_id text null,
    charge_id text null
);

create unique index if not exists ix_payment_records_payment_intent_id
    on payment_records (payment_intent_id);

create table if not exists subscription_records (
    business_subscription_id text not null primary key,
    user_id text not null,
    status text not null,
    customer_id text null,
    subscription_id text null
);

create unique index if not exists ix_subscription_records_subscription_id
    on subscription_records (subscription_id);

create table if not exists refund_records (
    business_refund_id text not null primary key,
    user_id text not null,
    business_payment_id text not null,
    status text not null,
    payment_intent_id text null,
    refund_id text null
);

create unique index if not exists ix_refund_records_refund_id
    on refund_records (refund_id);
