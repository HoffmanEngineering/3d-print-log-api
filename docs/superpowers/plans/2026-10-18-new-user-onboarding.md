# New-User Onboarding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Get new 3D Print Log accounts from sign-up to a first logged print with three pieces: a `/welcome` stepper, quick-add printer and material, and a Getting Started checklist. Alongside them: an add-printer path on the print form for users arriving from a slicer, onboarding emails that reach users who leave early, and funnel tracking.

**Architecture:**
- **API** (.NET 10):
  - two seeded setting types;
  - a private `GET /api/Users/me/onboarding` status endpoint;
  - an Auth0-backed email-verification refresher, used by that endpoint and by the email dispatcher;
  - a resend-verification endpoint;
  - telemetry additions.
- **UI** (Angular 21): an `OnboardingService` owns every rule. Standalone OnPush components plug into the existing print form, the print list, the auth callback and the routes.

**Tech stack:**
- **API:** ASP.NET Core, EF Core (SQLite in tests, SQL Server in prod), xUnit v3 on MTP, Auth0 Management API.
- **UI:** Angular 21 (signals, standalone, OnPush), Angular Material (M2 theme), Karma/Jasmine, Cypress, App Insights JS SDK, gtag.

**Spec:** `3d-print-log-ui/docs/superpowers/specs/2026-10-18-new-user-onboarding-design.md`. Section references below (§n) point into it. Read the spec section a task names before starting.

**Worktrees:**
- UI: `D:\Development\github\3d-print-log-ui-worktrees\onboarding`
- API: `D:\Development\github\3d-print-log-api-worktrees\onboarding`
- Both on branch `feat/new-user-onboarding`. Never edit the main checkouts.

## Global Constraints

- **Setting type ids:**
  - 29 = `Onboarding_WelcomeFinishedAt`
  - 30 = `Onboarding_ChecklistDismissedAt`
  - 26 = `Email_NoticeSeenAt` (existing)

  Values are ISO-8601 timestamp strings.
- **Badge keys:** `first-printer`, `first-material`, `first-print`. Earned means `me().families.find(f => f.key === k)?.tiers.length > 0`.
- **Eligibility:** `Onboarding:LaunchDate != null && CreatedDate != null && CreatedDate >= LaunchDate`. Null means the feature is off.
- **Event names are `Component_Action`** (UI AGENTS.md). They are listed exactly in §6 and §8.4.
- **American English** in code, comments and UI copy.
- **API:**
  - New `.cs` files need a UTF-8 BOM and CRLF: run `dotnet format` before committing.
  - No `#nullable` headers.
  - DTO reference properties are nullable, with no `required` and no `= null!`.
  - Pass `TestContext.Current.CancellationToken` to every call that takes a token.
  - Migrations only add things.
- **UI:**
  - Standalone (don't set `standalone: true`), OnPush, `inject()`, signals, native control flow.
  - SSR-safe: guard browser globals with `isPlatformBrowser`.
  - Theme tokens `--app-*` only, never `--mat-sys-*`.
  - Run `npx prettier --write` on every `.html` you touch.
  - Busy states go through `deferred-skeleton.ts`.
- **Onboarding never blocks the app:** any failed lookup is treated as ineligible or verified, and the user navigates normally.
- **Never request push permission** in any onboarding surface.
- **Commits:** conventional style (`feat(onboarding): …`). No `Claude-Session` trailer.

## Review Focus

1. **A user who signs up from a slicer on a phone, or in a browser where the Cura hand-off URL is long.** The print form must keep every slicer field after the quick-add dialog closes. Test in Task U9.
2. **An existing (ineligible) user with zero printers.** They must never be redirected to `/welcome` or see the checklist, and the email banner must behave as before. Tests in U1 and U10.
3. **A failing `me/onboarding` or settings call (an API older than this change, or offline).** The guard allows navigation, the checklist is hidden, and nothing throws. Test in U1, with a 404 from `me/onboarding`.
4. **Double submission:** Continue, Save or Resend pressed twice. There must be no duplicate printers or filaments and no 429 shown as an error. Tests in U5 and U8 (buttons disabled while saving) and U8 (a 429 shows "already sent").
5. **Two tabs:** setting 29 written in one tab while `/welcome` is open in another. `markWelcomeFinished` and `acknowledgeEmailNotice` must stay idempotent: they write only if unset, and treat a 400 "already exists" from POST as success. Test in U1.

---

## API track (`3d-print-log-api-worktrees/onboarding`)

Commands: `dotnet build --configuration Release`, `dotnet test --verbosity quiet` (filters go after `--`, e.g. `dotnet test -- --filter-class "*EmailDispatcherTests"`), `dotnet format`.

### Task A1: Seed setting types 29 and 30

**Files:**
- Modify: `PrintLogApi/PrintLogContext.cs:118-120` (append two `UserSettingType` rows after Id 28)
- Create: a migration named `AddOnboardingSettingTypes`, generated with `dotnet ef migrations add AddOnboardingSettingTypes --project=PrintLogApi`. It is shaped like `20261016131959_AddReleaseNotesSeenSettings.cs`.
- Create: `PrintLogApi/Users/OnboardingSettingTypes.cs`, with `public static class OnboardingSettingTypes { public const int WelcomeFinishedAt = 29; public const int ChecklistDismissedAt = 30; }`
- Test: `PrintLogApi.IntegrationTests/Controllers/UserSettingsControllerTests.cs:618-631` (add two `InlineData` rows)

**Interfaces:**
- Produces: `OnboardingSettingTypes.WelcomeFinishedAt` and `.ChecklistDismissedAt`.

- [ ] **Step 1:** Add `[InlineData(29, "Onboarding_WelcomeFinishedAt")]` and `[InlineData(30, "Onboarding_ChecklistDismissedAt")]` to `SettingTypes_AreSeeded`. Run `dotnet test -- --filter-method "*SettingTypes_AreSeeded*"`. Expect FAIL for 29 and 30.
- [ ] **Step 2:** Add the seed rows. Descriptions:
  - 29: "When the user finished or skipped the new-user welcome flow (ISO 8601)."
  - 30: "When the user dismissed the Getting Started checklist (ISO 8601)."

  Generate the migration and check that its `Up`/`Down` only insert and delete these two rows. Add the constants class.
- [ ] **Step 3:** Re-run the test. Expect PASS. Run `dotnet ef migrations has-pending-model-changes --project PrintLogApi` and expect no changes. If Docker SQL Server on port 1434 is up, run the AGENTS.md `database update` check against a scratch database, then drop it.
- [ ] **Step 4:** Run `dotnet format`, then commit `feat(onboarding): seed onboarding setting types 29 and 30`.

### Task A2: `UserOnboardingOptions` and `GET /api/Users/me/onboarding`

**Files:**
- Create: `PrintLogApi/Users/UserOnboardingOptions.cs`. It holds `public const string SectionName = "Onboarding"` and `public DateTimeOffset? LaunchDate { get; set; }`, plus `public bool IsEligible(DateTimeOffset? createdDate)` implementing the Global Constraints rule.
- Create: `PrintLogApi/Models/DTOs/User/OnboardingStatusDto.cs`, a record `OnboardingStatusDto(bool OnboardingEligible, DateTimeOffset? CreatedDate, bool EmailVerified)` in namespace `PrintLogApi.Models.DTOs`.
- Modify: `PrintLogApi/Startup.cs`. Register `services.Configure<UserOnboardingOptions>(Configuration.GetSection(UserOnboardingOptions.SectionName))` next to `PushOptions` at line 99.
- Modify: `PrintLogApi/appsettings.json`. Add a top-level `"Onboarding": { "LaunchDate": null }`.
- Modify: `PrintLogApi/Controllers/UsersController.cs`. Add the action next to `GET me/email` (88-103):
  - `[HttpGet("me/onboarding")]`
  - `[Authorize(Policy = "InteractiveUserOnly")]`
  - `public async Task<ActionResult<OnboardingStatusDto>> GetOnboardingStatus(CancellationToken ct)`
  - Inject `IOptions<UserOnboardingOptions>`.
  - In this task `EmailVerified` comes straight from the database. A4 adds the refresh.
- Test: `PrintLogApi.IntegrationTests/Controllers/OnboardingStatusEndpointTests.cs`

**Interfaces:**
- Produces: `UserOnboardingOptions.IsEligible(DateTimeOffset?)`; the `OnboardingStatusDto` JSON `{onboardingEligible, createdDate, emailVerified}`.

- [ ] **Step 1: Write the failing tests.** Seed users with `EmailTestData.CreateUserAsync(db, createdDate: …)`, set config through `_factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.Configure<UserOnboardingOptions>(o => o.LaunchDate = …)))`, and authenticate with `TestAuthHandler.TestUserIdHeader = user.OAuthUserId`.
  - `LaunchDateNull_IsNotEligible`
  - `CreatedAfterLaunch_IsEligible`: asserts `createdDate` round-trips and `emailVerified` matches the seed.
  - `CreatedBeforeLaunch_IsNotEligible`
  - `NullCreatedDate_IsNotEligible`
  - `ApiKey_IsRejected`: 401, copying `AccountEmailEndpointTests.GetAccountEmail_ApiKey_IsRejected`.
  - `PublicProfile_DoesNotExposeOnboardingFields`: `GET /api/Users/{id}` anonymous JSON has no `emailVerified` or `createdDate` property.
- [ ] **Step 2:** Run the filter class. Expect FAIL (404).
- [ ] **Step 3:** Implement the options class, the DTO and the action. Add an `InlineData` row for the new endpoint in `OpenApiDocumentTests.cs:202`, copying the `me/email` row.
- [ ] **Step 4:** Run the class and `OpenApiDocumentTests`. Expect PASS.
- [ ] **Step 5:** Run `dotnet format`, then commit `feat(onboarding): add GET users/me/onboarding status`.

### Task A3: Auth0 email-verification calls

**Files:**
- Modify: `PrintLogApi/Services/IAuth0Service.cs`. Add:
  - `Task<Auth0EmailStatus?> GetEmailStatus(string oauthUserId, CancellationToken ct);`
  - `Task SendVerificationEmail(string oauthUserId, CancellationToken ct);`
  - A record `public sealed record Auth0EmailStatus(string? Email, bool EmailVerified);` in the same file.
- Modify: `PrintLogApi/Services/Auth0Service.cs`:
  - `SendManagementAsync` (172-205) gains a trailing `HttpContent? content = null` parameter. The content must be re-created for the 401 retry, so pass a `Func<HttpContent>?` instead of an instance.
  - `GetEmailStatus` is `GET {ManagementBaseUrl}/users/{escaped}?fields=email,email_verified&include_fields=true`. It returns null on an empty body.
  - `SendVerificationEmail` is `POST {ManagementBaseUrl}/jobs/verification-email` with JSON `{"user_id": oauthUserId}`.
- Modify the test fakes that implement `IAuth0Service`:
  - `CustomWebApplicationFactory.TestAuth0Service`. Add knobs `public Auth0EmailStatus? EmailStatus`, `public bool ThrowOnEmailStatus`, `public List<string> VerificationEmailsSent` and `public bool ThrowOnSendVerification`.
  - `ConnectedAgentsControllerTests.FakeAuth0Service` and `UserDeletionServiceTests.TestAuth0Service`: these throw `NotImplementedException`.
- Test: `PrintLogApi.IntegrationTests/Auth0/EmailVerificationCallsTests.cs`, using `Auth0TestHarness.CreateService(StubHandler)`.

**Interfaces:**
- Produces: `IAuth0Service.GetEmailStatus` and `IAuth0Service.SendVerificationEmail`; `Auth0EmailStatus`; the `TestAuth0Service` knobs above. On a non-success response both methods throw `Auth0ApiException`.

- [ ] **Step 1: Write the failing tests.**
  - `GetEmailStatus_ParsesVerifiedFlag`: asserts the request URL contains `fields=email,email_verified` and the result is `(email, true)`.
  - `GetEmailStatus_EmptyBody_ReturnsNull`
  - `SendVerificationEmail_PostsUserId`: method POST, path `/api/v2/jobs/verification-email`, and the body's `user_id` equals the id.
  - `SendVerificationEmail_RetriesOnceAfter401WithBody`: the second request also carries the body.
  - `SendVerificationEmail_Non2xx_Throws`.
- [ ] **Step 2:** Run. Expect a compile failure or FAIL.
- [ ] **Step 3:** Implement the methods and update every fake. Build the whole solution.
- [ ] **Step 4:** Run the new class plus `ListMcpGrantsTests` and `RevokeMcpGrantTests`. Expect PASS.
- [ ] **Step 5:** Run `dotnet format`, then commit `feat(onboarding): auth0 email status and verification email calls`.

### Task A4: `EmailVerificationRefresher` (§7.3)

**Files:**
- Create: `PrintLogApi/Email/EmailVerificationRefresher.cs`:
  - `public interface IEmailVerificationRefresher { Task<bool> RefreshAsync(long userId, CancellationToken ct); }`
  - `public sealed class EmailVerificationRefresher(PrintLogContext db, IAuth0Service auth0, IMemoryCache cache, ILogger<EmailVerificationRefresher> logger, TimeProvider clock) : IEmailVerificationRefresher`
  - Registered scoped in `EmailServiceCollectionExtensions`.
- Modify: `UsersController.GetOnboardingStatus`. When the database flag is false, call `RefreshAsync` and use its result.
- Test: `PrintLogApi.IntegrationTests/Email/EmailVerificationRefresherTests.cs`. Build it on the factory's `TestAuth0Service` knobs and resolve it from a scope.

**Interfaces:**
- Consumes: A3's `GetEmailStatus`.
- Produces: `IEmailVerificationRefresher.RefreshAsync(long userId, CancellationToken)`, which returns the effective verified flag and never throws. Its throttle key is `$"email_verify_refresh:{userId}"`, sized `CacheBudget.SmallEntryBytes`, with a 1 h absolute expiry. Copy the `UserApiKeyService.cs:171-176` pattern and comment.

Behavior:
1. Load the tracked user. If it's missing, return false. If `EmailVerified` is true, return true.
2. If the throttle flag exists, return false.
3. Set the flag, then call Auth0.
4. When Auth0 reports verified, set `EmailVerified = true`, set `Email` to the trimmed, lower-cased Auth0 email if it differs (the same normalization as `ClaimsTransformer` email sync), set `EmailUpdatedAt = clock.GetUtcNow()`, and save.
5. On an exception other than cancellation, log a warning and return the current flag. A null `OAuthUserId` returns false with no call.

- [ ] **Step 1: Write the failing tests.**
  - `AlreadyVerified_NoAuth0Call`
  - `Auth0Verified_UpdatesUserAndReturnsTrue`: asserts the database row changes.
  - `Auth0Unverified_ReturnsFalse`
  - `SecondCallWithinHour_IsThrottled`: the Auth0 call count stays at 1.
  - `Auth0Throws_ReturnsFalseAndDoesNotThrow`
  - `OnboardingEndpoint_RefreshesUnverifiedUser`: `GET me/onboarding` returns `emailVerified: true` once the knob says verified.
- [ ] **Step 2:** Run. Expect FAIL.
- [ ] **Step 3:** Implement and register the refresher, and wire it into the endpoint.
- [ ] **Step 4:** Run the class and `OnboardingStatusEndpointTests`. Expect PASS.
- [ ] **Step 5:** Run `dotnet format`, then commit `feat(onboarding): refresh email verification from auth0`.

### Task A5: The dispatcher defers unverified onboarding emails (§7.2)

**Files:**
- Modify: `PrintLogApi/Email/Outbox/EmailDispatcher.cs`:
  - Add the constructor parameter `IEmailVerificationRefresher verification`.
  - Change the unverified gate at 218-221. When `row.Campaign == OnboardingCampaign.CampaignName` and the email is present but unverified, call `await verification.RefreshAsync(row.UserId, ct)`. If it is still false, `return Defer(row, now + GateRecheck, EmailSkipReasons.Unverified)`. If it is true, continue down the gates.
  - Other campaigns, and a missing email, keep `Skip(row, Unverified)`.
- Modify: `PrintLogApi.IntegrationTests/Email/EmailDispatcherTests.cs`, the `Harness`. Construct the dispatcher with a `FakeVerificationRefresher { public bool Result; public int Calls; }`, a test double in the same folder.
- Test: in `EmailDispatcherTests.cs`.

**Interfaces:**
- Consumes: A4's `IEmailVerificationRefresher`.

- [ ] **Step 1: Write the failing tests.**
  - `Onboarding_Unverified_DefersOneHour`: status Pending, `NextAttemptAt == Now.AddHours(1)`, an `Email_Deferred` event with `why=unverified`, refresher calls = 1.
  - `Onboarding_UnverifiedThenVerified_Sends`: flip `Result` and the database flag, advance the clock 1 h, tick, and expect one sent message.
  - `Onboarding_UnverifiedPastLifetime_Expires`: the existing expiry still wins.
  - `OtherCampaign_Unverified_StillSkipped`: `AssertSkipped(row, "unverified")` with a `TestCampaign`.
- [ ] **Step 2:** Run. Expect FAIL.
- [ ] **Step 3:** Implement. Keep the existing order of gates otherwise.
- [ ] **Step 4:** Run `EmailDispatcherTests`, `EmailPipelineEndToEndTests` and `OnboardingCampaignTests`. Expect PASS.
- [ ] **Step 5:** Run `dotnet format`, then commit `feat(email): defer unverified onboarding emails instead of skipping`.

### Task A6: `POST /api/Users/me/verification-email` (§7.4)

**Files:**
- Modify: `PrintLogApi/Controllers/UsersController.cs`:
  - `[HttpPost("me/verification-email")]`
  - `[Authorize(Policy = "InteractiveUserOnly")]`
  - `public async Task<IActionResult> ResendVerificationEmail(CancellationToken ct)`
  - Inject `IEmailVerificationRefresher`, `IAuth0Service` and `IMemoryCache`.
  - Add a private `CodedProblem(int, string, string)` helper copied from `FilamentsController.cs:512-521`.
- Test: `PrintLogApi.IntegrationTests/Controllers/VerificationEmailEndpointTests.cs`

The action, in order:
1. If `RefreshAsync` returns true: `409`, code `already_verified`.
2. If the throttle flag `$"verification_email_sent:{userId}"` exists: `429`, code `rate_limited`, with `Response.Headers.RetryAfter` set to the remaining seconds, rounded up and at least 1. Store the flag's expiry instant as its value so the remaining time can be computed.
3. Call `SendVerificationEmail`. On `Auth0ApiException` or `HttpRequestException`: `502`, code `auth0_unavailable`, and don't set the flag.
4. Set the flag (5 min, `SmallEntryBytes`), track `VerificationEmail_Sent {UserId}`, and return `204`.

- [ ] **Step 1: Write the failing tests.**
  - `Unverified_Sends204_AndCallsAuth0`
  - `SecondCallWithin5Min_Returns429WithRetryAfter`: the header is between 1 and 300.
  - `Verified_Returns409AlreadyVerified`
  - `Auth0Fails_Returns502_AndDoesNotThrottle`: a second call is also 502, not 429.
  - `ApiKey_IsRejected`
  - Add the OpenAPI `InlineData` row.
- [ ] **Step 2:** Run. Expect FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run. Expect PASS.
- [ ] **Step 5:** Run `dotnet format`, then commit `feat(onboarding): resend verification email endpoint`.

### Task A7: Telemetry and CORS (§8.2, §8.3)

**Files:**
- Modify: `PrintLogApi/Users/UserService.cs:81`. Change to `telemetry.TrackEvent("UserSignedUp", new Dictionary<string, string> { ["UserId"] = newUser.Id.ToString(CultureInfo.InvariantCulture), ["identityProvider"] = <authUserId before '|', or "unknown"> })`.
- Modify: `PrintLogApi/Startup.cs:969-972`. Add `.WithExposedHeaders("Request-Context")`.
- Test:
  - `PrintLogApi.IntegrationTests/Telemetry/TelemetryEventTests.cs:123-138`: extend `Creating_a_user_emits_UserSignedUp_once` with property asserts. An authId of `google-oauth2|123` gives `identityProvider == "google-oauth2"`, and `UserId` equals the created id.
  - A new `Cors_ExposesRequestContextHeader` test in a suitable existing CORS or startup test file, or a new `Controllers/CorsTests.cs`. Send `Origin: https://www.3dprintlog.com` and assert `Access-Control-Expose-Headers` contains `Request-Context`.

- [ ] **Step 1:** Write the failing asserts. Run. Expect FAIL.
- [ ] **Step 2:** Implement both changes.
- [ ] **Step 3:** Run the tests. Expect PASS.
- [ ] **Step 4:** Run `dotnet format`, then commit `feat(telemetry): user id and provider on UserSignedUp; expose Request-Context`.

### Task A8: Funnel docs, workbook tile and the spec copy

**Files:**
- Create: `docs/onboarding-metrics.md`, following the style of `docs/email-metrics.md`:
  - one paragraph explaining the weekly sign-up group funnel and that the UI's `Auth_SignedUp` gives the source;
  - a Kusto block;
  - caveats: data only from the UI release onward; the query joins on `user_AuthenticatedId`.
- Modify: `monitoring/build_workbook.py`:
  - add `onboarding_items()` returning one `query("onboarding-funnel", "New-user onboarding funnel (weekly sign-up groups)", …, "table", resources=["{ApiResource}", "{UiResource}"], width=100, time_param="UsageTimeRange")`;
  - include it in `usage_tab()`;
  - regenerate `workbook.json` with `python build_workbook.py`.
- Copy: the spec and this plan to `docs/superpowers/specs/` and `docs/superpowers/plans/` (the API repo commits them).

The Kusto query:
- **Sign-up group:** API `customEvents` `UserSignedUp`, where `UserId` comes from `customDimensions`. Weekly group = `startofweek(timestamp)`. Source = a left join to UI `customEvents` `Auth_SignedUp` on `user_AuthenticatedId`.
- **Firsts:** the minimum timestamp per user of `PrinterAdded`/`McpPrinterAdded`, `FilamentAdd` and `PrintAdded`, filtered to `customDimensions.AspNetCoreEnvironment == 'Production'`.
- **Returned:** any request on a day after the sign-up day.
- **Output columns:** `Cohort`, `Source`, `SignedUp`, `PrinterPct`, `MaterialPct`, `Print24hPct`, `Print7dPct`, `ReturnedPct`.

- [ ] **Step 1:** Write the doc and the tile. Run `python monitoring/build_workbook.py`, then `python monitoring/validate_queries.py`. It runs the panels read-only against App Insights through the logged-in `az` CLI. Expect "0 failing panels". If `validate_queries.py` can't authenticate, record that in the PR and continue.
- [ ] **Step 2:** Commit `docs(onboarding): funnel metrics doc, workbook tile, spec and plan`.

### Task A9: The API PR

- [ ] **Step 1:** Run `dotnet build --configuration Release`, `dotnet test --verbosity quiet` and `dotnet format --verify-no-changes`. Expect all green. Run `dotnet ef migrations has-pending-model-changes --project PrintLogApi`. Expect none.
- [ ] **Step 2:** Push the branch and open a PR to `main`: "feat: new-user onboarding API (status, verification refresh, resend, telemetry)". The body lists:
  - what's included;
  - that `Onboarding:LaunchDate` null keeps it off;
  - the manual steps: Auth0 `update:users` scope, `Onboarding__LaunchDate`, `monitoring/deploy.ps1`;
  - the link to the UI PR, added later.

---

## UI track (`3d-print-log-ui-worktrees/onboarding`)

**Setup** (once, before U1): give the worktree its own `node_modules`. Running `npm ci` in the worktree is simplest; a junction to the main checkout's `node_modules` works if the lockfiles match.

**Commands:** `npm run test:brief | tail -20` (read `RESULT:`), `npm run lint:brief`, `npm run test:scripts`, `npm run typecheck:cypress`. To focus specs, use `ng test --include` with a path.

### Task U1: Settings enum, `OnboardingApiService`, `OnboardingService`

**Files:**
- Modify: `src/app/core/services/user-setting.service.ts`, the enum (L7-60). Add `Onboarding_WelcomeFinishedAt = 29` and `Onboarding_ChecklistDismissedAt = 30`, each with a doc comment.
- Create: `src/app/onboarding/onboarding-api.service.ts`
- Create: `src/app/onboarding/onboarding.service.ts`
- Test: `src/app/onboarding/onboarding.service.spec.ts` and `onboarding-api.service.spec.ts`

**Interfaces:**
- Produces:

```ts
export interface OnboardingStatus { onboardingEligible: boolean; createdDate: string | null; emailVerified: boolean; }
export const INELIGIBLE_STATUS: OnboardingStatus = { onboardingEligible: false, createdDate: null, emailVerified: true };
export type ResendResult = 'sent' | 'already-verified' | 'rate-limited' | 'failed';
export const GETTING_STARTED_KEYS = { printer: 'first-printer', material: 'first-material', print: 'first-print' } as const;

// OnboardingApiService (providedIn root)
getStatus(): Observable<OnboardingStatus>              // GET {printLogApiUrl}/api/Users/me/onboarding; any error -> INELIGIBLE_STATUS
resendVerificationEmail(): Observable<ResendResult>   // POST .../me/verification-email; 204 sent, 409 already-verified, 429 rate-limited, else failed

// OnboardingService (providedIn root)
status(force?: boolean): Promise<OnboardingStatus>     // cached per page load; force re-fetches
shouldShowWelcome(targetUrl: string): Promise<boolean> // §5.3; never rejects (false on error)
markWelcomeFinished(): Promise<void>                  // writes 29 = now ISO if eligible and unset
acknowledgeEmailNotice(): Promise<void>                // writes 26 if eligible and unset
dismissChecklist(): Promise<void>                      // writes 30
checklistVisible(me: MyAchievements | null): Promise<boolean> // eligible && 30 unset && me != null && !hasEarned(me,'first-print')
static hasEarned(me: MyAchievements, key: string): boolean
clear(): void                                          // drop cached status (called on logout)
```

Rules:
- **Printer count** comes from `PrinterService.getCurrentUserPrinterSummaries(1, 1, '', false)`, reading `paging.totalCount`.
- **Target URL match:** strip the query and fragment, then the path must be exactly `/prints` or `/`.
- **Writes** go through `UserSettingService.addOrUpdateSetting`. A rejected write is logged with `LoggingService.logException` and swallowed.
- **Idempotency** (Review Focus 5): re-read the setting with `getCurrentUsersSettingByType` right before writing, and skip the write if it is set.

- [ ] **Step 1: Write the failing tests.**
  - **`shouldShowWelcome`,** as a table spec with one `it` per row:

    | Eligible | Setting 29 | Printers | Target | Expected |
    |---|---|---|---|---|
    | true | unset | 0 | `/prints` | true |
    | true | unset | 0 | `/` | true |
    | true | unset | 0 | `/prints?x=1` | true |
    | true | unset | 0 | `/prints/new/cura?settingId=1` | false |
    | true | unset | 0 | `/settings` | false |
    | true | unset | 1 | `/prints` | false |
    | true | set | 0 | `/prints` | false |
    | false | unset | 0 | `/prints` | false |

    Plus: status lookup throws → false; settings lookup throws → false; printers call errors → false.
  - **`checklistVisible`:** eligible with no badges → true; holds `first-print` → false; 30 set → false; `me` null → false; ineligible → false.
  - **`markWelcomeFinished`:**
    - writes 29 once;
    - doesn't write when it is already set;
    - doesn't write when ineligible;
    - doesn't throw when the write fails;
    - two concurrent calls write at most once (the second sees it set after re-reading).
  - **`acknowledgeEmailNotice`:** the same shape for setting 26.
  - **`OnboardingApiService`:** a 404 maps to `INELIGIBLE_STATUS`; resend status codes map to `ResendResult` (204, 409, 429, 500), using `HttpTestingController`.
- [ ] **Step 2:** Run `ng test --include src/app/onboarding`. Expect FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run. Expect PASS.
- [ ] **Step 5:** Commit `feat(onboarding): onboarding service and status api`.

### Task U2: App Insights user context and correlation (§8.1, §8.2)

**Files:**
- Modify: `src/app/core/services/logging.service.ts`:
  - Add `setAuthenticatedUser(id: number): void` and `clearAuthenticatedUser(): void`. Before the SDK is active they store a pending id, and `activate()` applies it. After that they call `appInsights.setAuthenticatedUserContext(String(id), undefined, true)` or `clearAuthenticatedUserContext()`.
  - In `initialize()` config (L111-145): `distributedTracingMode: DistributedTracingModes.AI_AND_W3C` and `correlationHeaderDomains: [new URL(environment.printLogApiUrl).host]`.
- Modify: `src/app/core/services/auth.service.ts`. Where `userProfileSubject$.next(...)` emits a real profile, call `loggingService.setAuthenticatedUser(profile.id)`. In `logout()` call `loggingService.clearAuthenticatedUser()` and `onboardingService.clear()`. Watch for a circular injection between `LoggingService` and `AuthService`: inject lazily with `Injector` if needed.
- Test: `logging.service.spec.ts`, which covers:
  - the pending id is applied on activate (stub the SDK the way existing specs do, or assert through the test seam the service exposes);
  - clear after set;
  - the config includes the API host.

- [ ] **Step 1:** Write the failing specs. Run. Expect FAIL.
- [ ] **Step 2:** Implement.
- [ ] **Step 3:** Run the logging and auth specs. Expect PASS.
- [ ] **Step 4:** Commit `feat(telemetry): authenticated user context and W3C correlation`.

### Task U3: Auth callback (sign-up source, welcome redirect, new-user PUT fixes)

**Files:**
- Modify: `src/app/core/services/auth.service.ts`:
  - `login(redirectPath = '/', options?: { signup?: boolean; source?: string })`. Store `appState: { target, source }`.
  - `handleAuthCallback()`:
    1. Read `appState.target` and `appState.source`.
    2. After the profile loads, call `logSignupIfNew(profile.id, source, target)`.
    3. Compute the final URL. If `await onboardingService.shouldShowWelcome(target)`, navigate to `/welcome` with state `{entry: 'callback'}`; otherwise navigate to the target.

    Keep navigation from waiting on more than these calls, and make sure any failure falls back to the target.
  - `logSignupIfNew`:
    1. Requires `status.createdDate` within 10 min of now and no `localStorage` key `pl_signup_logged_<id>`. The `localStorage` access goes in `try/catch`, browser only.
    2. Source = `source ?? (target.startsWith('/prints/new/cura') ? 'slicer' : 'direct')`.
    3. `logEvent('Auth_SignedUp', {source})`.
    4. `googleAnalytics.eventEmitter('sign_up', 'engagement', 'sign_up', source)`.
    5. Set the key.
  - **Nickname guard:** the `displayName === null` branch uses `(user.nickname ?? '').slice(0, 29) || null`, and leaves the name null rather than throwing.
  - **The PUT runs once per page load:** a private `newUserPutStarted` flag.
- Modify: `src/app/home/home.component.ts:70-73`. Use `auth.login('/prints', { signup: true, source: 'home' })`.
- Test: `auth.service.spec.ts`:
  - `Auth_SignedUp` is logged once for a new account, and not when `createdDate` is older than 10 min or the key exists;
  - the source rules (home / slicer / direct);
  - the callback navigates to `/welcome` when `shouldShowWelcome` resolves true, and to the target when it is false or rejects;
  - a missing nickname doesn't throw;
  - the PUT is called once when `getUser$` runs twice.

**Interfaces:**
- Consumes: U1's `OnboardingService.status` and `.shouldShowWelcome`.
- Produces: router state `{entry: 'callback' | 'guard' | 'link'}` on navigation to `/welcome`, read in U8.

- [ ] **Step 1:** Write the failing specs. Run. Expect FAIL.
- [ ] **Step 2:** Implement.
- [ ] **Step 3:** Run. Expect PASS.
- [ ] **Step 4:** Commit `feat(onboarding): signup source tracking and welcome redirect after callback`.

### Task U4: `InlineEmailNoticeComponent` (§6.3)

**Files:**
- Create: `src/app/onboarding/inline-email-notice/inline-email-notice.component.{ts,html,scss}`. Selector `app-inline-email-notice`. The copy is exactly as in §6.3. The "Email settings" link is `<a href="/settings#email" target="_blank" rel="noopener">`. Text uses `--app-text-secondary`.
- Modify: `src/app/shared/email-notice/email-notice.component.ts`. Hide on `/welcome`: build a `toSignal` of `router.events` `NavigationEnd` URLs (initial value `router.url`), and render only when `visible() && !url().startsWith('/welcome')`.
- Test: `inline-email-notice.component.spec.ts` (renders the copy and link), plus a new case in `email-notice.component.spec.ts`: hidden on `/welcome`, shown on `/prints`.

**Interfaces:**
- The component is presentational only. Hosts call `OnboardingService.acknowledgeEmailNotice()` on the user's action.

- [ ] **Step 1:** Write the failing specs. Run. Expect FAIL.
- [ ] **Step 2:** Implement, then run prettier on the template.
- [ ] **Step 3:** Run. Expect PASS.
- [ ] **Step 4:** Commit `feat(onboarding): inline email notice; hide banner on /welcome`.

### Task U5: `QuickAddPrinterComponent` and its dialog (§6.1)

**Files:**
- Create: `src/app/onboarding/quick-add-printer/quick-add-printer.component.{ts,html,scss}` and `quick-add-printer-dialog.component.ts`
- Create: `src/app/onboarding/quick-add-printer/printer-catalog.ts`. It exports `loadPrinterCatalog(): Promise<{make: string; model: string}[]>`, which dynamic-imports `../../printer/printer-detail/cura-exported-printers` and caches the promise. It also exports `searchCatalog(list, query, limit = 20)`, a case-insensitive match on `"${make} ${model}"` that contains every whitespace-separated token.
- Test: `quick-add-printer.component.spec.ts`, `printer-catalog.spec.ts`

**Interfaces:**
- Consumes:
  - `PrinterService.addPrinter(PrinterDetail)`;
  - `PrinterCategoryService.getPrinterCategories()`;
  - `LoggingService.logEvent`;
  - U4's component;
  - U1's `OnboardingService.acknowledgeEmailNotice`.
- Produces:

```ts
// QuickAddPrinterComponent
source = input.required<'welcome' | 'print-form' | 'checklist'>();
showEmailNotice = input(false);
saved = output<PrinterDetail>();
// QuickAddPrinterDialogComponent: open via
openQuickAddPrinterDialog(dialog: MatDialog, data: { source: 'print-form' | 'checklist'; showEmailNotice: boolean }): MatDialogRef<QuickAddPrinterDialogComponent, PrinterDetail | undefined>
```

**Form:**
- **Make/model:** a `search` control whose `mat-autocomplete` options come from `searchCatalog`. Selecting an option fills the `make` and `model` controls.
- **Free text:** on blur with no selection, split at the first space into make and the rest into model; a single word goes into both. The make/model controls sit under "Not listed?".
- **Name:** follows `model` until the user edits it (a `nameTouched` flag on `input`).
- **Type:** a `mat-button-toggle-group` with `FFF` (default) / `MSLA` (labeled Resin) / `Other`. Other shows a select of the remaining category nicknames.
- **Multi-material:** a toggle that reveals `slotCount` (2-64, default 4). Off means 1.
- **Save:**
  - builds `PrinterDetail { id: null, name, make, model, description: '', isActive: true, loadedFilaments: [], category: <PrinterCategory by nickname>, slotCount }`;
  - the button is disabled while saving;
  - on success it calls `acknowledgeEmailNotice()` when `showEmailNotice()`, then logs `QuickAddPrinter_Saved {source, fromCatalog, multiSlot}` and emits `saved`;
  - on error it shows the API message inline.
- **On init:** log `QuickAddPrinter_Opened {source}`.

**Dialog:** `width: 'min(30rem, 100vw - 2rem)'`, `maxWidth: '100vw'`, `autoFocus: 'first-tabbable'`. On phones (BreakpointObserver `(max-width: 599.98px)`) use `width/height: '100vw'/'100svh'` and the panel class `app-fullscreen-dialog`, defined in global styles with no border radius. The dialog closes with the saved printer.

- [ ] **Step 1: Write the failing tests.**
  - `searchCatalog`: "bambu x1" finds `Bambu Lab X1 Carbon`.
  - Selecting a catalog row fills make, model and name.
  - Free text "Voron 2.4" splits into make `Voron`, model `2.4`.
  - Name stops following once it's edited.
  - Slot toggle off → `slotCount` 1.
  - Save sends a `PrinterDetail` with `category.nickname === 'FFF'`.
  - A double click calls `addPrinter` once.
  - `acknowledgeEmailNotice` is called only when `showEmailNotice` is true.
  - A save error shows the message and `saved` doesn't emit.
  - The dialog closes with the printer.
- [ ] **Step 2:** Run. Expect FAIL.
- [ ] **Step 3:** Implement, then run prettier on the templates.
- [ ] **Step 4:** Run. Expect PASS.
- [ ] **Step 5:** Commit `feat(onboarding): quick-add printer component and dialog`.

### Task U6: `QuickAddMaterialComponent` (§6.2)

**Files:**
- Create: `src/app/onboarding/quick-add-material/quick-add-material.component.{ts,html,scss}`
- Test: `quick-add-material.component.spec.ts`

**Interfaces:**
- Consumes: `MaterialService.getMaterials()`, `FilamentService.addFilament(FilamentDetail)`, `PrinterService.loadSlot(printerId, 0, filamentId)`.
- Produces: `loadIntoPrinterId = input<number | null>(null)`; `saved = output<FilamentDetail>()`.

**Form:**
- **Material type:** an autocomplete over materials whose `materialCategoryNickname === 'filament'`. It pre-selects the material whose `acronym` or `name` is `PLA` (case-insensitive). Selecting one sets density from `densityGramPerCubicCm`.
- **Brand:** optional.
- **Color name:** optional.
- **Color:** an optional `<input type="color">`. The hex is stored without `#`, default `ffffff`.
- **Display name:** follows `[brand, type, color].filter(Boolean).join(' ')` until edited.

**Save:**
- Builds a `FilamentDetail`, using `cypress/support/commands.js` (L112-) as the minimal field set:
  - `materialCategoryNickname: 'filament'`
  - `source: FilamentSourceMeasurement.Weight`
  - `initialNominalWeightMg: 1_000_000`
  - `diameterMm: 1.75`
  - `colors: [hex]`
  - `colorPattern: Solid`
  - `finishType: Standard`
  - `effects: []`
  - `isActive: true`
  - `filamentAdjustments: []`
- If `loadIntoPrinterId()` is set, then calls `loadSlot`. A `loadSlot` failure still emits `saved`, logged with `logException`.
- Logs `QuickAddMaterial_Saved {materialType, loadedIntoPrinter}`.
- The button is disabled while saving.

- [ ] **Step 1: Write the failing tests.**
  - PLA pre-selected and density filled.
  - Switching to PETG updates density and the name.
  - Save posts weight 1,000,000 mg.
  - `loadSlot(7, 0, id)` is called when `loadIntoPrinterId` is 7, and not called when it is null.
  - A `loadSlot` error still emits.
- [ ] **Step 2:** Run. Expect FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run. Expect PASS.
- [ ] **Step 5:** Commit `feat(onboarding): quick-add material component`.

### Task U7: The `/welcome` route, redirect guard and SWA rules

**Files:**
- Create: `src/app/onboarding/onboarding.routes.ts`. `ONBOARDING_ROUTES = [{ path: '', providers: [importProvidersFrom(SharedModule)], children: [{ path: '', loadComponent: () => import('./welcome/welcome.component').then(m => m.WelcomeComponent) }] }]`
- Create: `src/app/onboarding/welcome-redirect.guard.ts`, exporting `welcomeRedirectGuard: CanActivateFn`. It returns `router.createUrlTree(['/welcome'])` when `await shouldShowWelcome(state.url)`, otherwise true. Under `environment.devAuthBypass` it still runs, so E2E can exercise it.
- Create: a placeholder `src/app/onboarding/welcome/welcome.component.ts` (heading only; U8 fills it in).
- Modify: `src/app/app.routes.ts`. Add `{ path: 'welcome', loadChildren: () => import('./onboarding/onboarding.routes').then(m => m.ONBOARDING_ROUTES), canActivate: [authGuard] }`.
- Modify: `src/app/print/print.routes.ts:44-59`. On the list route `''`, use `canActivate: [authGuard, welcomeRedirectGuard]`.
- Modify: `src/staticwebapp.config.json`. Add `/welcome` and `/welcome/*` rewrites next to `/achievements` (L82-83).
- Test: `welcome-redirect.guard.spec.ts` (true → a UrlTree for `/welcome`; false → true; rejection → true), plus `npm run test:scripts` for `swa-routes.test.mjs`.

- [ ] **Step 1:** Write the failing guard spec. Run. Expect FAIL.
- [ ] **Step 2:** Implement the routes, guard, placeholder and SWA rules.
- [ ] **Step 3:** Run the guard spec and `npm run test:scripts`. Expect PASS.
- [ ] **Step 4:** Commit `feat(onboarding): /welcome route and redirect guard`.

### Task U8: The welcome stepper (§3.1)

**Files:**
- Modify: `src/app/onboarding/welcome/welcome.component.{ts,html,scss}`
- Create: `src/app/onboarding/welcome/first-print-step/first-print-step.component.{ts,html,scss}` (step 3 cards and the confirm-email line)
- Test: `welcome.component.spec.ts`, `first-print-step.component.spec.ts`

**Interfaces:**
- Consumes: U1, U4, U5 (inline mode, `source='welcome'`), U6, `PrinterService.getCurrentUserPrinterSummaries`, `OnboardingApiService.resendVerificationEmail`, and `isCordova` from `core/utils/platform`.

Behavior, per §3.1:
- **Layout:** a `mat-stepper` (linear false), vertical below 600px and horizontal above, with `?step=3` opening step 3.
- **Step 1:**
  - chips of saved printers, loaded from the summary on init so an existing printer counts;
  - Continue is enabled when there is at least one printer;
  - "Add another printer" resets the form;
  - "Use the full printer form" goes to `/printers/new?returnUrl=/welcome`.
- **Step 2:** `QuickAddMaterialComponent`, with `loadIntoPrinterId` set to the only printer's id when there is exactly one. "Skip for now" logs `Welcome_StepSkipped {step: 2}`.
- **Step 3:**
  - **Card 1** ("Log a print now") calls `finish()` and navigates to `/prints/new/edit?onboarding=1`. It logs `Welcome_PathChosen {path: 'manual'}`.
  - **Card 2:** a slicer select (Cura → `/docs/cura-plugin`; PrusaSlicer, OrcaSlicer, Bambu Studio, Creality Print, Snapmaker Orca, Anycubic, Elegoo, QIDI, Orca Flashforge → `/docs/slic3r-uploader`), with two install lines and the doc link.
  - **Card 3:** Klipper → `/docs/printlog-bridge`, OctoPrint → `/docs/octoprint-webhook`, plus an `/api-keys` link.
  - Cards 2 and 3 have **Done**, which calls `finish()` and navigates to `/prints`, logging `Welcome_PathChosen {path, target}`.
  - Cards 2 and 3 are hidden when `isCordova`.
- **Footer on every step:** `app-inline-email-notice` and "Skip setup", which calls `finish()`, logs `Welcome_Skipped {atStep}` and goes to `/prints`.
- **`finish()`:** awaits `acknowledgeEmailNotice()` and `markWelcomeFinished()`. It always navigates, even if the writes fail.
- **On init:** log `Welcome_Shown {entry: history.state.entry ?? 'link'}` and `googleAnalytics.eventEmitter('tutorial_begin', 'engagement', 'tutorial_begin')`. Each Continue logs `Welcome_StepCompleted {step, printerCount}`.
- **Confirm-email line:** shown when `status().emailVerified === false`. Resend is disabled while in flight. The results:
  - `sent`: "Sent. Check your inbox."
  - `rate-limited`: "We just sent one. Check your inbox and spam folder."
  - `already-verified`: hide the line.
  - `failed`: "Couldn't send right now. Try again in a minute."

  Logs `VerificationEmail_Resent {result}`.
- **No push permission prompt anywhere.**

- [ ] **Step 1: Write the failing tests.**
  - Continue is disabled with 0 printers and enabled after `saved`.
  - An existing printer from the summary enables Continue.
  - Skip setup writes 26 and 29 (spy `acknowledgeEmailNotice` and `markWelcomeFinished`), then navigates to `/prints`.
  - Skip still navigates when `markWelcomeFinished` rejects.
  - Card 1 navigates to `/prints/new/edit` with `queryParams {onboarding: '1'}`.
  - Cards 2 and 3 are hidden under Cordova (override the injection or wrap `isCordova` in an injectable token `IS_CORDOVA`; add it if missing).
  - `?step=3` selects index 2.
  - Each resend result shows its copy.
  - A double click on Resend makes one call.
  - Material step `loadIntoPrinterId` equals the only printer's id, and is null with two printers.
- [ ] **Step 2:** Run. Expect FAIL.
- [ ] **Step 3:** Implement, then run prettier on the templates.
- [ ] **Step 4:** Run. Expect PASS. Run `npm run lint:brief`.
- [ ] **Step 5:** Commit `feat(onboarding): welcome stepper`.

### Task U9: Print form (add a printer inline, `onboarding=1`, first-print conversion)

**Files:**
- Modify: `src/app/print/edit-print-detail/edit-print-detail.component.ts` and `.html` (L129-147).
- Test: `edit-print-detail.component.spec.ts`. The print list still uses `PrinterRedirectPromptService` until U10 deletes it.

**Interfaces:**
- Consumes: U5's `openQuickAddPrinterDialog`; U1's `OnboardingService.status`, `.markWelcomeFinished` and `.hasEarned`; `AchievementService.me()`; `PrinterService.getCurrentUserPrinterSummaries(1, 100, undefined, false)`.

Changes:
1. **The printer select.** After the `@for`, add `<mat-option [value]="ADD_PRINTER_SENTINEL" class="add-printer-option">+ Add a printer</mat-option>`, with `ADD_PRINTER_SENTINEL = -1`.
   - The `printerId` valueChanges handler (L852-881) intercepts -1 first: it restores the previous value with `setValue(prev, {emitEvent: false})`, opens the dialog and returns, so `Prints_LastSelectedPrinterId` is never saved as -1.
   - When `printers.length === 0`, render an inline block instead of the field: "Add the printer you printed this on" and an "Add printer" button.
   - **Dialog close with a printer:**
     1. Re-fetch the summaries into `this.printers` and call `buildPrinterLabelCache()`.
     2. Call `printerId.setValue(newId)`, which runs the existing loaded-filament auto-fill.
     3. Call `markForCheck()`.

     No other control changes.
   - **Dialog data:** `{source: 'print-form', showEmailNotice: status.onboardingEligible}`.
2. **Remove the toast** (L602-627) and the `PrinterRedirectPromptService` injection, fields and unsubscribe here.
3. **`onboarding=1`:**
   - Read it with `activatedRoute.snapshot.queryParamMap.get('onboarding') === '1'` into a signal `onboardingMode`.
   - For a new print in that mode:
     - set `title` to "My first print" (only if empty);
     - set `printerId` to the highest id in `this.printers`, the newest;
     - render a strip above the form: "Getting started · Log your first print" with a "Back" `routerLink="/welcome"` and `[queryParams]="{step: 3}"`.
4. **First print:**
   - In `ngOnInit`, for a new print, load `achievementService.me()` once into `hadFirstPrint: boolean | null`. Use `OnboardingService.hasEarned(me, 'first-print')`; on error, null.
   - In `handleSaveSuccess` (L1859-1874), for a new print:
     - fire `emitConversion` only when `hadFirstPrint !== true` (null keeps today's behavior of firing);
     - when the status is eligible and `hadFirstPrint === false`, call `googleAnalytics.eventEmitter('tutorial_complete', 'engagement', 'tutorial_complete')`;
     - always call `onboardingService.markWelcomeFinished()`, fire-and-forget (it's a no-op for ineligible users).

- [ ] **Step 1: Write the failing tests.**
  - **Review Focus 1:** with a Cura-resolved print (title, note and usage rows set), selecting "+ Add a printer", then closing the dialog with printer id 42:
    - `printerId === 42`;
    - title, note and usage rows are unchanged;
    - `Prints_LastSelectedPrinterId` is never written with -1.
  - Zero printers renders the inline block; clicking it opens the dialog.
  - Cancel restores the previous `printerId`.
  - No toast is shown with zero printers.
  - `onboarding=1` pre-fills "My first print" and the newest printer, and shows the strip.
  - Without the param, no strip and an empty title.
  - The conversion doesn't fire when `hadFirstPrint` is true, and fires when false or null.
  - `tutorial_complete` only for eligible + false.
  - `markWelcomeFinished` is called on a new-print save.
- [ ] **Step 2:** Run `ng test --include src/app/print/edit-print-detail`. Expect FAIL.
- [ ] **Step 3:** Implement. The component is large, so keep the new logic in small private methods: `openAddPrinter()`, `applyOnboardingDefaults()`, `afterNewPrintSaved()`. Run prettier on the template.
- [ ] **Step 4:** Run. Expect PASS.
- [ ] **Step 5:** Commit `feat(onboarding): add a printer from the print form; first-print conversion`.

### Task U10: Print list (checklist, toast suppression, empty state)

**Files:**
- Create: `src/app/onboarding/getting-started-checklist/getting-started-checklist.component.{ts,html,scss}`
- Create: `src/app/print/print-list/print-list-hint-slot/print-list-hint-slot.component.ts`. It renders `<app-getting-started-checklist>` when the checklist is visible and `<app-next-achievement-hint>` otherwise, and owns a single `me()` fetch.
- Modify: `print-list.component.html:162-163`. Replace `<app-next-achievement-hint />` with `<app-print-list-hint-slot />`.
- Modify: `print-list.component.ts`:
  - Add `onboardingActive = signal(false)`, set from the slot's output: `<app-print-list-hint-slot (checklistActive)="onboardingActive.set($event)" />`. `syncAddPrinterPrompt()` (L763-781) dismisses the toast and returns early while `onboardingActive()` is true. It is called again whenever the signal changes.
  - Replace the `PrinterRedirectPromptService` use (L648-664) with a direct `printerService.getCurrentUserPrinterSummaries(1, 1, '', false)` mapped to `totalCount === 0`, keeping the same error behavior.
  - Show the toast at most once per session with a module-level `let addPrinterToastShown = false`, set it when the toast shows, and add `queryParams: {returnUrl: '/prints'}` to its navigation.
- Delete: `src/app/print/services/printer-redirect-prompt.service.ts` and `.spec.ts`. Update the specs that mock it: `print-list.component.spec.ts` (L92-163, 465, 611, 677) and `edit-print-detail.component.spec.ts` (L90, 151).
- Modify: `print-empty-state.component.html:18-40`. Add `[queryParams]="{ returnUrl: '/prints' }"` to the add-printer button.
- Test: `getting-started-checklist.component.spec.ts`, `print-list-hint-slot.component.spec.ts`, `print-list.component.spec.ts` updates, `print-empty-state.component.spec.ts`.

**Interfaces:**
- Consumes: U1 (`checklistVisible`, `dismissChecklist`, `hasEarned`, `GETTING_STARTED_KEYS`) and U5 (`openQuickAddPrinterDialog` with `source: 'checklist'`).
- Produces: `PrintListHintSlotComponent.checklistActive = output<boolean>()`.

**Checklist (§4.1):**
- **Items:**

  | Item | Done when | Link |
  |---|---|---|
  | Add a printer | `first-printer` | opens the dialog |
  | Add a material (optional) | `first-material` | `/materials/new` |
  | Log your first print | `first-print` | `/prints/new/edit?onboarding=1` |

- **Progress text:** `"{n} of 2 done"`, counting the printer and print items only.
- **Secondary link:** "Send prints automatically", to `/welcome?step=3`.
- **Dismiss** (an icon button, aria-label "Dismiss getting started") calls `dismissChecklist()`, hides the card and emits `checklistActive(false)`.
- **Events:** `GettingStarted_Shown` once per render, `GettingStarted_ItemClicked {item}`, `GettingStarted_Dismissed {doneCount}`.
- **After the dialog saves:** re-fetch `me()`, since badges are granted after commit; retry once after 2 s if `first-printer` hasn't appeared yet.

- [ ] **Step 1: Write the failing tests.**
  - The slot shows the checklist for an eligible user without `first-print`, and the hint for an ineligible user (Review Focus 2) or after `first-print`.
  - `me()` failing → the hint component, which handles its own error, and `checklistActive` false.
  - Progress "1 of 2 done" when only `first-printer` is held.
  - Dismiss calls `dismissChecklist` and swaps to the hint.
  - The print list doesn't show the add-printer toast while `onboardingActive` is true, and shows it at most once otherwise.
  - The toast tap navigates with `returnUrl`.
  - The empty-state button carries `returnUrl=/prints`.
- [ ] **Step 2:** Run. Expect FAIL.
- [ ] **Step 3:** Implement and delete the service. Run prettier on the templates.
- [ ] **Step 4:** Run `npm run test:brief | tail -20`. Expect `RESULT: PASSED`.
- [ ] **Step 5:** Commit `feat(onboarding): getting started checklist on /prints; one card at a time`.

### Task U11: Navbar `/users/null` fix (§9.1)

**Files:**
- Modify: `src/app/shared/navbar/navbar.component.ts` (35-68). `userId = computed(() => user()?.id ?? null)`, with no profile-picture condition.
- Modify: `navbar.component.html` (the `#profileMenu` My Profile item). Wrap it in `@if (userId() !== null)`.
- Test: `navbar.component.spec.ts`: with a profile that has no picture, the link is `/users/5`; with a null profile there is no link.

- [ ] **Step 1:** Write the failing spec. Run. Expect FAIL.
- [ ] **Step 2:** Implement, then run prettier.
- [ ] **Step 3:** Run. Expect PASS.
- [ ] **Step 4:** Commit `fix(navbar): profile link no longer points to /users/null`.

### Task U12: Cypress end-to-end and the full check

**Files:**
- Create: `cypress/e2e/onboarding/new-user-onboarding.cy.ts`

**Approach:**
- Use `cy.login()` (dev bypass) with a fresh `?devUserId=<Date.now() % 1e6 + 900000>` on the first visit, which persists per tab.
- Confirm that the API's dev auth creates an unseen dev user on first request (read `PrintLogApi/Authentication` in the API worktree). If it doesn't, intercept `GET **/api/printers/summary*` with `{items: [], paging: {totalCount: 0}}` until the printer is saved.
- Always intercept `GET **/api/Users/me/onboarding` with `{onboardingEligible: true, createdDate: <now ISO>, emailVerified: true}`.

**Flow:** visit `/prints` → expect `/welcome` → quick-add "Prusa i3 MK3S" → Continue → Skip for now → Log a print now → expect `/prints/new/edit?onboarding=1`, the title "My first print" and a printer selected → submit → `/prints`, and the checklist is gone or shows the hint card.

- [ ] **Step 1:** Write the spec. Run `npm run typecheck:cypress`. Expect no errors.
- [ ] **Step 2:** Run it only when the API worktree can run in `E2ETesting` mode on port 5101 and the UI with the e2e config on port 4310, so Chris's servers on 4200 and 5000 are left alone: `CYPRESS_baseUrl=https://localhost:4310 CYPRESS_apiUrl=http://localhost:5101 npx cypress run --spec cypress/e2e/onboarding/new-user-onboarding.cy.ts`. The e2e environment points at `localhost:5000`, so serve the UI with an override, or add the port to the e2e config for this run without committing it. If the stack can't be started, leave the spec committed and note "not run" in the PR. Kill both servers by port afterwards.
- [ ] **Step 3:** Run the full checks: `npm run test:brief | tail -20` (`RESULT: PASSED`), `npm run lint:brief` (clean), `npm run test:scripts` (pass), `npm run prettier` (only the pre-existing `settings.component.html` warning), `npm run build` (succeeds, prerender verified).
- [ ] **Step 4:** Commit `test(onboarding): cypress new-user flow`.

### Task U13: Visual check and the UI PR

- [ ] **Step 1:** Serve the UI worktree on port 4310 against the API worktree on port 5101. With the dev bypass and a fresh dev user, plus a local `Onboarding__LaunchDate` in the past for the API process, take screenshots at 1280px and 390px wide:
  - each of the three welcome steps;
  - the confirm-email line;
  - a Cura-style hand-off with no printers (`/prints/new/edit` with zero printers is enough if the hand-off can't be faked);
  - the quick-add dialog;
  - the `/prints` checklist;
  - an existing user (dev user 1) on `/prints`, showing no change;
  - dark mode, for the stepper and the checklist.

  Look at every screenshot. Check for overlap, text that doesn't fit, a dialog that isn't full screen on a phone, and dark-mode colors. Fix what's wrong in the owning task's files and commit. Kill the servers by port afterwards.
- [ ] **Step 2:** Push, then open a PR to `main`: "feat: new-user onboarding (welcome stepper, quick add, checklist, slicer arrivals)". The body:
  - links the API PR, with "merge and deploy API first";
  - summarizes the spec;
  - attaches the screenshots;
  - lists the manual steps.

  Edit the API PR body to link this PR.

---

## Order and parallelism

- The API tasks A1–A9 and the UI tasks U1–U13 are independent until U13 (the visual check needs both). Run the two tracks side by side.
- **Within each track, run the tasks in order.**
- **UI exceptions:** U4, U5, U6 and U11 depend only on U1, so they can run in any order after it.
