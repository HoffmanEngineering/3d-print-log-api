# MCP — Production Auth0 Configuration

Everything needed to stand up the MCP server against the **production** Auth0 tenant
(`3dprintlog.auth0.com`). Companion to `docs/mcp-deployment.md` (app settings + rollout) and the
dev-tenant quickstart in `README.md`.

## The one value that breaks everything

`Auth0:McpIdentifier` **must be the MCP endpoint URL, character-for-character**:

```
https://api.3dprintlog.com/mcp
```

It is not an abstract audience string, despite looking like one. It is used twice
(`Startup.cs`): as the JWT `Audience`, and as the `resource` advertised in the RFC 9728
protected-resource metadata. Per RFC 9728 a client compares the advertised `resource` against the
URL it connected to and **refuses to connect if they differ** — so an "audience-shaped" value like
`https://3dprintlog.com/mcp` (the website host, not the API host) fails against every
spec-compliant client, with no useful error.

Note the path has **no `/api` prefix**. Controllers carry `[Route("api/[controller]")]`
individually; the MCP endpoint is mapped straight onto the host root:

```csharp
endpoints.MapMcp("/mcp")...   // → https://api.3dprintlog.com/mcp
```

**An API identifier is immutable in Auth0.** Getting this wrong means deleting and recreating the
resource server. Confirm it before you click create.

## 1. API (Resource Server)

| Setting | Value |
| --- | --- |
| Name | `3D Print Log MCP` |
| Identifier | `https://api.3dprintlog.com/mcp` |
| Signing algorithm | RS256 |
| Permission | `read:printdata` — "Read your 3D print data" |
| Access-token lifetime | 3600s |
| Allow Offline Access | **ON** |
| RBAC | **OFF** |

**RBAC stays off, deliberately.** `read:printdata` is a *consent boundary*, not a privilege tier.
With RBAC on, the permission must be individually assigned to every user — there is no
self-service surface for that in Auth0 — and an unassigned user gets a token Auth0 issues happily
but `/mcp` rejects, which is a miserable failure to diagnose. Authorization is enforced
server-side regardless: MCP bearer + `read:printdata` + a mapped internal user (the `McpAccess`
policy) + creator-only ownership on every tool.

**Offline Access on**, or clients cannot obtain a refresh token and will re-prompt constantly.

The existing `https://3dprintlog.com/api` resource server is unrelated. Leave it alone.

## 2. Application (public PKCE client)

| Setting | Value |
| --- | --- |
| Name | `PrintLog AI Connector` |
| Type | **Native** |
| Token Endpoint Auth Method | **None** |
| Grant types | `Authorization Code` + `Refresh Token` only |
| Refresh Token Rotation | On, with reuse detection |

Uncheck Implicit, Client Credentials, and Password. Native + auth-method `none` is what permits
the secret-less PKCE flow; set to anything else, Auth0 demands a client secret and the flow fails
with an error that does not name the cause.

Users paste the **client ID** into their MCP client. Both Claude Code
(`--client-id`) and the claude.ai web connector (*Add custom connector → Advanced settings → OAuth
Client ID*) accept it, so **DCR and CIMD are not needed** — leave Dynamic Client Registration
disabled.

### Allowed Callback URLs

```
https://claude.ai/api/mcp/auth_callback,
http://localhost:8400/callback,
http://127.0.0.1:8400/callback,
http://localhost:8401/callback,
http://127.0.0.1:8401/callback
```

- **No wildcards, and none are possible.** Auth0 permits a wildcard only in the *subdomain*
  (`https://*.example.com`), never in a port or path — `http://localhost:*/callback` is invalid.
  Auth0 also does not implement the RFC 8252 loopback rule (authorization servers "MUST allow any
  port" for `127.0.0.1` redirects); matching is exact. So every loopback port must be
  pre-registered.
- **Register both loopback spellings.** `localhost` and `127.0.0.1` are different strings to
  Auth0's matcher. Claude Code currently sends `localhost`, which violates RFC 8252 §7.3
  (anthropics/claude-code#42765). If that is fixed it will start sending `127.0.0.1`, and an app
  registering only the `localhost` form breaks for **every CLI user at once**, with nothing having
  changed on our side. Registering both is free insurance.
- **8401 is the documented fallback** for users who already have 8400 bound. Without a second
  registered port they have no recourse, because they cannot add callback URLs themselves.
- **Do not add MCP Inspector's `http://localhost:6274/oauth/callback`.** That is a debugging tool;
  it belongs in the dev tenant only and needlessly widens the production callback surface.

Hosted clients (claude.ai web/desktop/mobile) never use loopback — they redirect to their own
fixed HTTPS endpoint, the same for every user.

## 3. Tenant setting (easy to miss)

Enable the **Resource Parameter Compatibility Profile**. MCP clients send the RFC 8707 `resource`
parameter rather than Auth0's proprietary `audience`. Without this, Auth0 ignores `resource` and
mints a token for the wrong audience — `/mcp` then returns 401 while every setting *looks* correct.

## 4. Management API (existing M2M app)

Authorize it for `read:grants` and `delete:grants` — these back the "Disconnect all AI agents"
action in Settings.

## 5. Verify before announcing

```bash
curl -s https://api.3dprintlog.com/.well-known/oauth-protected-resource | jq
```

- `resource` **exactly** equals `https://api.3dprintlog.com/mcp` (the URL clients connect to).
- `authorization_servers` points at `https://3dprintlog.auth0.com/`.
- `scopes_supported` contains `read:printdata`.

If `resource` does not match, stop — no compliant client will connect, and the fix means
recreating the API.

Then connect end-to-end **twice**, because the two paths exercise different callback URLs:

```bash
# Claude Code (loopback callback)
claude mcp add --transport http printlog https://api.3dprintlog.com/mcp \
  --client-id <CLIENT_ID> --callback-port 8400
```

and once through claude.ai web → *Add custom connector* → Advanced settings → OAuth Client ID
(hosted callback).

> Claude Code caches the discovered OAuth client per server in `~/.claude/.credentials.json` under
> `mcpOAuth`. `claude mcp remove` does **not** clear it — delete the entry by hand when
> reconfiguring, or you will keep re-testing the old client.
