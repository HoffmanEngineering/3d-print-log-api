# Design — Prevent overlapping/concurrent Stripe checkouts from double-charging users

**Bug report:** `docs/bughunt/high-stripe-overlapping-checkouts.md`
**Primary code:** `PrintLogApi/Services/SubscriptionService.cs`, `PrintLogApi/Models/Subscription.cs`, `PrintLogApi/PrintLogContext.cs`
**Date:** 2026-07-10

## Problem

`CreateCheckoutSession` performs a check-then-create sequence — a DB read, an external Stripe write, then a DB write — with no guard against an existing live subscription, no idempotency keys, and no serialization. `HandleCheckoutSessionCompleted` blindly overwrites `StripeSubscriptionId`. As a result a user can end up with multiple live Stripe subscriptions and recurring charges, with earlier paid subscriptions orphaned locally.

### Failure vectors (most to least likely)

1. **Re-subscribe while already Pro** — a user with an `Active` subscription starts checkout again. No check exists today; they get a second live subscription and the webhook orphans the first. Highest-probability double-charge, needs no concurrency.
2. **Impatient double-submit with no existing subscription** — two near-simultaneous requests both read `null`, both create a Stripe customer, both create a session. The unique `UserId` index rejects one local row, but both Stripe objects already exist. Double-charge only if the user completes both sessions.
3. **Webhook overwrite** — if two sessions complete, the second silently overwrites the first's `StripeSubscriptionId`, leaving a paid subscription unmanaged.
4. **Webhook replay** — Stripe delivers at-least-once; the current handler mostly re-writes the same values, so replays are largely harmless today (out of scope).

## Scope

Chosen altitude: **core guards + session-tracking on the existing `Subscription` row.** This closes vectors 1 and 3, collapses vector 2's duplicate customers, and meaningfully narrows vector 2's duplicate sessions — without the bug report's dedicated checkout-attempt table, reconciliation loop, or unique-when-non-null constraints, which add more new failure surface than this low-traffic billing flow justifies. Webhook event-ID dedup (vector 4) is explicitly deferred.

## Design

### 1. Live-subscription guard (closes vector 1)

At the top of `CreateCheckoutSession`, after loading the user's `Subscription` row: if `Status` is `Active` or `PastDue` **and** `StripeSubscriptionId` is non-empty, throw
`SubscriptionException("You already have an active subscription. Manage it from the billing portal.")`.

- `trialing` already maps to `Active` (`MapStripeStatus`), so trialing users are covered.
- `None` and `Canceled` fall through and may start a new checkout.
- Existing controller error handling surfaces `SubscriptionException` to the client; no controller change expected (verify during implementation).

### 2. Customer reuse + idempotent creation (collapses vector 2 duplicate customers)

Continue reusing an existing `StripeCustomerId`. When creating a customer, pass
`new RequestOptions { IdempotencyKey = $"customer-{userId}" }` to `CustomerService.CreateAsync`.
Two concurrent first-time requests then converge on a single Stripe customer.

### 3. Session serialization & reuse (narrows vector 2 duplicate sessions)

Add three **nullable** columns to `Subscription` (additive, backwards-compatible migration):

| Column | Type | Notes |
|--------|------|-------|
| `PendingCheckoutSessionId` | `string` (255) | Stripe Checkout Session id |
| `PendingCheckoutSessionUrl` | `string` (2048) | Session URL returned to the client |
| `PendingCheckoutExpiresAt` | `DateTimeOffset?` | From the Stripe session's `ExpiresAt` |

`CreateCheckoutSession` flow after the guard (#1) and customer step (#2):

1. If `PendingCheckoutSessionId` is set **and** `PendingCheckoutExpiresAt > DateTimeOffset.UtcNow` → return the stored `PendingCheckoutSessionUrl`. No Stripe call. (Handles refreshes and sequential double-clicks.)
2. Otherwise create the session with a **deterministic** idempotency key:
   `new RequestOptions { IdempotencyKey = $"checkout-{userId}-{planId}" }`.
   Truly-concurrent identical clicks send the same key, so Stripe returns **one** session.
3. Store `PendingCheckoutSessionId`, `PendingCheckoutSessionUrl` (= `session.Url`), and `PendingCheckoutExpiresAt` (= `session.ExpiresAt`) on the row and save.

**Why deterministic keys instead of an optimistic-concurrency claim:** provider-agnostic (integration tests run on SQLite, where SQL Server `rowversion` does not work cleanly), and needs no retry loop.

**Known residual gap (accepted):** two *concurrent, different-plan* checkouts could still mint two sessions — vanishingly unlikely, and the webhook guard (#4) catches the aftermath. Stripe retains idempotency keys ~24h, matching default Checkout Session expiry, so an expired attempt naturally frees its key.

On successful activation, `HandleCheckoutSessionCompleted` clears all three `Pending*` fields.

### 4. Webhook non-overwrite invariant (closes vector 3)

In `HandleCheckoutSessionCompleted`, before writing the incoming subscription id: if the row already has a non-empty `StripeSubscriptionId` that **differs** from the incoming `stripeSubscriptionId` **and** the existing `Status` is live (`Active` or `PastDue`):

- Do **not** overwrite the existing subscription fields.
- Emit high-severity telemetry `Subscription_DuplicateActiveDetected` with `userId`, the existing subscription id, and the incoming subscription id.
- **Policy: alert only.** Do not auto-cancel or refund — with the new pre-check (#1) this branch should almost never fire, and auto-cancellation risks reversing a legitimately intended change. Reconciliation/refund is manual, driven off the telemetry alert.

Otherwise proceed as today (activate, clear `Pending*` fields).

### 5. Testing

Integration tests with a fake/substituted Stripe client proving:

- **a)** Re-checkout while `Active` (with a `StripeSubscriptionId`) is rejected with `SubscriptionException`.
- **b)** Sequential double-submit (second call while a non-expired pending session exists) returns the **same** session URL and makes no second Stripe session call.
- **c)** Concurrent same-plan submits produce exactly **one** Stripe customer and **one** session (deterministic idempotency keys).
- **d)** `HandleCheckoutSessionCompleted` with a second, different live subscription id does **not** overwrite the existing `StripeSubscriptionId` and emits `Subscription_DuplicateActiveDetected`.

The current code uses `new CustomerService()` / `new SessionService()` directly, which is not substitutable in tests. Implementation must introduce a seam (e.g. inject Stripe service abstractions/factory) so the fake client can be wired in. This refactor is part of the work.

## Out of scope

- Dedicated checkout-attempt table and reconciliation loop (bug report's full proposal).
- Processed-Stripe-event-ID dedup table (vector 4).
- Unique-when-non-null constraints on `StripeCustomerId` / `StripeSubscriptionId`.
- Auto-cancel/refund of duplicate subscriptions.

## Migration & deployment notes

- The three new columns are nullable and additive — backwards compatible per the deployment rule (old app version runs against the migrated DB).
- New EF migration via `dotnet ef migrations add AddPendingCheckoutFields --project=PrintLogApi`.
