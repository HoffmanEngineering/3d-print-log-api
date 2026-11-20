# Outbound webhooks

## Outbound webhooks (#235)

Users subscribe URLs (Home Assistant webhook triggers first) to `print.started`, `print.finished`
and `print.failed`. Code: `Webhooks/`, `Controllers/WebhooksController.cs`.

- **Two choke points, and every print transition must go through one.** Finished and failed come
  from `PrintFinishNotifier.NotifyIfJustFinished` (#166); started from `PrintStartNotifier`, called
  after the save on every path that creates a print in Printing or moves one into it. A new path
  that starts or finishes a print calls the matching notifier, or webhooks silently miss it.
- **The URL path can be the receiver's secret** (a Home Assistant webhook id). Logs, notifications
  and telemetry carry the host only: `WebhookTelemetryRedactionInitializer` cuts the dependency to
  `scheme://host:port/`, the named client has its logging handlers removed, and outbound requests
  carry no trace headers (no-output propagator on the webhook handler, and
  `SetComponentCorrelationHttpHeaders = false` for Application Insights). That second switch is
  process-wide but only stops the SDK's own headers; other clients still send the runtime's
  `traceparent`. The initializer recognizes a delivery by a request option or the event header.
- **SSRF is enforced at connect time** in the handler's `ConnectCallback`
  (`WebhookAddressGuard.ConnectAsync`); the check at save is only for a clear 400. Redirects and
  proxies are off. `Webhooks:AllowPrivateAddresses` is honored in Development only.
- **Secrets are encrypted, not hashed** (they sign), with `Webhooks:SecretEncryptionKeys`: the first
  encrypts, all decrypt. No key: endpoints 503 and deliveries are skipped. Removing a key makes its
  secrets `secret_unavailable` until rotated.
- **Delivery is in process.** Events queue in a `Channel`; `WebhookDeliveryService` fans each out
  into per-subscription deliveries and schedules single attempts: FIFO per subscription (a retry
  holds its lane, so `print.finished` never overtakes `print.started`), at most 32 in flight, 2 per
  user and 16 per host. **Keep the host cap well above the user cap**: every Home Assistant Cloud
  user posts to hooks.nabu.casa. The handler's connection pool follows the host cap. Capacity waits
  are per-key lists drained on release, not polling; a delivery that waits past `MaxDeliveryWait`,
  or finds the queue full, is recorded as `dropped`. **A drop is not a failure**: it is our
  capacity, so it never touches `ConsecutiveFailures`, the circuit or auto-disable, and its write is
  conditional on nothing newer being recorded. Drop writes go through one recorder loop (one at a
  time, coalesced per subscription), never one task per drop. Every attempt re-reads the
  subscription first. A circuit opens after 3 failures; one event claims the probe after the
  cooldown with a conditional update on `LastDeliveryAt`, and a successful test event closes it. A
  restart loses what is queued. Tests deliver synchronously through
  `WebhookTestFactory.DeliverPendingAsync`; the worker's own tests use a `SqliteFileDatabase` (WAL),
  because the shared in-memory connection is not thread-safe.
