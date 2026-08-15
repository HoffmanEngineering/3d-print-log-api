# MCP Server Discoverability — Submission Plan

Working document for getting the PrintLog MCP server (`https://api.3dprintlog.com/mcp`) listed
wherever agents and their users look for MCP servers. Research current as of **2026-07-29**.

Server facts every submission needs:

| Field | Value |
| --- | --- |
| Endpoint | `https://api.3dprintlog.com/mcp` |
| Transport | Streamable HTTP (stateless) |
| MCP revision | 2026-07-28 (SDK 2.0.0) |
| Auth | OAuth 2.1 via Auth0, shared-client registration, RFC 9728 resource metadata |
| Scopes | `read:printdata`, `write:printdata` |
| Tools | 23 (11 read, 12 write) |
| Repo | https://github.com/HoffmanEngineering/3d-print-log-api (public) |
| Registry namespace | `com.3dprintlog` (domain-based; we own `3dprintlog.com`) |
| Registry server name | `com.3dprintlog/printlog` |

### Which repo owns what

The user-facing assets every submission asks for are **website concerns, not API concerns** — none of
them belong in `PrintLogApi`:

| Asset | Lives in | Status |
| --- | --- | --- |
| Docs page (`/docs/mcp`) | `3d-print-log-ui` | ✅ shipped |
| Privacy policy (`/docs/privacy-policy`) | `3d-print-log-ui` | ✅ written, on branch `docs/mcp-privacy-policy` |
| `llms.txt` | `3d-print-log-ui` (`src/llms.txt`) | ✅ MCP section added, same branch |
| Icon | `3d-print-log-api` `docs/assets/mcp-icon-512.png` | ✅ generated |
| Registry DNS TXT record | DNS provider | ❌ needs account access |
| `server.json` | `3d-print-log-api` (repo root) | ✅ drafted |
| Tool titles + annotations | `3d-print-log-api` | ✅ done, test-pinned |

The API's only remaining obligation is `server.json` and keeping the tool surface annotated. It
serves no submission asset itself.

---

## Progress checklist

- [x] **Tool annotations** — every tool advertises a `title` plus `readOnlyHint`/`destructiveHint`.
      Pinned by `ToolSchemaTests.EveryTool_AdvertisesATitle` and
      `EveryTool_DeclaresReadOnlyOrDestructiveIntent`, so a new tool added with a bare
      `[McpServerTool]` fails the build rather than silently disqualifying the server.
- [x] **`server.json`** drafted at the repo root.
- [x] **Public docs page** — already shipped at **https://3dprintlog.com/docs/mcp**
      (`3d-print-log-ui`, `documentation/docs/docs-mcp/`). Covers what the assistant can and can't
      do, example questions, the endpoint URL and shared OAuth Client ID, per-client connect steps
      for Claude / Claude Code / ChatGPT, and how to revoke access. This satisfies the
      `documentation URL` requirement as-is — no new page needed.
- [x] **`llms.txt`** — already existed at `src/llms.txt` (and was already wired into
      `angular.json` assets, so it ships to `https://www.3dprintlog.com/llms.txt`). Extended with an
      **MCP server** section naming the endpoint, the OAuth-not-API-key auth model, the tool
      categories, and the creator-only/no-delete guarantees, plus links to `/docs/mcp` and
      `/docs/privacy-policy`. An agent reading `llms.txt` now learns the MCP server exists.
- [x] **Privacy policy — MCP section written** (branch `docs/mcp-privacy-policy`). Added: what
      account data is stored and which processors handle it (Auth0, Azure); a
      **Connecting an AI Assistant (MCP)** section covering opt-in authorization, what is and isn't
      sent (no photos or comments), creator-only scope, create/edit-but-never-delete, that **the AI
      provider's own policy governs the data once it arrives** including possible model training,
      immediate revocation via Settings → Connected AI Agents, and exactly what telemetry we keep
      (tool name, outcome, duration, irreversible account hash — never request contents); a
      **Data Retention and Deletion** section using the real 24-hour deactivation behaviour; and a
      **Contact Us** section with `hello@3dprintlog.com`. Also fixed two pre-existing boilerplate
      defects: a stray `www.website.com` and a "consult this list" reference to an advertising-partner
      list that did not exist.
- [x] **Icon** — `docs/assets/mcp-icon-512.png`, a 512×512 transparent PNG rasterized from the
      square brand mark (`src/assets/3d_brand_logo_b833b3f20dd.svg`, viewBox 2002×2002). The only
      pre-existing raster icon was `apple-touch-icon.png` at 180×180, which is below what most
      directories want.
- [ ] **Reviewer test account** with a populated print history, printers, and filament inventory.
      Needs a real Auth0 account and seeded data — see "What I can't do" below.
- [ ] Official MCP Registry publish
- [ ] Anthropic Connectors Directory
- [ ] ChatGPT plugin directory
- [ ] Tier-2 directories
- [ ] Niche 3D-printing channels

### Reusable listing copy

- **Name:** 3D Print Log
- **Tagline (≤55):** `Track your 3D prints, printers, and filament` (44 chars)
- **Short description:** Track 3D prints, printers, filament inventory, and projects on
  3dprintlog.com. Search your print history, check what filament you have left before starting a
  print, log finished prints, and review per-printer success rates.
- **Example prompts** (Anthropic wants ≥3 that exercise *different* tools):
  1. "Do I have enough black PLA left for a 240 g print?" (`find_material`)
  2. "What's my success rate on the Bambu X1C over the last three months?" (`get_printer_stats`)
  3. "Log the Benchy I just finished on the Prusa — 42 g of grey PETG, 2 hours." (`create_print`)

---

## Tier 1 — highest leverage

### 1. Official MCP Registry

`https://registry.modelcontextprotocol.io` — metadata-only; it does not host anything. **This is the
upstream feed.** Smithery, PulseMCP, Docker Hub, Anthropic, and GitHub all ingest it on a cadence,
so one publish here propagates to several downstream surfaces. Still in preview: breaking changes
and data resets are possible, so expect to re-publish.

Use **domain-based auth** to claim `com.3dprintlog` rather than GitHub auth (`io.github.*`) — the
reverse-DNS namespace reads as first-party and isn't tied to a personal GitHub account.

`server.json` (already at the repo root):

```json
{
  "$schema": "https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json",
  "name": "com.3dprintlog/printlog",
  "title": "3D Print Log",
  "description": "Track 3D prints, printers, filament inventory, and projects on 3dprintlog.com. …",
  "repository": {
    "url": "https://github.com/HoffmanEngineering/3d-print-log-api",
    "source": "github"
  },
  "version": "1.0.0",
  "remotes": [
    { "type": "streamable-http", "url": "https://api.3dprintlog.com/mcp" }
  ]
}
```

Notes on the shape:
- Only `remotes` — no `packages`, since there is no npm/PyPI/stdio distribution. A remote server
  MUST be publicly reachable at the given URL; the registry checks.
- Do **not** add an `sse` remote. SSE is deprecated in the spec and the server is stateless
  Streamable-HTTP only.
- No `headers` entry: auth is OAuth, not an API key, so clients discover it via the 401 challenge
  and `/.well-known/oauth-protected-resource`.
- `version` is the *server* version, independent of the API's assembly version. Bump it on each
  re-publish.

Install the CLI (Windows):

```powershell
$arch = if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq "Arm64") { "arm64" } else { "amd64" }
Invoke-WebRequest -Uri "https://github.com/modelcontextprotocol/registry/releases/latest/download/mcp-publisher_windows_$arch.tar.gz" -OutFile "mcp-publisher.tar.gz"
tar xf mcp-publisher.tar.gz mcp-publisher.exe
```

**Option A — DNS auth (recommended).** Generate a key pair and publish a TXT record on the apex:

```bash
openssl genpkey -algorithm Ed25519 -out key.pem
PUBLIC_KEY="$(openssl pkey -in key.pem -pubout -outform DER | tail -c 32 | base64)"
echo "3dprintlog.com. IN TXT \"v=MCPv1; k=ed25519; p=${PUBLIC_KEY}\""
# add that TXT record at the DNS provider, wait for propagation (minutes)
PRIVATE_KEY="$(openssl pkey -in key.pem -noout -text | grep -A3 "priv:" | tail -n +2 | tr -d ' :\n')"
mcp-publisher login dns --domain 3dprintlog.com --private-key "${PRIVATE_KEY}"
mcp-publisher publish
```

DNS auth covers subdomains too (`com.3dprintlog.*`).

**Option B — HTTP auth.** Same key generation, but write the proof to a file
(`v=MCPv1; k=ed25519; p=<PUBLIC_KEY>`) hosted at `/.well-known/mcp-registry-auth`, then
`mcp-publisher login http --domain 3dprintlog.com --private-key "${PRIVATE_KEY}"`.

> **Why DNS rather than HTTP, even though we already serve `/.well-known/oauth-protected-resource`:**
> that well-known lives on `api.3dprintlog.com`, the API. The registry namespace is the reverse-DNS
> of the **apex**, so the proof must be served from `3dprintlog.com` — a different host and a
> different deployment (the Angular SPA on Azure Static Web Apps).
>
> HTTP auth *is* achievable there: `src/staticwebapp.config.json` sets
> `navigationFallback` → `/shells/app-shell.html`, but that fallback only applies to paths with no
> matching file, so adding `src/.well-known/mcp-registry-auth` to the `assets` array in
> `angular.json` (as `llms.txt`, `robots.txt`, and `ads.txt` already are) would serve it as a real
> file. The catch is that it costs a UI deploy per key rotation and puts the proof in a repo that
> isn't the one being published. DNS keeps the proof next to the domain it attests to, and needs no
> deploy — hence the recommendation, not a technical blocker.
>
> Either way, do **not** put it on `api.3dprintlog.com`. That's the same class of host mix-up
> documented in `docs/mcp-auth0-production.md`, where the endpoint lives on `api.` but a related
> identifier does not.

`key.pem` is a publishing credential. Keep it out of git — store it in the same place as other
deployment secrets, and treat losing it as "rotate the TXT/well-known record".

Verify:

```bash
curl "https://registry.modelcontextprotocol.io/v0.1/servers?search=com.3dprintlog/printlog"
```

Once this works manually, automate re-publishing from the Azure pipeline so a tool change ships a
metadata update. The registry docs have a GitHub Actions recipe that adapts.

### 2. Anthropic Connectors Directory

The highest-intent audience: our only auth path is OAuth-in-Claude, and this is where Claude users
browse. Vetted and reviewed, so a listing carries real weight.

> ⚠️ **Blocker to decide on:** remote-server submissions go through
> `https://claude.ai/admin-settings/directory/submissions/new`, which lives in **organization admin
> settings**. That requires a **Team or Enterprise** plan — admin settings do not exist on
> individual plans. On Team, only Owners can submit. If we're on Pro, listing here means paying for
> at least one Team seat.

Requirements, with our status:

| Requirement | Status |
| --- | --- |
| Meets Anthropic's security standards | Creator-only tools, uniform `not_found`, no hard-delete tools, per-tool authorization filter |
| Every tool has `title` + `readOnlyHint`/`destructiveHint` | ✅ done, and test-pinned |
| OAuth 2.0 for authenticated services | ✅ Auth0 |
| Privacy policy (HTTPS URL) | ⚠️ page exists, but has no MCP/print-data section — see checklist |
| Clear setup and usage documentation | ✅ https://3dprintlog.com/docs/mcp |

Portal flow (11 steps, progress auto-saves in-browser): Introduction → Connection (URL + transport)
→ Tools (**synced live from the running server**; anything missing a title or annotation is flagged
here and must be fixed server-side before submitting) → Listing (name ≤100, tagline ≤55,
description ≤2000, 1–5 categories, docs URL, privacy URL, support contact, icon, **permanent URL
slug**) → Use cases → Company → Authentication → Data handling → Test & launch (reviewer
credentials for a populated account; you must confirm you've run every tool yourself) → Compliance
(7 acknowledgements, all required) → Review.

Run the [pre-submission checklist](https://claude.com/docs/connectors/building/review-criteria)
first. Review times are unpublished and vary with queue volume; community reports range from two
weeks to several months. Track status in the submissions dashboard; escalate to
`mcp-review@anthropic.com`.

Not applicable to us: MCP Apps (interactive UI, needs 3–5 carousel screenshots), desktop extensions
(MCPB, separate form — would need a local stdio build), and `allowed_link_uris` (we don't use
`ui/open-link`; if we ever link to 3dprintlog.com from a tool result, declare
`https://3dprintlog.com` and `https://api.3dprintlog.com` separately — subdomains are not implied).

### 3. ChatGPT

**The surface moved.** As of **2026-07-09** the ChatGPT app directory was folded into the **Plugin
directory**; plugins are now the primary discovery unit across ChatGPT and Codex, and an app (the
MCP integration) is one thing a plugin can contain. Submit through the OpenAI Developer Platform.
Submissions cover MCP connectivity details, testing guidance, directory metadata, and country
availability. OpenAI's stated bar favours tightly-scoped apps that complete a real workflow started
in conversation — "did I have enough filament / log this print" fits that framing well.

---

## Tier 2 — cheap and mostly self-serve

Do these after the official registry publish, since several will have already picked us up from it.

| Directory | Scale | How to submit | Notes for us |
| --- | --- | --- | --- |
| **Smithery** | ~6k | `https://smithery.ai/new`, GitHub auth | Accepts remote HTTP endpoints, not only Smithery-hosted builds. Has a CLI installer (`smithery mcp add`), so listings actually convert. Also ingests the official registry. |
| **PulseMCP** | hand-reviewed | "Submit" in the top nav at `pulsemcp.com` | The largest *curated* directory — curation is why a listing here is worth more than a mass aggregator. Also ingests the official registry. |
| **Glama** | ~37k | Auto-crawls public GitHub; then **claim** the listing | Our repo is public, so check whether we're already indexed before submitting. Acts as a metaregistry. |
| **mcp.so** | 20k+ | Submit button on the site, or a GitHub issue | Volume aggregator. |
| **LobeHub** | 56k+ | Site submission | Largest by raw count. |
| **Cursor Directory** | 1800+ | Form at `cursor.directory` | Name, description, repo, npm package, license, categories, tags. Our lack of an npm package may limit the form — remote-only entries are less well served here. |
| **awesome-mcp-servers** (punkpeye) | 91.5k ⭐ | GitHub PR to `README.md` | Alphabetical within category, one server per line, link to the repo. Quirk: an automated agent may append `🤖🤖🤖` to the PR title to opt into fast-tracked merging. |
| **GitHub MCP Registry / VS Code `@mcp` gallery** | curated | Publish to the official registry | The VS Code Extensions-panel gallery (`@mcp` in the search bar) is fed by the GitHub MCP Registry, which sources the official registry. **No separate submission** — Tier 1 #1 covers it. |

**Deliberately skipped: Docker MCP Catalog.** It wants a `server.yaml` PR to `docker/mcp-registry`
plus a containerized/stdio distribution that Docker builds and signs. Poor fit for an OAuth-gated
hosted service. Revisit only if the `selfhost/` work produces a container users are meant to run
themselves — the payoff would be signed images with SBOMs and provenance.

---

## Tier 3 — the gap the registries don't fill

Every directory above is general-purpose, and **"3D printing" is not a category any of them ranks
well for.** PrintLog is one of very few MCP servers in the maker/fabrication space. That scarcity is
the real advantage, and it's captured through channels no registry covers:

- r/3Dprinting, r/BambuLab, r/prusa3d, r/FixMyPrint — lead with the workflow ("ask Claude if you
  have enough filament before slicing"), not the protocol.
- Prusa and Bambu community forums; Printables.
- A launch post (Hacker News "Show HN", the 3D printing newsletters).
- Existing 3D Print Log users — in-app announcement and changelog. They are the only audience that
  already has data for the server to read, which is what makes a first session impressive rather
  than empty.

**Publish our own docs + `llms.txt`.** Needed for the Tier-1 submissions anyway, and agents
increasingly find servers by web search rather than registry lookup. A page that spells out the
connect command, the tool list, and the scopes is the single artifact every other item on this list
links to.

---

## What I can't do (needs you)

Everything left is gated on credentials, an account, a payment decision, or an outward-facing
publish. In rough dependency order:

1. **Merge and deploy the UI branch** `docs/mcp-privacy-policy` (privacy policy + `llms.txt`). Every
   Tier-1 submission links to these URLs, so they must be live first.
2. **Confirm `hello@3dprintlog.com` receives mail.** It is now published in the privacy policy and
   will be the support contact on every listing.
3. **Add the DNS TXT record** on `3dprintlog.com`. Requires DNS provider access. I can generate the
   key pair and print the exact record on request — I have not, because it creates a long-lived
   publishing credential on disk, and it should be generated wherever it will be stored. `key.pem`
   and `mcp-registry-auth` are already gitignored.
4. **Run `mcp-publisher login dns` + `publish`.** Needs the private key from step 3. Publishing to a
   public registry is an outward-facing action, so it needs your go-ahead regardless.
5. **Decide on the Team/Enterprise seat** for the Anthropic Connectors Directory. No way around it:
   the submission portal only exists in org admin settings.
6. **Create the reviewer test account** with a populated print history, printers, and filament
   inventory, and confirm you've personally run all 23 tools (the portal makes you attest to this).
   Needs a real Auth0 identity and credentials you're willing to hand a reviewer.
7. **Directory account signups** — Smithery (GitHub auth), PulseMCP, mcp.so, LobeHub, Cursor
   Directory, and claiming the Glama listing. Each needs an authenticated session as you.
8. **The `awesome-mcp-servers` PR** — I can draft the README line and open it, but forking and
   pushing to a 91.5k-star public repo under your GitHub identity is your call to authorize.
9. **Legal read-through of the privacy policy.** I described what the code does; I deliberately made
   no commitments on your behalf (there is no "we never sell your data" line, for instance).

## Open questions

1. **Team seat for the Anthropic directory** — worth the cost, or defer? This is the only Tier-1
   item with a hard money cost.
2. **`update_project` destructiveness.** This change set marked it `Destructive = true`, matching
   `update_print` and `update_printer` (overwriting a field loses the old value irrecoverably). Note
   `update_material` is deliberately `Destructive = false` — pinned by
   `MaterialWriteToolsProtocolTests.ToolList_ExposesRenamedTools_WithAnnotations` with the rationale
   that a capacity rebase changes a baseline without deleting history. That leaves the codebase with
   three `update_*` tools marked destructive and one not; the inconsistency is documented rather than
   resolved, and worth a deliberate decision before an Anthropic reviewer asks about it.
3. **Automating registry re-publish** from the Azure pipeline once the manual publish works.

---

## Sources

- [MCP Registry quickstart](https://modelcontextprotocol.io/registry/quickstart)
- [Publishing remote servers](https://modelcontextprotocol.io/registry/remote-servers)
- [Registry authentication (DNS/HTTP)](https://modelcontextprotocol.io/registry/authentication)
- [MCP Registry announcement](https://blog.modelcontextprotocol.io/posts/2025-09-08-mcp-registry-preview/)
- [Anthropic: Submitting to the Connectors Directory](https://claude.com/docs/connectors/building/submission)
- [OpenAI: Developers can now submit apps to ChatGPT](https://openai.com/index/developers-can-now-submit-apps-to-chatgpt/)
- [OpenAI: Developer mode and MCP apps in ChatGPT](https://help.openai.com/en/articles/12584461-developer-mode-and-mcp-apps-in-chatgpt)
- [awesome-mcp-servers CONTRIBUTING](https://github.com/punkpeye/awesome-mcp-servers/blob/main/CONTRIBUTING.md)
- [Tallyfy: listing on the MCP Registry, Smithery, Glama, PulseMCP](https://tallyfy.com/how-to-list-mcp-server-registry-smithery-glama-pulsemcp/)
- [RoxyAPI: MCP registries in 2026](https://roxyapi.com/blogs/mcp-registries-where-to-list-your-server)
- [explainX: top 10 MCP directories 2026](https://www.explainx.ai/blog/top-10-mcp-server-directories-2026)
- [Docker mcp-registry CONTRIBUTING](https://github.com/docker/mcp-registry/blob/main/CONTRIBUTING.md)
- [VS Code MCP support GA](https://github.blog/changelog/2025-07-14-model-context-protocol-mcp-support-in-vs-code-is-generally-available/)
