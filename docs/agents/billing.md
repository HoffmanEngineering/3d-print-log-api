# Stripe billing

## Stripe billing

Checkout, the billing portal and the inbound webhook: `Controllers/SubscriptionController.cs`,
`Services/SubscriptionService.cs`, settings in `Models/Stripe/StripeOptions.cs`.

- **Return URLs come from configuration, never from the request.** Stripe's success, cancel and
  portal return URLs are built on `Email:WebBaseUrl`. The request's `Origin` is used only when it
  exactly matches an entry in `Stripe:AllowedReturnOrigins` (empty by default; for Development, where
  a UI worktree runs on its own port), and the `Host` header never is. A caller can send any
  `Origin`, and a genuine, branded Stripe checkout that ends on an attacker's page is a phishing aid.
  With no usable `Email:WebBaseUrl` both endpoints answer 503 rather than guess. Entries that are
  not an http(s) origin (a path, a query, user info) stop the app at startup in every environment.
- **A blank `Stripe:WebhookSecret` turns the webhook off (503), and never stops the host.**
  Stripe.net 50.x accepts a payload signed with an empty key when the secret is empty, so a blank
  secret would let anyone post billing events. The webhook checks before it reads the body and logs
  one warning per refused request; the service refuses a blank secret too. In Production,
  `StripeWebhookSecretCheck` also writes one Critical entry at startup. Failing startup was
  considered and rejected: the webhook already fails closed, so it adds no protection, and a missing
  app setting would turn a deploy into a full API outage. Development, E2ETesting,
  IntegrationTesting and staging run without Stripe and get no Critical entry. The integration
  suite has a test-only secret in `appsettings.IntegrationTesting.json`.
- **Production must have** the App Service settings `Stripe__WebhookSecret` (the endpoint's
  `whsec_…` from the Stripe Dashboard) and `Stripe__SecretKey`, and `Email:WebBaseUrl`
  (`https://www.3dprintlog.com` from `appsettings.json`, unless overridden). Without the webhook
  secret the API still runs, but every Stripe event gets 503 until it is set (Stripe retries for up
  to three days). Nothing needs to be set for `Stripe:AllowedReturnOrigins`.
- **`checkout.session.completed` stores the retrieved subscription's own status** through
  `MapStripeStatus`, and sends the "activated" notice only when that is Active. Stripe neither orders
  nor deduplicates deliveries, so the event can arrive after the subscription was canceled, and
  nothing afterwards would correct a hard-coded Active. Only `active` and `trialing` map to Active;
  any other or unknown status maps to one that grants nothing.
- **`customer.subscription.updated` re-reads the subscription too** (`GET /v1/subscriptions/{id}`)
  and ignores the event's snapshot. An "active" snapshot retried, delivered late or resent after
  `customer.subscription.deleted` used to restore Pro for good. It looks up the local row first, so
  an event for a subscription the API never stored costs no Stripe call. A test that posts this
  event must install `FakeStripe` and join `StripeClientCollection`, as `SpoolmanConnectionsTests`
  does.
- **The webhook body is capped at 1 MiB** (`MaxWebhookBodyBytes`): it is read before its signature
  can be checked, on an anonymous route exempt from rate limiting. TestServer does not enforce
  request size limits, so `StripeWebhookTests` pins the endpoint metadata instead.
- **Tests that stub Stripe** set the process-wide `StripeConfiguration.StripeClient` through
  `Billing/FakeStripe.cs` and must be in `StripeClientCollection` (not parallel); `Install()`
  returns a scope that restores the default client. Nothing may call the real Stripe API.
