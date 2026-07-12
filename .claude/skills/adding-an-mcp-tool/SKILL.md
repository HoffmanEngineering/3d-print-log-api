---
name: adding-an-mcp-tool
description: Use when adding a new read-only tool to the MCP server (PrintLogApi/Mcp/PrintLogTools, exposed at /mcp), or when an MCP tool leaks another user's data, reports wrong filament usage, or its integration test hangs.
---

# Adding an MCP Tool

## Overview

The `/mcp` server is read-only and creator-only. A tool = a **contract record** + a
**creator-scoped service query** + a **thin tool method** + **integration tests through /mcp**.
Copy an existing tool (e.g. `SearchPrints` / `SearchOwnPrintsForMcp`) — this skill is the
checklist and the non-obvious gotchas.

## Steps

1. **Contract** — add a concrete `record` to `Mcp/McpContracts.cs`. Never return EF entities, API
   DTOs, or anonymous objects. Invariant units only: **grams** (from mg), **seconds**, **UTC**.
2. **Service method** (e.g. in `PrintService`/`FilamentService`, or `McpStatisticsService` for
   stats): the FIRST filter is always the ownership boundary — `Where(x => x.CreatedById == userId)`
   (prints) or the owning user column. `AsNoTracking`, `CancellationToken`, paging via
   `McpPaging`. Aggregate in SQL; no unbounded materialization.
3. **Register** any NEW service in `Startup.ConfigureServices` and inject it into `PrintLogTools`'s
   constructor.
4. **Tool method** on `Mcp/PrintLogTools.cs`: `[McpServerTool, Description("…")]`. Get the user
   with `McpUserContext.RequireUserId(httpContextAccessor.HttpContext!.User)` — **never** a tool
   argument. Validate with `McpPaging.RequirePage`/`ClampPageSize` and `McpValidation`. Throw
   `McpToolException.NotFound/InvalidArguments/Forbidden` for errors (the call-tool filter maps
   them to `IsError`, and logs `Mcp_ToolCalled`).
5. **Tests** — new class with `IClassFixture<McpDataWebApplicationFactory>`. Cover: creator-only
   isolation (another user's rows excluded; a foreign id → not-found even if public), each filter,
   paging clamp (1000 → 100), and invalid input → error.

## Gotchas (these cost real time)

- **Filament usage comes from the child rows, not the scalar.** Sum `PrintFilament.AmountMg`
  (fallback `EstimatedAmountMg` when `AmountMg` is null/0). `Print.FilamentUsageMg` is legacy and
  **not maintained** — using it reports 0/understated grams for real prints.
- **JWT test hang:** the local-signing test factory MUST set `ConfigurationManager = null` on the
  bearer options, or every test does a ~30s OIDC-metadata DNS timeout. (See `CustomWebApplicationFactory`
  and the note in `CLAUDE.md`.)
- **Tool name is snake_case** of the method: `SearchPrints` → `"search_prints"`.
- **CallToolAsync args:** pass `new Dictionary<string, object> { ["x"] = … }` — the parameter is
  `IReadOnlyDictionary`, so `new() { … }` won't compile. Enum/`Guid`/`DateTimeOffset` args
  round-trip through JSON; put the CLR value in the dict.
- **Error assertions:** use `McpDataWebApplicationFactory.IsToolError(client, name, args)` — it
  handles both an `IsError` result and a thrown `McpException`.
- **Parse results** from the first `TextContentBlock` of the `CallToolResult` (JSON), deserialized
  with `PropertyNameCaseInsensitive = true`.
- **Seed realistically:** store usage in `PrintFilament` rows (not the scalar) so tests actually
  exercise the production path.

## Verify

```bash
dotnet test PrintLogApi.IntegrationTests --filter Mcp --verbosity quiet
```
