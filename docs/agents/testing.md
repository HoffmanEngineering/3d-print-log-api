# Integration testing

`WebApplicationFactory` over an in-memory SQLite database. See
`PrintLogApi.IntegrationTests/README.md`, and copy the shape of an existing test file.

### xUnit v3 on Microsoft.Testing.Platform (#70)

The runner is linked into the test assembly, which builds as an executable. There is no
`Microsoft.NET.Test.Sdk`, no `xunit.runner.visualstudio` and no `coverlet.collector`, because all
three are VSTest components. Three facts follow, and each is a trap:

- **`global.json` selects the runner** (`"test": { "runner": "Microsoft.Testing.Platform" }`).
  It is not a preference. Remove it and `dotnet test` routes to VSTest, which fails the build
  because no adapter is referenced. This is the one thing in the migration that fails loudly.
  `dotnet.config`'s `[dotnet.test.runner]` section — which the preview docs describe — is **not**
  read by the 10.0.1xx band; that was checked, not assumed.
- **Platform options go after a `--`.** `--coverage`, `--filter-class`, `--filter-method` and the
  rest belong to the executable, not the CLI. CI's
  `dotnet test --no-build --configuration Release --verbosity normal` is unchanged and still works.
- **A run that executes zero tests exits 8, not 0.** Verified by running a filter that matches
  nothing. This is strictly better than what VSTest did, and it is the property that makes the
  runner-selection trap above survivable: a misconfiguration cannot present as a green CI run that
  tested nothing. Do not add anything that swallows the test step's exit code.
- **The test project's `Properties/launchSettings.json` is gone, and must not come back.**
  `dotnet test` now *launches* the project, so it applied that file's launch profile — which was
  Web SDK scaffolding nobody had looked at, and set `ASPNETCORE_ENVIRONMENT=Development` plus an
  `applicationUrl`. `CustomWebApplicationFactory.UseEnvironment("IntegrationTesting")` overrode the
  environment, so nothing failed, but the suite was one `IHostEnvironment` read away from
  configuring itself as Development. Under VSTest the file was simply inert.
- **Coverage is `Microsoft.Testing.Extensions.CodeCoverage`**, driven from `coverage.ps1` /
  `coverage-check.sh`. Both were rewritten and both were run. See the coverage section of the
  tests README before editing either — the Cobertura format and the absolute results directory are
  each load-bearing, and the failure mode of getting them wrong is a missing report, not an error.

`xUnit1051` is the new analyzer that matters: every call taking a `CancellationToken` now passes
`TestContext.Current.CancellationToken`, applied across ~1080 sites by `dotnet format`. It has a
code fix, so the CI format gate — not just the build — is what enforces it. Do not add a call
without the token; do not suppress the rule.

**A test may not depend on running before its siblings.** v3 orders cases within a class
differently from v2, and that alone broke two tests that had been asserting on seeded rows a
sibling deletes (`DeleteAllNotifications_*`) or pages past (the summary endpoints default to
`PageSize = 10`). Both were latent; nothing about v3 made them wrong. Arrange what you assert on.

**The shared in-memory connection takes one user at a time.** EF registers its SQL functions on
the connection for every new `DbContext`, and SQLite refuses with error 5 ("database is locked",
or "unable to delete/modify user-function due to active statements") while another statement on it
is running, so the failure lands on whichever test created a context, often an unrelated one. Read a
`ResponseHeadersRead` response to the end before the test returns (the streaming CSV export ran into
the next test that way), and put a host whose requests or workers really run in parallel on a
`SqliteFileDatabase` (a temporary file in WAL mode, a connection per context). The tests README
has the details.

### Gotcha: inotify limits in containers

In a container with a low inotify limit (Claude Code cloud sessions allow 128 instances), a full
`dotnet test` fails about 800 tests with `IOException: The configured user limit (128) on the
number of inotify instances has been reached`. Each test host's JSON configuration source starts
its own file watcher. It is the environment, not the code. Run the suite with polling watchers:

```bash
DOTNET_USE_POLLING_FILE_WATCHER=true dotnet test --verbosity quiet
```

### Gotcha: testing JWT-protected endpoints against a local signing key

When validating a real `JwtBearer`/`McpBearer` scheme against a locally-issued token (e.g. the
MCP audience-isolation tests), you MUST null out the metadata configuration after re-pointing the
options:

```csharp
options.Authority = null;
options.MetadataAddress = null;
options.ConfigurationManager = null; // <-- critical
options.TokenValidationParameters = new() { IssuerSigningKey = TestJwt.SigningKey, /* ... */ };
```

The built-in `JwtBearerPostConfigureOptions` already created a `ConfigurationManager` from the
original `Authority`, so setting `Authority = null` alone is not enough. If it survives, every
request performs an OIDC-metadata fetch to a non-existent tenant that DNS-times-out (~30-40s per
request), turning a 9-test suite into a 6-minute one and causing SDK MCP-client tests to fail on
their init timeout. See `CustomWebApplicationFactory.ConfigureLocalJwt`.
