# MCP server — deployment & rollback

Operational record for the read-only MCP server (`/mcp`) added to `PrintLogApi`.

## Required configuration

| Key | Dev value | Prod value | Notes |
| --- | --- | --- | --- |
| `Auth0:Domain` | `dev-3dprintlog.auth0.com` | `3dprintlog.auth0.com` | Existing key. |
| `Auth0:ApiIdentifier` | `https://dev.3dprintlog.com/api` | `https://3dprintlog.com/api` | App audience. Unchanged. |
| `Auth0:McpIdentifier` | `https://dev.3dprintlog.com/mcp` | `https://3dprintlog.com/mcp` | **New.** Dedicated MCP audience — never accepted by the normal bearer scheme. |
| `Auth0Management:*` | — | — | Existing M2M client; now also needs `read:grants` + `delete:grants`. |
| `Mcp:RateLimitPerMinute` | `60` (default) | `60` (default) | Optional. Per-user HTTP request budget on `/mcp`. |

Integration tests set `Auth0:McpIdentifier=https://test.mcp` and a high rate limit.

## Auth0 tenant changes (see `docs/mcp-auth0-setup.md`)

1. **MCP API** (`PrintLog MCP`) with identifier `https://3dprintlog.com/mcp`, scope
   `read:printdata`, access-token lifetime **3600s**, offline access enabled.
2. **Public PKCE client** (`PrintLog AI Connector`, native, `token_endpoint_auth_method=none`,
   grant types `authorization_code` + `refresh_token`) with Claude/ChatGPT callback URLs.
   Registration model is **shared-client** → the UI exposes a single
   "Disconnect all AI agents" action.
3. **Management API M2M** app authorized for `read:grants` and `delete:grants`.

## Health checks after deploy

- `GET /.well-known/oauth-protected-resource` (referenced from the `/mcp` 401 challenge)
  returns JSON whose `resource` = the MCP identifier, `authorization_servers` = the tenant,
  and `scopes_supported` includes `read:printdata`.
- Unauthenticated `POST /mcp` → `401` with a `WWW-Authenticate: Bearer resource_metadata="…"` header.
- A valid MCP token: `tools/list` returns the seven tools + `ping`; a web-audience token is
  rejected by `/mcp`, and an MCP-audience token is rejected by an ordinary `[Authorize]` endpoint.

## Disabling `/mcp` without affecting the web API

The MCP endpoint is an isolated mapping (`endpoints.MapMcp("/mcp")`) with its own
`McpBearer`/`McpChallenge` schemes and the `McpAccess` policy. To disable it, remove (or
comment out) the `MapMcp("/mcp")…` line in `Startup.Configure` and redeploy — the normal API
and its default bearer scheme are untouched.

## Revoking access

- A single user: revoke `PrintLog AI Connector` under their Auth0 **Authorized Applications**,
  or use the in-app **Settings → Connected AI Agents → Disconnect all AI agents**
  (calls `DELETE /api/connected-agents/{grantId}`). Already-issued access tokens remain valid
  until expiry (≤ 1 hour).
- All users / kill switch: disable the MCP API in Auth0 (rejects new tokens) and/or remove the
  `/mcp` mapping as above.

## Rollback

The MCP work is additive: new files under `PrintLogApi/Mcp`, `PrintLogApi/Authentication`
(`McpUser*`), the connected-agents controller/service methods, and the `MapMcp` line. To roll
back, revert the feature commits and redeploy; no schema migrations were introduced. The Auth0
MCP API and connector client can be left in place (they are inert without the endpoint) or
deleted.
