# Connector write budget, device pairing and device tokens

## Connector write budget (#151)

Writes (`POST`/`PUT`/`PATCH`/`DELETE` under `/api`, except the heartbeat below) made with a
connector credential get a per-credential token bucket on top of the per-user "api" policy: `Api:ConnectorWrites:Burst`
(20) to start, refilled at `Api:ConnectorWrites:PerMinute` (60). Either at 0 disables it, which is
how the integration suite opts out. Code: `Middleware/ConnectorWriteRateLimiting.cs`.

- **"Connector" means the request carries `ConnectorCredentialClaim`.** `ApiKeyMiddleware` adds it
  for API keys and `DeviceTokenMiddleware` for device tokens (`device:<grant id>`, #148); no
  interactive scheme does, which is the whole of "web app traffic is not affected".
- **It is in `GlobalLimiter`, chained after the email ceiling, not an endpoint policy.** An
  endpoint carries one `[EnableRateLimiting]` and "api" already holds it. The global limiter runs
  in addition.
- **`Retry-After` is rounded up** in the shared `OnRejected`. A token bucket refills at fractional
  intervals (60/7 per minute is 8.57 s); truncating told clients to come back before a token
  existed. `ConnectorWriteRateLimitTests.RetryAfter_RoundsAFractionalWaitUp` pins it.
- **The limits are in-process**, like every other budget here. More than one App Service instance
  multiplies them.
- **The heartbeat (`PUT /api/Connections/{id}`) is exempt** (#200), by
  `[ExemptFromConnectorWriteBudget]` on the action, which the partitioner reads off
  `GetEndpoint()` (routing runs before the limiter). A backfill on one key retries on `Retry-After`
  and takes each token as it refills, so a heartbeat sharing that bucket starves: three missed
  ones mark the bridge stale, the Moonraker notifier stops being suppressed, and its prints
  duplicate the bridge's. The per-user "api" policy still bounds it. Never put the attribute on a
  write that changes print data. `[DisableRateLimiting]` would not do: it also drops "api". The
  same PUT also registers a new connection, so first registration is exempt too. The bridge
  check-in (`PUT /api/bridges/me`) is deliberately **not** exempt; see `docs/agents/bridges.md`.

## Device pairing and device tokens (#148)

printlog-bridge (and later Home Assistant) pairs instead of pasting an API key. Code:
`Authentication/Devices/`, `Services/DeviceGrants/`, `Controllers/DeviceGrantsController.cs`.

- **Two tokens, on purpose.** The device runs Auth0's device flow itself (parameters from the
  anonymous `GET /api/device-grants/pairing`), against the Native app `DevicePairing:ClientId` and
  the separate audience `DevicePairing:Audience`, then exchanges that access token once at
  `POST /api/device-grants` for an API-issued token (`pld_` + 32 random bytes). Auth0 tokens carry
  no per-device identity and cannot be revoked before they expire, so per-device name, last used,
  immediate revoke and the bridge identity of printlog-bridge#103 all need the API's own row. The
  API proxies no polling, so there is no user-code endpoint to rate-limit or probe.
- **The pairing policy checks more than the JWT:** `azp` must be the pairing app, `iat` at most
  `MaxPairingTokenAgeMinutes` old, and the token must carry a `DeviceScopes` scope. Blank client
  or audience fails closed (discovery 503, pairing refused); issued device tokens keep working.
  `(UserId, DeviceId)` is unique: re-pairing a device rotates its token on the same row.
- **A pairing token never carries `offline_access`** (403 `offline_access_not_allowed`). One that
  does came with a refresh token, or from one (refreshed access tokens keep the scope), and that
  refresh token could mint fresh pairing tokens after the user revoked the device, silently
  re-pairing it. Revoking here cannot reach a refresh token in Auth0, so the API refuses them, and
  the Auth0 API for devices must have Allow Offline Access off.
- **Each pairing token pairs once** (403 `pairing_token_used`). `PairingTokenUses` holds a hash of
  its `jti`, or of the whole token when there is no `jti`, until the token's `exp` plus 5 minutes.
  It is spent in the same `SaveChanges` as the grant, so a refused pairing leaves it unspent, and
  the unique index decides a race on any instance. Each pairing deletes expired rows first (one
  indexed delete); there is no background job. **No exception, not even for the device it
  paired:** a replay would let whoever copied the approval and the device id mint a token that
  supersedes the real bridge's. A device whose reply was lost runs the device flow again (the
  re-pair grace below keeps it logging meanwhile). A save that committed but whose acknowledgment
  was lost (what an EF execution-strategy retry runs into) is recognized by the generated token
  being the grant's current one and answered as the success it was; that is the only
  "already spent" that succeeds.
- **A re-pair rotates the token pair it read, or reads again.** `TokenHash` and
  `PreviousTokenHash` are `[ConcurrencyCheck]`, so a racing re-pair or the new token's first use
  (which clears `PreviousTokenHash`) makes the rotation's update match no row. The pairing then
  checks whether the row still exists: if so it reads and saves again (up to 3 attempts), which
  keeps the token the device just used valid; if not, the user revoked it, and it answers 409
  `device_grant_revoked` and spends the token (recreating the row or leaving the token reusable
  would let an approval given before the revoke undo it).
- **Re-pair grace** (api#287): a re-pair keeps the replaced token valid (`PreviousTokenHash`)
  until the new token's first use or `DeviceGrantService.RepairGrace` (10 minutes), whichever
  comes first. A re-pair while a grace runs keeps the older token and its deadline (the token in
  between was never used). The lookup reads `TokenHash` first and `PreviousTokenHash` only on a
  miss; the first use of the new token clears the previous one in one conditional update. A
  revoke deletes the row, so both die together. **Known limit:** that first use is a read, then a
  separate conditional update, so a re-pair landing between the two can still rotate from the
  read's state (tracked as a follow-up; the concurrency tokens below catch the save, not the read).
- **A superseded token logs prints and sends heartbeats, nothing more**, enforced centrally in
  `DeviceTokenMiddleware`: it reaches only endpoints whose `[DeviceAccess]` scope is in
  `DeviceScopes.DuringRepairGrace` (`prints:write`, `connections:write`), and every other device
  endpoint answers 403 `device_token_superseded` (Users/me, every read, printer create and slots,
  file upload URLs, bridge check-in, the desired list with its sealed credentials).
  `DeviceSupersededScopeTests` walks every `[DeviceAccess]` endpoint with a superseded full-scope
  token and pins the reachable set: **widening `DuringRepairGrace` is a security decision.** A
  superseded heartbeat never changes `Connection.BridgeId` (`keepBridgeLink`): an existing link
  stays, so a bridge whose reply was lost keeps its connections, and a connection it creates gets
  none, so a copy of the old token can't attach connections to the bridge. The bridge treats the
  403 as a generic check-in problem until the grace ends (printlog-bridge follow-up).
- **Where a device token works is an allow-list.** `DeviceTokenMiddleware` (after
  `UseAuthentication`, before `ApiKeyMiddleware`) sets a principal only on an endpoint whose action
  carries `[DeviceAccess(scope)]` and only if the grant holds that scope; everywhere else it answers
  403 `device_endpoint_not_allowed` itself. It is a middleware rather than a policy because
  anonymous endpoints never run authorization and would serve a device-authenticated owner their
  private data. `DeviceAccessCoverageTests` walks every `/api` endpoint with a full-scope token and
  pins the allow-list: **adding `[DeviceAccess]` anywhere is a security decision and must update
  that list.** An allowed action must use the default policy; a scheme-pinned one drops the principal.
- **The token lookup is never cached.** One indexed read per device request (two for a token
  that isn't current), so a revoke (a row delete) is effective on the next request on every
  instance. Only the last-used throttle flag is in `IMemoryCache` (5 minutes).
- **The unknown-token limit counts distinct tokens per address** (`Api:InvalidDeviceTokenAttemptsPerMinute`,
  20, api#287): a token already counted in the minute always gets 401 `invalid_device_token`, a new
  one past the limit 429. Guessing is distinct tokens, so it is limited as before; one revoked token
  retried (bridges behind one NAT, or a collapsed client IP) keeps hearing "re-pair". Memory holds
  8-byte HMAC fingerprints under a per-process key, at most the limit per address, for a minute. It
  only picks 401 or 429 after the database refused the token; it never answers for the database
  (no negative cache, #193). **This only partly fixes api#287 item 4:** 20 distinct garbage tokens
  a minute from the same address (another client behind the NAT, or anyone when the client IP
  collapses to the front end's) still make a revoked bridge's first try with its own token a 429.
  Item 4 stays open with item 5 (idle expiry).
- **Revoke all:** `DELETE /api/device-grants` (`InteractiveUserOnly`) deletes every grant of the
  user in one transaction with exactly what a single revoke does (unlink connections, cascade the
  bridges, their discovered, desired and sealed rows), answers `{ "revoked": n }`, and emits
  `DeviceGrantRevoked` per grant with `Via=all`. Nothing in the API sees an Auth0 password change
  or "sign out everywhere", so nothing calls it automatically.
- **Pairing notice** (api#287): the `device-paired` outbox row is queued **in the pairing's own
  `SaveChanges`** (`DevicePairedCampaign.Queue`), with `DeviceGrant.PairingNoticeAt` and
  `PairingNoticeFor` set on the row being rotated. Always for a new device; a re-pair within the
  hour (`DeviceGrantService.PairingNoticeRepeatAfter`) of the last notice is throttled only when
  it is identical to what that notice described: `PairingNoticeFor` is a SHA-256 of the scopes,
  client id and address, so new scopes, another client or another address notices again. A
  throttled re-pair emits `DevicePairingNoticeThrottled` (`GrantId` only). PeriodKey is device,
  time and the fingerprint's prefix. A pairing that commits has its email queued; one whose row
  can't be queued fails. The snapshot in `Exposure` holds name, scopes, time and the client
  address from `PairingNoticeAddress.For(RemoteIpAddress)`: IPv4-mapped IPv6 shown as IPv4, and
  **left out** when private, shared (100.64/10), loopback, link-local or unspecified (that is the
  front end's address). `RemoteIpAddress` is the client only when the host's forwarded headers
  are on (`ASPNETCORE_FORWARDEDHEADERS_ENABLED`; Startup never calls `UseForwardedHeaders`);
  `DevicePairingForwardedAddressTests` pins both cases. The address is in the email body and the
  in-app message ("from IP address ..."), never the subject, telemetry or logs. Only the in-app
  notification (`DevicePairingNotifier`, when `PairedDevice.Noticed`) runs after the commit, best
  effort: a failure is logged, counted (the `DevicePairingNoticeFailed` event) and never fails the
  pairing. `device-paired` is the first **security notice**
  (`IEmailCampaign.IsSecurityNotice`): on unless `Email:Campaigns:device_paired:Enabled=false`, it
  ignores preferences (the master switch too), the in-app notice gate and the holdout, has no
  unsubscribe header or link, and is outside the frequency cap; `Email:Enabled`, dry run, the
  allow-list, the daily cap, verification and the suppression list still apply. The device name is
  the device's own text: it is never in the subject.
- Device principals are role `DeviceUser` (refused by `InteractiveUserOnly`, like `ApiUser`), carry
  `auth_method=device` (prints get `PrintSource.ApiKey`, as bridge prints always have) and
  `ConnectorCredentialClaim` `device:<id>`, so #151's budget applies unchanged.
- The `/api` error `code`s in `DeviceTokenErrors` are the bridge's contract: `invalid_device_token`
  (401) means "re-pair", and nothing else may use it; the per-address guess limit answers 429
  `device_token_throttled`. A 401 for a `pld_` bearer carries no `resource_metadata` (the remedy
  is pairing, not OAuth).
- Telemetry: `DevicePaired`/`DeviceRepaired`/`DeviceGrantRevoked` carry `GrantId`, `ClientId` and
  `Scopes`; `DeviceTokenRejected` carries `Reason` (and `GrantId` for a valid token). Never the
  token or its hash.
