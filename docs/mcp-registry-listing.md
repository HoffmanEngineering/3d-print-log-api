# MCP Registry and directory listing runbook

How the MCP server (`https://api.3dprintlog.com/mcp`) gets into the official MCP Registry, the Claude
Connectors Directory, and the ChatGPT plugin directory (#128). Everything the repo can do is done:
`server.json`, its validation, the publish job, the tool annotations, and the ChatGPT
domain-verification endpoint. What is left needs your accounts, your DNS, or a public submission,
so it is listed here as steps. Research current as of **2026-10-06**.

`docs/mcp-discoverability.md` is the broader plan (tier-2 directories, community channels, listing
copy). This file is the part that has to be done exactly.

## What is in the repo

| Piece | Where | Guarded by |
| --- | --- | --- |
| Registry metadata | `server.json` (repo root) | `ServerJsonTests` validates it against the vendored schema, offline |
| Registry schema | `PrintLogApi.IntegrationTests/Mcp/Registry/server.schema.2025-12-11.json` | `DeclaredSchema_IsTheVendoredSchema` fails if `$schema` moves without it |
| Publish job | `publish-mcp-registry` in `.github/workflows/deploy.yml` | Off unless `vars.MCP_REGISTRY_PUBLISH == 'true'` |
| Tool annotations | `PrintLogReadTools` / `PrintLogWriteTools` / `PrintLogDocsTools` | `ToolSchemaTests.EveryTool_*` |
| ChatGPT domain check | `GET /.well-known/openai-apps-challenge` | `OpenAiAppsChallengeTests`; 404 until configured |

**The server name is `com.3dprintlog/printlog`.** #128 proposed `com.3dprintlog/mcp`; the existing
name was kept because the UI's server card (`/.well-known/mcp/server-card.json`, from
`3d-print-log-ui` `scripts/discovery-lib.mjs`) already publishes it, and a registry name is
permanent once used. `printlog` names the product, which reads better in a client's server list than
a second "mcp".

**The description is the 97-character one the UI card uses.** The registry caps `description` at
100 characters, and the original 230-character text would have been rejected at publish time.
The longer copy belongs in the directory listings, which allow 2,000 (Claude) and 4,000 (ChatGPT).
Listing version 1.1.0 changed it to mention the docs tools (#129).

**The anonymous docs server, `https://api.3dprintlog.com/mcp/docs`, is not a second registry
entry.** It serves a subset of this server's tools (the docs ones, which `/mcp` also has), so a
client that installs this listing already gets the docs. A `remotes` entry would be wrong: clients
treat every remote as the same server, and one that picked `/mcp/docs` would never see the data
tools. The UI's server card advertises it as a second endpoint instead.

## Versioning

`server.json`'s `version` is the version of the **listing**, not of the API.

- Registry versions are immutable: a version cannot be republished, and nothing in it can be
  edited ([versioning](https://modelcontextprotocol.io/registry/versioning)).
- The registry stores no tool list. An API release that adds or changes tools changes nothing the
  registry holds, so it needs no new registry version.
- **Bump `version` in the same PR that changes anything in `server.json`**, and change
  `MCP_SERVER_VERSION` (plus `MCP_DESCRIPTION`, if it changed) in the UI's
  `scripts/discovery-lib.mjs` to match. Use plain `MAJOR.MINOR.PATCH`; `ServerJsonTests` rejects a
  prerelease suffix, because the registry sorts `1.0.1-1` below `1.0.1` and it would never become
  "latest".

The publish job runs after every production deploy (`v*` tag) and handles the rest:

| Registry state for `name`@`version` | Job does |
| --- | --- |
| Not published | Publishes, then verifies it can read the new version back |
| Published, identical to `server.json` | Nothing; the step passes |
| Published, different from `server.json` | **Fails**, naming the fix: bump the version |

So a bump reaches the registry with the next API release, after the server it describes is live.
The failing case exists because skipping it would silently drop a metadata change. A failure there
leaves production deployed (the job runs after `deploy`); fix `server.json` and cut a patch release.

If the registry ever starts adding fields to what it returns, the comparison fails on every release.
The error output shows the `diff`; strip the new field in the job's `jq '.server'` filter.

## Owner steps

Do these in order. Steps 1–5 are the registry, which the directories and several aggregators
(PulseMCP, Smithery, the GitHub/VS Code `@mcp` gallery) read from.

### 1. Generate the DNS key pair

Domain namespaces (`com.3dprintlog/*`) are proved by DNS or HTTP, not by GitHub. GitHub OIDC
(`mcp-publisher login github-oidc`) only grants `io.github.<owner>/*`, so it cannot be used here
([authentication](https://modelcontextprotocol.io/registry/authentication)).

Run this where the key will be stored (OpenSSL 3; Git Bash on Windows is fine), not in the repo
(`key.pem` is gitignored anyway):

```bash
openssl genpkey -algorithm Ed25519 -out key.pem
PUBLIC_KEY="$(openssl pkey -in key.pem -pubout -outform DER | tail -c 32 | base64)"
echo "3dprintlog.com. IN TXT \"v=MCPv1; k=ed25519; p=${PUBLIC_KEY}\""
PRIVATE_KEY="$(openssl pkey -in key.pem -noout -text | grep -A3 "priv:" | tail -n +2 | tr -d ' :\n')"
echo "${PRIVATE_KEY}"   # 64 hex characters: this is the secret for step 3
```

Store `key.pem` in your password manager. Anyone with it can publish **and overwrite** anything
under `com.3dprintlog/*`.

> Alternative: an Azure Key Vault ECDSA P-384 key
> (`mcp-publisher login dns azure-key-vault --domain 3dprintlog.com --vault <vault> --key <key>`)
> keeps the private key out of GitHub entirely, at the cost of an `azure/login` step with
> `id-token: write` and a federated credential. Worth it if the key ever needs rotating; the job
> as written uses the simpler secret.

### 2. Add the TXT record at Namecheap

Namecheap → `3dprintlog.com` → Advanced DNS → Add new record:

| Type | Host | Value | TTL |
| --- | --- | --- | --- |
| TXT | `@` | `v=MCPv1; k=ed25519; p=<PUBLIC_KEY>` | Automatic |

- **It must be on the apex (`@`)**, not `_mcp-registry` or any other selector; the registry only
  reads the apex and fails with a generic signature error otherwise.
- Other apex TXT records (SPF, site verification) stay; multiple TXT records are fine.
- On a key rotation, **delete the old record**. A stale one is tried first and makes verification
  fail.

Check propagation:

```bash
dig +short TXT 3dprintlog.com | grep MCPv1
```

Then record it in `3d-print-log-infra` (its README lists the hand-made Namecheap records). DNS for
the apex is not in Terraform, so the README entry is the record of it.

### 3. Create the `mcp-registry` environment and secret

GitHub → `3d-print-log-api` → Settings → Environments → New environment `mcp-registry`:

- **Deployment branches and tags:** Selected → add tag rule `v*`. Only release tags can reach it.
- **Required reviewers:** yourself. Each publish then waits for an approval, like `production`.
- **Environment secret:** `MCP_REGISTRY_PRIVATE_KEY` = the 64-hex-character value from step 1.

Store it on the environment, **not** as a repository secret. A repository secret is readable by
any workflow on any branch; the environment secret is readable only by a job that passes the
environment's rules ([securing the token in CI](https://modelcontextprotocol.io/registry/github-actions)).
Create the environment **before** enabling the job: GitHub silently auto-creates a missing
environment with no protection rules.

### 4. Publish the first version by hand (optional, recommended)

Doing the first publish locally surfaces any DNS problem before it becomes a red release:

```bash
# from the repo root; mcp-publisher from https://github.com/modelcontextprotocol/registry/releases
mcp-publisher validate                 # read-only check against the live registry
mcp-publisher login dns --domain 3dprintlog.com --private-key "${PRIVATE_KEY}"
mcp-publisher publish
mcp-publisher logout
```

The release job then finds the version already published and identical, and passes. (Done
2026-10-10 for `1.1.0`.)

### 5. Enable the publish job

GitHub → Settings → Secrets and variables → Actions → **Variables** → New repository variable
`MCP_REGISTRY_PUBLISH` = `true`.

Unset it (or set anything else) to turn publishing off again; the job is skipped, not failed.

To prove the job works without cutting a release, re-run just that job from the latest `v*`
deploy run. Upstream jobs are not re-run, so nothing is redeployed, and the re-run reads the
variable's current value:

```bash
gh run list --workflow deploy.yml --limit 1                  # the run id
gh run view <run-id> --json jobs --jq '.jobs[] | {name, databaseId}'
gh run rerun <run-id> --job <publish-mcp-registry job id>    # then approve mcp-registry
```

Against an unchanged `server.json` it logs "already published and unchanged" and skips the login,
so it does **not** exercise `MCP_REGISTRY_PRIVATE_KEY`; the first real publish after a version bump
is what does.

### 6. Verify the listing

```bash
curl -s "https://registry.modelcontextprotocol.io/v0.1/servers?search=com.3dprintlog/printlog" | jq
curl -s "https://registry.modelcontextprotocol.io/v0.1/servers/com.3dprintlog%2Fprintlog/versions/latest" | jq '.server'
```

The first returns one server (#128's original check, `?search=3dprintlog`, matches too). The
second should equal `server.json`. Then rescan with Ora; the "MCP server / manifest" check reads
the registry.

### 7. Directory prerequisites (both directories)

Both directories list a server for users who cannot paste an OAuth client ID, and production
Auth0 is set up for exactly that (shared public client, DCR off; `docs/mcp-auth0-production.md`).
Each directory needs a client identity of its own first:

| | Claude | ChatGPT |
| --- | --- | --- |
| Accepted | DCR, CIMD, or Anthropic-held client credentials | CIMD, DCR, or a predefined OAuth client |
| Recommended here | **Anthropic-held credentials** (`oauth_anthropic_creds`) | **Predefined client** |
| Auth0 app | New *Regular Web Application* (confidential), Authorization Code + Refresh Token, rotation on | Same, a separate app |
| Callback URL | `https://claude.ai/api/mcp/auth_callback` | `https://chatgpt.com/connector_platform_oauth_redirect` (or the per-connector `https://chatgpt.com/connector/oauth/{callback_id}` the dashboard shows, if Auth0's `iss` response parameter is not detected) |
| Hand over | Email `mcp-review@anthropic.com` with the `client_id`; they reply with how to send the secret | Entered in the OpenAI Platform dashboard |

Both recommendations keep DCR off. DCR would register a new Auth0 application on every fresh
connection, which Anthropic itself warns against for directory traffic
([authentication](https://claude.com/docs/connectors/building/authentication)). Grant both apps
the `3D Print Log MCP` API with `read:printdata` and `write:printdata`. The **Resource Parameter
Compatibility Profile** tenant setting is already required and stays on.

Smoke-test each app before handing it over: open
`https://3dprintlog.auth0.com/authorize?response_type=code&client_id=<id>&redirect_uri=<callback>&scope=openid%20offline_access%20read:printdata%20write:printdata&resource=https://api.3dprintlog.com/mcp`,
sign in, copy `code` from the directory's error page, and exchange it at `/oauth/token` with the
secret. The access token's `aud` must include `https://api.3dprintlog.com/mcp`, its `scope` both
data scopes, and the response must carry a refresh token. `invalid_client` means the app's token
endpoint auth method (Post vs Basic) is wrong; missing login options mean its connections are not
enabled.

Also needed by both:

- **A reviewer test account per directory**: a real Auth0 username/password login (no 2FA, no
  social login), never shared between directories or with the Google Play reviewer account,
  because reviewers use the write tools. OpenAI rejects a submission whose account needs sign-up
  or 2FA. Populate each with `scripts/seed-reviewer-account.py` and an API key created on that
  account, then revoke the key. The script also sets the currency and electricity rate, without
  which every cost tile reports `RateMissing`.
- **You have run every tool yourself** (both portals ask you to attest to it), through
  MCP Inspector or as a custom connector.
- **Privacy policy**: `https://www.3dprintlog.com/docs/privacy-policy`. OpenAI requires it to cover
  categories of personal data, purposes, recipients, retention and user controls. As of
  2026-10-10 it does not: it covers logs, analytics, email and ad cookies, but not account data,
  its processors, retention or deletion, nor AI connectors. Fix it in `3d-print-log-ui` before the
  ChatGPT submission.
- **Terms of service** (ChatGPT only): an HTTPS URL. The site has no terms page today; it needs one
  before the ChatGPT submission.
- **Docs**: `https://www.3dprintlog.com/docs/mcp`. **Support**: `hello@3dprintlog.com`.
- **Icon**: `docs/assets/mcp-icon-512.png` (512×512 PNG). It is an opaque disc on transparency, so
  the same file serves as ChatGPT's dark-theme logo.

All the paste-ready copy for steps 8 and 9 (descriptions, example prompts, test cases) is in
`mcp-directory-listing-copy.md`.

### 8. Claude Connectors Directory

Submit at **https://claude.ai/directory/manage** → *Submit new* → *MCP connector*. Any paid Claude
plan can submit; the earlier Team/Enterprise requirement noted in `mcp-discoverability.md` no longer
applies ([submission](https://claude.com/docs/connectors/building/submission)).

- **Connection:** `https://api.3dprintlog.com/mcp`, universal URL.
- **Tools:** synced from the live server. Every tool carries a `title` and explicit
  `readOnlyHint`/`destructiveHint`/`idempotentHint`/`openWorldHint`, so none should be flagged.
- **Listing:** name `3D Print Log` (≤100), one-liner (≤200), description (≤2,000), 1–5
  categories, docs/privacy URLs, support contact, icon, and the **URL slug, which is permanent**.
- **Authentication:** Anthropic-held client credentials (step 7).
- **Data handling:** first-party API; no health data; no sponsored content.
- **Test & launch:** the reviewer account and how to connect.
- **Compliance:** seven acknowledgments, all required.

Run the [pre-submission checklist](https://claude.com/docs/connectors/building/review-criteria)
first. It rejects catch-all tools, descriptions that steer the model, and generic error text; the
server has none of these, but the checklist is what reviewers apply. Submissions are scanned and
listed as *Community* connectors by default; status appears in the same portal. Escalations:
`mcp-review@anthropic.com`.

### 9. ChatGPT plugin directory

The app directory became the **plugin directory** in July 2026; an MCP server is submitted as a
plugin that contains it. Submit at **https://platform.openai.com/plugins**
([submission](https://developers.openai.com/plugins/deploy/submission),
[guidelines](https://developers.openai.com/apps-sdk/app-submission-guidelines)).

1. **Verify the organization** (individual or business) in the OpenAI Platform's organization
   settings. You need the Owner role or *Apps Management Write*.
2. **Domain verification.** The dashboard issues a token and fetches
   `https://api.3dprintlog.com/.well-known/openai-apps-challenge` (the API's origin, not the
   website's). Set the App Service setting `Mcp__OpenAiAppsChallengeToken` to the token, restart,
   and check:

   ```bash
   curl -s https://api.3dprintlog.com/.well-known/openai-apps-challenge   # prints the token
   ```

   Leaving the setting in place afterwards is harmless; removing it returns the path to 404.
3. **Connect the server** with the predefined client from step 7.
4. **Listing:** display name (≤30), short description (≤30), long description (≤4,000),
   category, logo and dark logo, website/support/privacy/terms URLs.
5. **Review materials:** 5 positive test cases (prompt + the tool expected to run), 3 negative
   cases the server should decline, the reviewer credentials, and a demo video URL. Example
   prompts are in `mcp-discoverability.md` ("Reusable listing copy").
6. Resolve the automated checks, attest, submit. Feedback arrives by email; one active review at
   a time.

OpenAI's tool rules are stricter than Anthropic's on one point: `readOnlyHint`, `destructiveHint`
and `openWorldHint` must be **explicit booleans on every tool**, read tools included.
`ToolSchemaTests.EveryTool_StatesEveryBehaviorHintExplicitly` pins that.

## Sources

- MCP Registry: [quickstart](https://modelcontextprotocol.io/registry/quickstart),
  [authentication](https://modelcontextprotocol.io/registry/authentication),
  [GitHub Actions](https://modelcontextprotocol.io/registry/github-actions),
  [versioning](https://modelcontextprotocol.io/registry/versioning),
  [remote servers](https://modelcontextprotocol.io/registry/remote-servers),
  [official registry requirements](https://github.com/modelcontextprotocol/registry/blob/main/docs/reference/server-json/official-registry-requirements.md),
  [schema 2025-12-11](https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json)
- Claude: [submission](https://claude.com/docs/connectors/building/submission),
  [review criteria](https://claude.com/docs/connectors/building/review-criteria),
  [authentication](https://claude.com/docs/connectors/building/authentication)
- ChatGPT: [plugin submission](https://developers.openai.com/plugins/deploy/submission),
  [submission guidelines](https://developers.openai.com/apps-sdk/app-submission-guidelines),
  [auth](https://developers.openai.com/apps-sdk/build/auth),
  [domain verification at the origin](https://community.openai.com/t/chatgpt-app-submissions-domain-verification-step-does-not-support-subpath-hosted-mcp-servers/1379021)
