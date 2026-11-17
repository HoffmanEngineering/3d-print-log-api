# MCP server

## The `/mcp` endpoint

The `/mcp` endpoint (Streamable HTTP, stateless, MCP revision 2026-07-28 via SDK 2.0.0) exposes
tools to agents in two classes: `PrintLogReadTools` (`McpRead`, `read:printdata`) and
`PrintLogWriteTools` (`McpWrite`, `write:printdata`). Both are creator-only — the user is always
token-derived, never a tool argument, and a foreign or missing id returns a uniform `not_found`
rather than an existence oracle.

Authorization is two-layered, and **the endpoint is the weaker layer**. The `"Mcp"` policy requires
a mapped internal user plus _at least one_ data scope, so a read-only token legitimately reaches
`/mcp`. The read/write split is enforced per tool _class_ by the SDK's authorization filter
(`AddAuthorizationFilters()`), which also hides write tools from a read-only token's `tools/list`.
Write-tool denial rests entirely on that filter — never assume the endpoint blocks it.

Do not "simplify" these; each is load-bearing and looks redundant:

- `options.Stateless = true` — now the SDK default, kept to document the invariant it relies on
  (no session state, no standalone SSE `GET`/`DELETE`).
- `cacheScope: private` on `tools/list` — the list varies by token scope, so a shared cache would
  disclose the write-tool surface to a read-only caller (`McpCachingHintsTests`).
- The assertions in `McpWritePolicyTests` — read their comments before editing; which status codes
  they accept is what makes them cover the tool layer rather than the endpoint.
- The call-tool filter in `Startup.ConfigureMcpServer` — the single choke point for tool errors and
  telemetry, so exceptions never reach a caller as detail.

No hard-delete tools. Every write invalidates `ICacheVersionService` after commit.

See the `adding-an-mcp-tool` skill before adding or changing a tool — it carries the checklist, the
domain rules, and the known gaps.

### Docs over MCP (#129)

The public user docs are served as three tools (`search_docs`, `list_docs`, `get_doc`) and as
`docs://<slug>` resources, on `/mcp` **and** anonymously on `/mcp/docs`. Code: `Mcp/Docs/`.

- **`/mcp/docs` is a second endpoint, not anonymous access on `/mcp`.** A client starts OAuth only
  when its first request gets a 401. Letting an unauthenticated `initialize` through on `/mcp` would
  leave a new client connected, unauthenticated and seeing only the docs, with nothing prompting it to
  sign in. `McpDocsEndpointTests` pins `/mcp`'s 401 byte for byte.
- **One server registration, narrowed per request.** `McpDocsEndpoint.ConfigureSessionOptions`
  replaces the tool collection with the `[PublicDocsTools]` classes when the path is `/mcp/docs`.
  The SDK hands that callback a fresh options instance on every stateless request, so it cannot
  leak into `/mcp`; a test proves `/mcp` keeps its tools after a docs call. The data tools keep their
  `[Authorize]` policies as the second layer. **Never put a user-scoped service in a
  `[PublicDocsTools]` class.**
- **The content is fetched, never copied.** `DocsCatalog` reads `/docs/llms.txt` and each
  `/docs/<slug>.md` twin from `Docs:BaseUrl` (the UI generates them in `scripts/docs-twins.mjs`).
  Each fetch is rebuilt from that origin and a slug that passed the slug rule, never from the
  index's link text; redirects are off and bodies are size-capped. A failed or partial load is kept
  for `Docs:FailureCacheMinutes`, a complete one for `Docs:CacheMinutes`, in a field rather than
  `IMemoryCache` (reasoning at the class).
- **A missing site degrades, it does not fail.** Empty resource list, `unavailable` from the tools,
  host starts normally. Every test host routes the `Docs` HTTP client to `DocsSiteStub`, which
  404s anything unseeded, so no test reaches the network.
- Rate-limited per client address (`Mcp:DocsRateLimitPerMinute`), since there is no user to
  partition on.

### MCP Apps views (#130)

`get_material_inventory` links to a `ui://` view (`Mcp/Apps/`), built against MCP Apps (SEP-1865)
spec `2026-01-26`. A host that renders apps shows the result as a table; every other client gets
the unchanged text result. Tool `_meta` carries `ui.resourceUri` plus ChatGPT's
`openai/outputTemplate` alias, via `[McpMeta]`.

- **A view is presentation only.** It renders the tool result the host posts to it and never calls
  a tool, so no view can start a write. `McpAppResourcesTests` fails on `tools/call` in the HTML.
  Confirm-before-write was declined in #130 for that reason, not deferred for effort.
- **Self-contained HTML, embedded in the assembly.** No external URL of any kind, values written
  with `textContent` only, and an empty CSP allowlist in `_meta.ui.csp` on both the listing and the
  read. The tests fail on a URL, a `src`/`href`, `innerHTML`, or a network API in the file.
- **Not on `/mcp/docs`.** `McpAppResources` wraps the docs resource handlers (the SDK takes one
  list and one read handler for both endpoints) and drops `ui://` on the docs path, failing closed
  when it cannot see the path.
- The tool result has no `structuredContent`; the view parses the JSON text block, preferring
  `structuredContent` if a tool ever adds it. Renaming a field of `MaterialInventoryItem` breaks
  the view silently, which is what the field-name assertion in the tests is for.

### Registry listing (`server.json`)

`server.json` at the repo root is the official MCP Registry entry, published by the
`publish-mcp-registry` job in `deploy.yml` (off until `vars.MCP_REGISTRY_PUBLISH` is `'true'`).
Runbook and owner steps: `docs/mcp-registry-listing.md`.

- **Its `version` is the listing's, not the API's.** Registry versions are immutable and the
  registry holds no tool list, so bump it only in a PR that changes `server.json`, and mirror
  name/description/version in the UI's `scripts/discovery-lib.mjs`. The job fails a release whose
  `server.json` changed without a bump rather than silently skipping it.
- **`description` is capped at 100 characters** by the registry schema. `ServerJsonTests` validates
  the file against the vendored schema offline; a new `$schema` means vendoring the new file.
- **Every tool states all four behavior hints explicitly**, read tools included, or the ChatGPT
  directory rejects the server (`ToolSchemaTests`).

### Agent skill and plugin (`skills/`, `plugin.json`, `mcp.json`, `.claude-plugin/`)

`skills/3d-print-log/SKILL.md` is the public skill that teaches an agent to use the MCP tools
(UI #215). The repo root doubles as a plugin that bundles it with the server, in two formats:
`plugin.json` + `mcp.json` (Agent Plugins 1.0.0) and `.claude-plugin/marketplace.json` (Claude Code,
which carries the public OAuth client ID that Agent Plugins has no field for). `npx skills add
HoffmanEngineering/3d-print-log-api` installs the skill; `claude plugin marketplace add` with the
same repo installs the plugin.

- **Renaming or removing a tool or parameter means editing `SKILL.md` in the same PR.**
  `AgentSkillTests` reads the skill against the live `tools/list` and fails on any tool, call
  argument or usage-row field that no longer exists. It also fails when a tool name suggests a
  delete or a filament load, because the skill promises neither exists.
- **The UI serves a byte-identical copy** at `/.well-known/agent-skills/3d-print-log/SKILL.md`,
  with its SHA-256 in `/.well-known/agent-skills/index.json`. After this file changes on `main`,
  run `npm run discovery:sync-skill` in the UI repo and commit the result. Until then the site
  serves the previous version, which is stale but still self-consistent.
- **No `version` in `plugin.json` or the marketplace entry, on purpose.** Both formats pin an
  installed plugin to its declared version until it changes, so a forgotten bump would freeze
  users on an old skill. Without one, Claude Code tracks the commit.
- `.claude/skills/` holds contributor skills. They carry `metadata.internal: true` so the skills
  CLI does not offer them to people installing the public one.
