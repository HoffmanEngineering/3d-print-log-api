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

Chosen altitude: **core guards + a single-attempt-per-user checkout claim tracked on the existing `Subscription` row.** No separate checkout-attempt table, no reconciliation loop, no unique-when-non-null constraints — those remain out of scope. But an antagonistic review (see *Review incorporated* below) showed that the first draft's "deterministic Stripe idempotency key + non-atomic check-then-write" mechanism had correctness holes that fire even in single-user, sequential flows. This revision keeps the lightweight footprint (extra columns on one row) while making the concurrency and idempotency handling actually correct: serialization moves to an **atomic conditional claim** on the row, and idempotency uses a **per-attempt key**. Webhook event-ID dedup (vector 4's side-effect replay) remains deferred, but attempt-scoped activation now prevents stale completions from corrupting a newer attempt.

## Design

### 1. Live-subscription guard + reconciliation (closes vector 1)

At the top of `CreateCheckoutSession`, after loading the user's `Subscription` row: if `Status` is `Active` or `PastDue` **and** `StripeSubscriptionId` is non-empty, throw
`SubscriptionException("You already have an active subscription. Manage it from the billing portal.")`.

- `trialing` already maps to `Active` (`MapStripeStatus`), so trialing users are covered.
- `None` and `Canceled` fall through and may start a new checkout.
- Existing controller error handling surfaces `SubscriptionException` to the client; no controller change expected (verify during implementation).

**Reconciliation against stale local state (review finding 5).** The local row is written asynchronously by webhooks, which Stripe may deliver late, out of order, or retry for up to three days. So a live Stripe subscription can exist while the local row still reads `None`/`Canceled`. Before creating a checkout, if the row has a `StripeCustomerId` but no locally-live subscription, list that customer's subscriptions at Stripe (`status=all`, filtered to `active`/`trialing`/`past_due`). If a live one exists, adopt it into the local row (activate) and reject the checkout via the guard above instead of creating a second subscription. Brand-new users (no `StripeCustomerId`) skip this — they cannot have a Stripe subscription yet.

### 2. Customer reuse + idempotent creation (collapses vector 2 duplicate customers)

Continue reusing an existing `StripeCustomerId`. When creating a customer, pass
`new RequestOptions { IdempotencyKey = $"customer-{userId}" }` to `CustomerService.CreateAsync`.
This key is safe as a stable per-user constant: customer-create parameters (just the `userId` metadata) never vary, so Stripe never sees a same-key/different-params conflict. Two concurrent first-time requests converge on a single Stripe customer.

### 3. Single-attempt checkout claim (closes vectors 2 & 3's root cause)

Add these **nullable** columns to `Subscription` (additive, backwards-compatible migration):

| Column | Type | Notes |
|--------|------|-------|
| `PendingCheckoutSessionId` | `string` (255) | Stripe Checkout Session id, populated after the Stripe call |
| `PendingCheckoutSessionUrl` | `string` (2048) | Session URL returned to the client |
| `PendingCheckoutExpiresAt` | `DateTimeOffset?` | From the Stripe session's `ExpiresAt` |
| `PendingCheckoutIdempotencyKey` | `string` (255) | Per-attempt GUID; the Stripe session idempotency key |
| `PendingCheckoutPlanId` | `string` (64) | The `planId` this attempt is for |

The pending fields model **one open checkout attempt per user, regardless of plan.** An attempt is "open" while `PendingCheckoutIdempotencyKey` is set and `PendingCheckoutExpiresAt > UtcNow`.

`CreateCheckoutSession` flow after the guard (#1) and customer step (#2):

1. **Reuse (review finding 6):** If an open attempt exists **and** `PendingCheckoutPlanId == planId` → return the stored `PendingCheckoutSessionUrl`. Only reuse on an exact plan match, so a monthly-then-annual sequence never returns the wrong-plan URL.
2. **Atomic claim (review findings 1, 2, 4):** Generate a fresh attempt GUID. Claim the slot with a single conditional update — set `PendingCheckoutIdempotencyKey`, `PendingCheckoutPlanId`, and a **short-lease** `PendingCheckoutExpiresAt` (e.g. `now + 10 minutes`, not the full 24h) **only where** no open attempt currently exists (`PendingCheckoutIdempotencyKey IS NULL OR PendingCheckoutExpiresAt <= now`). Use EF Core `ExecuteUpdateAsync` so this is a real compare-and-swap that works on both SQL Server and SQLite (no `rowversion` needed). The short lease bounds the lockout window if this request dies before it stores a session (review finding 2): a crashed pre-session claim frees in ~10 minutes instead of 24h.
   - **0 rows affected** → another request holds an open attempt. Reload the row and go to step 1 (reuse its URL if plan matches; otherwise reject with `SubscriptionException("A checkout is already in progress. Complete or cancel it before starting another.")`). This is the conscious handling of the different-plan overlap — we serialize to one attempt rather than minting a second session.
   - **1 row affected** → we own the lease; continue.
3. Create the Stripe session with `new RequestOptions { IdempotencyKey = <the stored attempt GUID> }`. The key belongs to this specific attempt (not a `userId+planId` constant), so a genuinely new attempt always gets a fresh key — removing the first draft's defect where changed `successUrl`/`cancelUrl` would trigger a same-key/different-params error, or an expired-key window would return a dead completed-session URL. **On a Stripe failure (review finding 9), release the lease** with a conditional update (`WHERE PendingCheckoutIdempotencyKey = <ours>`) and rethrow, so the user can retry immediately rather than waiting out the lease. (We do not attempt same-key server-side retry/takeover — that is out of scope; the trade-off is that an ambiguous timeout where Stripe *did* create the session leaves an orphaned, never-surfaced session that expires unused in ~24h with no charge.)
4. Update the row with `PendingCheckoutSessionId` (= `session.Id`), `PendingCheckoutSessionUrl` (= `session.Url`), and the real `PendingCheckoutExpiresAt` (= `session.ExpiresAt`) **only where we still own the lease** (`WHERE PendingCheckoutIdempotencyKey = <ours>`). If that update affects **0 rows** (review finding 12 — the lease was completed by a webhook or reclaimed after expiry while Stripe was working), still return the valid URL to the user but emit `Subscription_CheckoutAttemptLostDuringCreate` for reconciliation rather than reporting clean success. Otherwise return the URL.

**Residual (review finding 7), accepted:** the reuse path in step 1 trusts `PendingCheckoutExpiresAt` without re-fetching the session, so a session that was completed or expired-early *before* its webhook lands could momentarily hand back a stale URL. This only bites in the narrow window between session close and webhook delivery; the next attempt (after expiry, or after attempt-scoped clearing in #4-webhook) recovers. If this proves noisy in telemetry, add a `Session.GetAsync` status check before reuse as a follow-up.

### 4. Attempt-scoped webhook activation (closes vector 3, hardens vector 4)

In `HandleCheckoutSessionCompleted`:

- **Atomic non-overwrite claim (review findings 1, 6).** The first draft's read-check-write is a TOCTOU race: two concurrent completions both read an empty `StripeSubscriptionId`, both pass a plain guard, and the second silently wins — and the alert never fires. Instead, write the subscription id with a conditional update that takes ownership only when the row is **not already owned by a *different live* subscription**:
  `ExecuteUpdateAsync ... WHERE StripeSubscriptionId IS NULL OR StripeSubscriptionId = @incoming OR (Status <> Active AND Status <> PastDue)`.
  The third clause is essential (review finding 1): a **canceled** row retains its old `StripeSubscriptionId` (`HandleSubscriptionDeleted`/`CancelSubscriptionImmediately` set `Status = Canceled` but never clear the id), so without it a legitimate re-subscription after cancellation would be rejected as a false duplicate and never activate. If **0 rows** are affected, a *different live* subscription genuinely owns the row: do not overwrite; **reload `AsNoTracking` and read the current DB `StripeSubscriptionId`** (review finding 6 — `ExecuteUpdate` does not refresh the tracked entity, which under a race still holds the pre-update value), then emit high-severity telemetry `Subscription_DuplicateActiveDetected` with `userId`, the reloaded existing id, and incoming id. This makes the alert reliable under concurrency.
- **Policy: alert only.** Do not auto-cancel or refund — with the pre-check (#1) and single-attempt claim (#3) this branch should almost never fire, and auto-cancellation risks reversing a legitimately intended change. Reconciliation/refund is manual, driven off the telemetry alert.
- **Attempt-scoped clear (review finding 3).** Stripe can redeliver or resend an old `checkout.session.completed` days later, possibly while a *newer* attempt is pending. Only clear the `Pending*` fields when the completed `session.Id == PendingCheckoutSessionId`. A completion for a session that is not the current pending attempt must **not** wipe the newer attempt's pending fields.
- **Map the real status (review finding 8).** The handler already fetches the Stripe subscription; set `Status = MapStripeStatus(stripeSubscription.Status)` rather than force-writing `Active`, so a completion for a non-`active` subscription (e.g. `incomplete`, `past_due`) records the truthful status.

> Note: fully fixing out-of-order `customer.subscription.updated`/`deleted` events that arrive before the completion (review finding 8, second half) is a broader pre-existing issue and is **out of scope** here — tracked as a follow-up. This design does not make it worse.

### 5. Testing

The concurrency guarantee now lives in the database compare-and-swap, so tests must exercise that — a fake Stripe dictionary alone would only prove the fake (review finding 9). Split the coverage:

- **a)** Re-checkout while `Active` (with a `StripeSubscriptionId`) is rejected with `SubscriptionException`.
- **b)** Sequential double-submit for the **same** plan (while an open attempt exists) returns the same URL and makes **no** second Stripe session call; a second submit for a **different** plan is rejected rather than returning the wrong URL.
- **c)** Reconciliation: a user with a `StripeCustomerId` and a live Stripe subscription but a stale `None` local row is adopted/rejected instead of creating a second subscription (fake Stripe returns a live subscription for the customer).
- **d)** Checkout CAS serialization: exercise the claim predicate directly with two `DbContext` instances — the first conditional `ExecuteUpdateAsync` claim affects 1 row, the second (same predicate) affects **0** rows while the lease is held. This proves the CAS is the serialization primitive without relying on real thread parallelism.
- **e)** Re-subscribe after cancellation (review finding 1): a row with `Status = Canceled` still holding `sub_old`, then a completion for `sub_new`, **activates** `sub_new` (the non-live clause lets it replace) and does **not** log a false duplicate.
- **f)** Webhook non-overwrite CAS: with a row already owned by a *different live* subscription, the completion claim affects **0** rows, the id is not overwritten, and exactly one `Subscription_DuplicateActiveDetected` event is emitted carrying the reloaded existing id and the incoming id.
- **g)** Webhook attempt-scoping: a **matching** completion (`session.Id == PendingCheckoutSessionId`) clears the pending fields (genuine red-green); a **non-matching** completion whose `session.Id` != current `PendingCheckoutSessionId` leaves a newer attempt's pending fields intact.
- **h)** Failure boundary (review findings 2, 9): a `CreateCheckoutSessionAsync` that throws releases the lease (pending fields cleared) so an immediate retry can claim again.

Assert telemetry via an in-memory `ITelemetryChannel` test sink (review findings 5, 6), not an unobservable `TelemetryClient`. The current code news up `new CustomerService()` / `new SessionService()` directly, which is not substitutable. Implementation must introduce a seam (inject Stripe service abstractions/factory) so a fake can be wired in.

**SQLite-vs-SQL-Server limitation (review findings 3, 4), documented:** the integration harness shares one open `SqliteConnection` across contexts, so it cannot faithfully reproduce true-parallel connection races. The tests above assert the CAS *logic* (1-vs-0 affected rows) deterministically, which is the invariant that matters; validating production connection-level concurrency would require a SQL Server-backed test and is out of scope for this SQLite harness.

## Out of scope

- Dedicated checkout-attempt table and reconciliation loop (bug report's full proposal). The single-attempt claim here reuses the existing row instead.
- Processed-Stripe-event-ID dedup table for side-effect replay (vector 4).
- Fully ordering-independent handling of `customer.subscription.updated`/`deleted` events (review finding 8, second half) — pre-existing, tracked separately.
- Unique-when-non-null constraints on `StripeCustomerId` / `StripeSubscriptionId`.
- Auto-cancel/refund of duplicate subscriptions.

## Migration & deployment notes

- The new columns are nullable and additive — backwards compatible per the deployment rule (old app version runs against the migrated DB).
- New EF migration via `dotnet ef migrations add AddPendingCheckoutFields --project=PrintLogApi`.
- **Mixed-version window (review finding 10), accepted as low-risk.** During the migration→deploy interval an old app instance ignores the pending/claim/idempotency invariants and its webhook still overwrites blindly. Given low traffic and a short pipeline window this is acceptable; the `Subscription_DuplicateActiveDetected` telemetry surfaces any anomaly created in that window for manual reconciliation. If a stronger guarantee is ever needed, gate checkout creation briefly during deploy.

## Review incorporated

This spec was revised after an antagonistic design review (Codex, 2026-07-10). Accepted and applied: findings 1, 3, 4, 5, 6, 8 (first half), 9 as correctness/robustness fixes; findings 2, 7, 10 as narrowed-and-documented residuals; finding 8 (event ordering) and finding 2's full auto-reconciliation deferred as out-of-scope follow-ups consistent with the chosen altitude. The central change: serialization moved from a deterministic Stripe idempotency key to an atomic per-user attempt claim (DB compare-and-swap) with a per-attempt idempotency key.
