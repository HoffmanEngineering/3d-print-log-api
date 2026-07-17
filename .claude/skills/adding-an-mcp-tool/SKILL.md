---
name: adding-an-mcp-tool
description: Use when adding a read or write tool to the MCP server (PrintLogApi/Mcp/PrintLogReadTools or PrintLogWriteTools, exposed at /mcp), when choosing whether a write tool's idempotency key is required, or when an MCP tool leaks another user's data, reports wrong filament usage, advertises the wrong required fields, duplicates an entity on retry, or its integration test hangs.
---

# Adding an MCP Tool

## Overview

The `/mcp` server has **two** tool classes, split by scope. Both are creator-only.

| | Read | Write |
| --- | --- | --- |
| Class | `Mcp/PrintLogReadTools.cs` | `Mcp/PrintLogWriteTools.cs` |
| Scope | `read:printdata` (`McpRead`) | `write:printdata` (`McpWrite`) |
| Contracts | `Mcp/McpContracts.cs` | `Mcp/McpWriteContracts.cs` |
| Copy from | `SearchPrints` / `SearchOwnPrintsForMcp` | `CreateProject` / `CreateProjectForMcp` |

A tool = a **contract record** + an **ownership-scoped service method** + a **thin tool method** +
**integration tests through /mcp**. Copy the nearest existing tool; this skill is the checklist and
the gotchas. `CLAUDE.md` holds the surface's invariants and the reasoning behind them.

## Steps (both kinds)

1. **Contract** — a concrete `record`. Never return EF entities, API DTOs, or anonymous objects.
   Invariant units only: **grams** (from mg), **seconds**, **UTC**.
2. **Service method** — the FIRST filter is always the ownership boundary. It is not the same column
   everywhere: Print/Filament/Project use `CreatedById`, **Printer uses `UserId`**. A foreign or
   missing id must surface a uniform `not_found` — never an existence oracle.
3. **Register** any NEW service in `Startup.ConfigureServices` and inject it into the tool class.
4. **Tool method** — `[McpServerTool, Description("…")]`. Get the user with
   `McpUserContext.RequireUserId(httpContextAccessor.HttpContext!.User)` — **never** a tool argument.
   Throw `McpToolException.NotFound/InvalidArguments/Forbidden/Conflict`.
5. **Tests** — `IClassFixture<McpDataWebApplicationFactory>`. Cover creator-only isolation, each
   filter/validation rule, and invalid input → error.

## Extra steps for a WRITE tool

1. **Decide the idempotency key: required or optional.** The deciding question is *what a keyless
   retry costs*, not consistency with the other tools.
   - **Required** when the tool has a side effect outside the database that cannot be taken back —
     `create_print` (required) and `create_feedback` (required: it emails the maintainers).
   - **Optional** when the worst case is a duplicate row the user can delete — `create_material`,
     `create_printer`, `create_project`. State the at-least-once risk in the tool description and
     pin it with a test.
2. **Fingerprint** — add a `ComputeCreateX` to `McpRequestFingerprint`. Same key + same args
   replays; same key + different args is a `conflict`.
3. **Canonicalize ONCE, in the service, before both hashing and persistence.** Normalizing inside
   the fingerprint instead makes it assert two calls are equivalent while the database stores
   different rows.
4. **New target column** — add `CreatedXId` to `McpIdempotencyRecord`, a `ForX` to
   `McpIdempotencyRecordFactory` (and its non-null **count**), plus an additive migration. Several
   targets are `Guid?`, so a target written to the wrong one still compiles — the count is the only
   thing that catches it.
5. **Echo everything settable in the result.** A write-only agent cannot call the read tools at all,
   and most entities have no `get_x`, so the echo is the ONLY way a caller can confirm what it wrote.
6. **Validate before mutating**, so a rejected write leaves the entity untouched.
7. **Invalidate `ICacheVersionService` after commit** — unless the entity appears in no cached
   response (feedback), where invalidating is pure cost.

## Gotchas (these cost real time)

- **Optional params need C# defaults; required params must NOT have one.** The SDK derives the
  schema's `required` list from parameters *without* a default — nullability is irrelevant. A
  positional record with no defaults advertised every field as required while the server happily
  accepted them omitted, so agents sent `"notes": null` to satisfy a rule that did not exist. The
  same rule in reverse: giving a required key a default silently downgrades it to optional. Pin the
  advertised schema in `ToolSchemaTests`.
- **Reject with the valid options.** Nothing lists printer/material categories or feedback types, so
  name them in the rejection. Fixed seeds/enums shared by all users — safe to enumerate, and the
  extra query runs only on the failure path.
- **A config-gated side effect passes tests vacuously.** The feedback notification only sends when
  `FeedbackEmailAddress` is set, and test settings left it empty — the tests "passed" while covering
  nothing. Assert on the side effect itself (`RecordingEmailSender`), not just the row.
- **Post-commit work must not take the caller's `CancellationToken`.** After the commit, honouring a
  disconnect cannot un-commit anything — it only strands the side effect, reports a committed write
  as failed, and burns the idempotency key so the retry replays and never redoes it. Omit the
  parameter rather than documenting "don't pass it", and catch cancellation like any other failure
  there (`HttpClient` timeouts arrive in that shape too). Test with an
  `OperationCanceledException`-shaped failure specifically — a generic-exception test passes straight
  through this bug.
- **Test config: `ConfigureAppConfiguration`, not `UseSetting`.** `UseSetting` writes *host*
  configuration, which `appsettings.json` is then layered on top of, putting the old value back.
- **Fixture fakes are singletons shared by every test in the class.** Tag each test's data with a
  unique marker and assert on matches; counting everything sent couples the tests to each other.
- **Filament usage comes from the child rows, not the scalar.** Sum `PrintFilament.AmountMg`
  (fallback `EstimatedAmountMg` when `AmountMg` is null/0). `Print.FilamentUsageMg` is legacy and
  **not maintained** — using it reports 0/understated grams for real prints.
- **JWT test hang:** the local-signing test factory MUST set `ConfigurationManager = null` on the
  bearer options, or every test does a ~30s OIDC-metadata DNS timeout. (See
  `CustomWebApplicationFactory` and the note in `CLAUDE.md`.)
- **Tool name is snake_case** of the method: `SearchPrints` → `"search_prints"`.
- **CallToolAsync args:** pass `new Dictionary<string, object> { ["x"] = … }` — the parameter is
  `IReadOnlyDictionary`, so `new() { … }` won't compile. Enum/`Guid`/`DateTimeOffset` args
  round-trip through JSON; put the CLR value in the dict.
- **Error assertions:** use `McpDataWebApplicationFactory.IsToolError(client, name, args)` — it
  handles both an `IsError` result and a thrown `McpException`.
- **Wire format:** the serializer omits nulls, so a cleared/unset field is **absent**, not null.
  Tests asserting a clear must check for absence.
- **Parse results** from the first `TextContentBlock` of the `CallToolResult` (JSON), deserialized
  with `PropertyNameCaseInsensitive = true`.
- **Seed realistically:** store usage in `PrintFilament` rows (not the scalar) so tests actually
  exercise the production path.
- **Auth0 access tokens carry no `email` claim** (it lives in ID tokens). Resolve account email
  server-side via `IAuth0Service.GetUserEmail`; needs `read:users` on the M2M app per environment.
  Treat it as best-effort — never fail a user's write because Auth0 is down.

## Verify

```bash
dotnet test PrintLogApi.IntegrationTests --filter Mcp --verbosity quiet
```
