# New-user onboarding design

Date: 2026-10-18
Repos: `3d-print-log-ui`, `3d-print-log-api` (one PR each, linked to each other)
Follow-up out of scope: UI #350 (Auth0 sign-up screen hint)

## 1. Why

Funnel analysis of App Insights on 2026-10-18, covering the 31 trackable accounts created Oct 6–18:

| Step | Users | % |
|---|---|---|
| Signed up and loaded the app | 31 | 100% |
| Added a printer | 11 | 35% |
| Added a material | 6 | 19% |
| Logged a print | 5 | 16% |
| Came back on a later day | 9 | 29% |

The causes:

1. **Users arriving from a slicer hit a dead end.**
   - About a third of new sign-ups come from the Cura plugin or Slic3r uploader. They land on the new-print form pre-filled.
   - `printerId` is required, so a user with no printer can't save. The dropdown is empty and offers no way to add one.
   - The "No Active Printers" toast navigates away and loses what was entered.
2. **The empty first screen.** Of the 20 users who never added a printer, 11 left within 5 minutes. Most of them saw only the empty `/prints`.
3. **Onboarding emails don't reach the people who leave.**
   - The dispatcher defers every email until the user has dismissed the email notice. Each email expires 2 days after it is due, so someone who never comes back never gets the welcome.
   - An unverified address is a terminal skip (25 skips so far). The local `EmailVerified` flag only refreshes when a new token arrives, and tokens are long-lived.
4. **The funnel can't be measured.**
   - The UI never sets the authenticated user, and UI/API operation ids don't correlate.
   - `UserSignedUp` carries no user id or source.
   - GA4 gets no custom events, and the Ads conversion fires on every print.

## 2. Goals and success

A new user is **onboarded** once they have added a printer *and* logged a first print, whether by hand, from a slicer, or through an integration. Adding a material and connecting an integration are encouraged, never required.

Targets four weeks after launch, measured on weekly sign-up groups (§8.5):

| Metric | Today | Target |
|---|---|---|
| Added a printer | 35% | 60% or more |
| First print within 7 days | 16% | 35% or more |

Constraints:

- **Existing users see no change.** Only accounts created on or after the launch date are eligible.
- **At most one card on `/prints` at a time** (§4.4).
- **Never ask for push permission** during onboarding. On Android 13+ a denial is permanent.
- **Works at phone width.** The Android app wraps the site, and slicer and integration setup is desktop-only.
- **The email notice gate stays.** It is the consent record from the email spec (2026-10-05, §8.5 and §13.1). Onboarding shows the notice; it does not bypass it.

## 3. The flow

### 3.1 Welcome stepper (`/welcome`)

Who gets it: eligible accounts that arrive on the default target (`/prints`) with no printers (§5.3).

**Step 1: Add your printer(s).**
- `QuickAddPrinterComponent` (§6.1), plus an **Add another printer** button.
- Saved printers are listed above the form as chips.
- **Continue** is enabled once at least one printer exists.
- A **Use the full printer form** link opens `/printers/new?returnUrl=/welcome`.

**Step 2: Add a material (optional).**
- `QuickAddMaterialComponent` (§6.2), with **Skip for now** next to **Continue**.
- If the user has exactly one printer, the saved spool is loaded into it.

**Step 3: Log your first print.** Three cards:
1. **Log a print now** (the main option). Opens `/prints/new/edit?onboarding=1` (§3.3).
2. **Send prints from my slicer.** Lists Cura, PrusaSlicer, OrcaSlicer, Bambu Studio and the other Slic3r-family slicers. Picking one shows two or three install lines and a link to its doc (`/docs/cura-plugin` or `/docs/slic3r-uploader`).
3. **Log prints automatically from my printer.** Klipper (printlog-bridge) or OctoPrint: a short explanation, a link to `/api-keys`, and a link to `/docs/printlog-bridge` or `/docs/octoprint-webhook`.

Cards 2 and 3 end with **Done**, which goes to `/prints`. The checklist (§4) stays until the first print arrives. On Cordova only card 1 shows.

**On every step:**
- A **Skip setup** link goes to `/prints`.
- `InlineEmailNoticeComponent` (§6.3) sits in the footer.
- The user's own action ends the stepper: **Done**, **Skip setup**, or **Log a print now**. That action writes setting 29 (`Onboarding_WelcomeFinishedAt`) and, because the notice was on screen, setting 26 (`Email_NoticeSeenAt`) if it is not set yet.

The global email-notice banner is hidden on `/welcome`.

**Confirm your email.** When `/me` reports the email as unverified, step 3 shows a line: "Check your inbox to confirm your email so we can send you tips." It has a **Resend** button (§7.4). The line clears once `/me` reports the address verified (§7.3).

### 3.2 Arriving from a slicer (`/prints/new/cura?...`)

- **No redirect.** The return URL already survives Auth0 sign-up, so the user lands on the pre-filled print form.
- **"+ Add a printer" in the printer dropdown.** It is always the last option, for every user. Selecting it opens `QuickAddPrinterDialogComponent` and leaves the control's value unchanged.
- **With no printers,** the field is replaced by an inline message, "Add the printer you printed this on", and an **Add printer** button that opens the same dialog.
- **When the dialog closes with a saved printer,** the printer list reloads and the new printer is selected. Everything else on the form, including the slicer data, is kept, because nothing navigates.
- **For eligible users, the dialog shows `InlineEmailNoticeComponent` in its footer.** Saving the printer writes setting 26.
- **The slicer's usage rows are unchanged:** still labels, not matched to the user's own spools.
- **Saving the print** grants the first-print badge, which fires the existing celebration. For an eligible user it also writes setting 29 if it is unset.
- **Removed from the print form:** the "No Active Printers" toast (`edit-print-detail.component.ts` around 460–482). Remove `PrinterRedirectPromptService` too, if nothing else uses it.

### 3.3 First print from the stepper

`/prints/new/edit?onboarding=1` opens the existing form with:

- **Title** pre-filled as "My first print".
- **Printer:** the most recently added printer.
- **Material:** pre-filled through the existing loaded-material auto-fill.
- **A "Getting started" header strip** with a **Back** link to `/welcome`.

After saving, the normal navigation and celebration run. The `onboarding` param only changes presentation; validation and the save path are unchanged.

## 4. `/prints`: one card at a time

### 4.1 Getting Started checklist

`GettingStartedChecklistComponent` takes the next-achievement hint's slot (`print-list.component.html`, around 163).

**When it shows:** the user is eligible, setting 30 is unset, and the user does not have the `first-print` badge.

**Items, read from `achievements.me()` (the call the hint card already makes):**

| Item | Done when the user has | Link |
|---|---|---|
| Add a printer | `first-printer` | quick-add dialog |
| Add a material (optional) | `first-material` | `/materials/new` |
| Log your first print | `first-print` | `/prints/new/edit?onboarding=1` |

There is also a secondary link, **Send prints automatically**, to `/welcome?step=3`. The card has progress text ("1 of 2 done"; material is not counted) and a dismiss button that writes setting 30.

### 4.2 Back to normal

Once `first-print` is held, or setting 30 is set, the slot renders the existing `NextAchievementHintComponent` unchanged.

### 4.3 Toasts

The no-printer toast on `/prints` (`showAddPrinterToast`) is not shown while onboarding is active. "Active" means the user is eligible and the checklist conditions hold.

The toast's show-once flag is also fixed: `hasShownPromptBefore` has to be set when the toast shows, so it appears at most once per session.

### 4.4 Priority

The global email-notice banner and the slot card never both show for a new user, because an eligible user records the notice inside onboarding (§3.1, §3.2).

For existing users nothing changes: the banner if they haven't seen the notice, plus the normal hint card.

## 5. State and eligibility

### 5.1 API

- **New config:** `Onboarding:LaunchDate` (`DateTimeOffset?`, default null). It is bound to a new `UserOnboardingOptions` class, section `Onboarding`. That class is separate from `Email:Onboarding:StartDate`, which gates the email series.
- **Eligibility:** `onboardingEligible = LaunchDate != null && user.CreatedDate != null && user.CreatedDate >= LaunchDate`. While the date is null, the feature is off.
- **New endpoint `GET /api/Users/me/onboarding`,** `InteractiveUserOnly`, returning `OnboardingStatusDto(bool OnboardingEligible, DateTimeOffset? CreatedDate, bool EmailVerified)`. `EmailVerified` is refreshed per §7.3.
  - These fields stay off `UserDetailDto` on purpose. That DTO is also returned by the anonymous `GET /api/Users/{id}`, so they would leak onto public profiles.
  - Wherever this spec says "`/me` reports", read this endpoint.

### 5.2 Settings

The migration seeds two new setting types. Both store an ISO-8601 timestamp string, like setting 26. The UI enum in `user-setting.service.ts` mirrors them.

| Id | Name |
|---|---|
| 29 | `Onboarding_WelcomeFinishedAt` |
| 30 | `Onboarding_ChecklistDismissedAt` |

### 5.3 Entry rule

All entry rules live in the UI's `OnboardingService`.

`shouldShowWelcome(targetUrl)` is true when all of these hold:
- `onboardingEligible`
- setting 29 is unset
- the user has zero printers (from the existing printer summary)
- `targetUrl` is `/prints` or `/`, ignoring query strings

The rule is checked in two places:
- **After the callback.** `handleAuthCallback` navigates to `/welcome` in place of the target.
- **On the `/prints` list route,** by `welcomeRedirectGuard` (a functional `CanActivateFn`). It returns a `UrlTree` to `/welcome`.

Deep links (`/prints/new/...`, `/settings` and so on) are never redirected.

**Failures:**
- If `/me` or the settings lookup fails, the guard allows navigation and never shows the stepper. If the achievements lookup fails, the checklist hides.
- Onboarding must never block the app.

## 6. Shared UI components

### 6.1 `QuickAddPrinterComponent`

A standalone reactive form.

**Fields:**
- **Make/model:** one autocomplete over `cura-exported-printers.ts`, loaded with a dynamic `import()`. It matches on make and model and fills both. Free text is allowed: what's typed before the first space becomes the make, the rest the model, and both stay editable in two small fields under "Not listed?".
- **Name:** pre-filled from the model and editable until the user changes it.
- **Type:** a toggle, FFF (default) / Resin / Other. It maps to the printer category nickname.
- **Multi-material / tool changer:** a toggle. When on, it shows slot count (2–64). When off, the slot count is 1.

**Behavior:**
- Saves through the existing `PrinterService` create call with the same DTO the full form sends.
- Emits the saved printer.
- Shows API validation errors inline.

**`QuickAddPrinterDialogComponent`** wraps it in `MatDialog`: full screen below 600px, 480px wide above. It closes with the saved printer, or with undefined on cancel.

**Events:** `QuickAddPrinter_Opened` and `QuickAddPrinter_Saved` with `{source: 'welcome' | 'print-form' | 'checklist', fromCatalog, multiSlot}`.

### 6.2 `QuickAddMaterialComponent`

**Fields:**
- **Material type:** an autocomplete over `GET /api/Materials`, pre-selected to PLA. Choosing a type fills density, as the full form does.
- **Brand:** optional.
- **Color:** optional; a name plus swatch, using the full form's color control if it can be reused, otherwise a text field.
- **Display name:** pre-filled as `"{brand} {type} {color}"` (trimmed) and editable.
- **Initial weight:** 1000 g.

**Behavior:**
- Saves through the existing filament create call.
- If an input `loadIntoPrinterId` is set, it then calls the existing set-loaded-filament endpoint for slot 0.

**Event:** `QuickAddMaterial_Saved {materialType, loadedIntoPrinter}`.

### 6.3 `InlineEmailNoticeComponent`

One line: "We'll email you a few tips while you get set up, plus a monthly recap. [Email settings]". The link opens `/settings#email` in a new tab, so the stepper isn't lost.

It does not write setting 26 itself. It exposes `acknowledge()`, and the host calls it on the user's action (§3.1, §3.2). `acknowledge()` writes setting 26 only if it is unset. It is a no-op for ineligible users.

## 7. Email

### 7.1 Notice

Handled in §3.1, §3.2 and §6.3. `Notice.Required` stays true. The global banner (`app.component.html`) is hidden on `/welcome`.

### 7.2 Unverified addresses are deferred for onboarding

In `EmailDispatcher`, when the row's campaign is `onboarding` and the address is unverified:

1. Refresh the flag (§7.3).
2. If it is still unverified, `Defer(row, now + GateRecheck, "unverified")`.

The existing lifetime expiry still applies. Other campaigns keep the terminal `Skip(Unverified)`.

### 7.3 `EmailVerificationRefresher`

A scoped service: `Task<bool> RefreshAsync(User user, CancellationToken ct)`.

- **Verified users:** if `user.EmailVerified` is already true, it returns true with no call.
- **Throttle:** a per-user `IMemoryCache` flag (byte-charged per `CacheBudget`) limits it to one Auth0 lookup per hour per user. This is a flag, not a cache, which matches the reasoning in AGENTS.md.
- **Lookup:** `GET /api/v2/users/{authId}?fields=email,email_verified` through `Auth0Service`. When Auth0 says verified, it sets `user.EmailVerified = true`, updates `user.Email` if it differs, and saves.
- **Errors:** on any Auth0 failure it logs and returns the current flag. It never throws to callers.

**Called by:**
- `EmailDispatcher` (§7.2);
- `GET /api/Users/me/onboarding`, when the user is unverified.

### 7.4 Resend verification

`POST /api/Users/me/verification-email`. `InteractiveUserOnly`.

**Calls** Auth0 `POST /api/v2/jobs/verification-email` with `{user_id}`.

**Responses:**
- **`204`** on success.
- **`409`** with code `already_verified` when the refresher says the address is verified.
- **`429`** with `Retry-After` when called within 5 minutes of the last call for that user. This is a per-user flag in `IMemoryCache`.
- **`502`** with code `auth0_unavailable` when Auth0 fails.

**Requires** the `update:users` scope on the Management API client (manual step).

**UI event:** `VerificationEmail_Resent`.

### 7.5 Unchanged

The schedule (welcome at +15 min, then day 2, day 5 and day 10), lifetimes, the frequency cap and campaign content.

## 8. Tracking

### 8.1 UI user context

After `/me` loads, `LoggingService.setAuthenticatedUser(id)` calls `appInsights.setAuthenticatedUserContext(String(id), undefined, true)`. On logout it calls `clearAuthenticatedUserContext()`.

If the SDK hasn't loaded yet, the call is queued and applied on load. The SDK loads lazily, which is the pattern for anything that arrives before it.

### 8.2 Correlation

**UI App Insights config:**
- `distributedTracingMode: DistributedTracingModes.AI_AND_W3C`
- `correlationHeaderDomains: [<api host from environment.apiBase>]`
- The `*.auth0.com` exclusion stays.

**API CORS:** add `.WithExposedHeaders("Request-Context")`.

### 8.3 Sign-up source

- **Tagging the login.** `AuthService.login` takes an optional `source`, stored in `appState`. The home CTA passes `'home'`.
- **Logging it.** In `handleAuthCallback`, once `me/onboarding` loads: if its `createdDate` is within 10 minutes and the `localStorage` key `pl_signup_logged_<id>` is absent, log `Auth_SignedUp {source}` and set the key.
- **Source values:** the source from `appState`; else `'slicer'` when the target starts with `/prints/new/cura`; else `'direct'`.
- **API side.** `UserSignedUp` gains properties `UserId` (the new local id) and `identityProvider` (the Auth0 `sub` before `|`).

### 8.4 Events

**Onboarding UI events:**

| Event | Properties |
|---|---|
| `Welcome_Shown` | `{entry: callback\|guard\|link}` |
| `Welcome_StepCompleted` | `{step, printerCount}` |
| `Welcome_StepSkipped` | `{step}` |
| `Welcome_Skipped` | `{atStep}` |
| `Welcome_PathChosen` | `{path: manual\|slicer\|integration, target}` |
| `GettingStarted_Shown` | — |
| `GettingStarted_ItemClicked` | `{item}` |
| `GettingStarted_Dismissed` | `{doneCount}` |

`QuickAddPrinter_*`, `QuickAddMaterial_Saved` and `VerificationEmail_Resent` are defined in §6 and §7.4.

**GA4,** via `GoogleAnalyticsService.eventEmitter`:
- `sign_up {method: source}`, alongside `Auth_SignedUp`;
- `tutorial_begin`, on `Welcome_Shown`;
- `tutorial_complete`, when the first-print badge appears for an eligible user.

**Google Ads.** The conversion in `edit-print-detail.component.ts` (around 1716) fires only when the user did not hold `first-print` before the save.

### 8.5 Funnel reporting

API repo, `docs/onboarding-metrics.md`: the KQL for the weekly sign-up group funnel, broken down by sign-up source. Its steps:

- signed up
- printer added
- material added
- first print within 24 h
- first print within 7 days
- active on a later day

The same query is added as a tile to the `printlog-monitoring` workbook (`monitoring/main.bicep`). It is deployed by `monitoring/deploy.ps1`, a manual step.

## 9. Bug fixes included

1. **Navbar "My Profile" goes to `/users/null`.** `navbar.component.ts` sets `userId` only when there is a profile picture. Take `userId` from the user regardless, and render the link only once it is known.
2. **The print empty state doesn't bring the user back.** It promises "we will bring you straight back here" but passes no `returnUrl`. Add `returnUrl=/prints` to its add-printer link.
3. **The no-printer toast shows on every visit.** Its show-once flag is never set (§4.3).
4. **The new-user PUT can crash or run twice.** In `auth.service.ts`, `user.nickname.length` throws when the nickname is missing; guard it. The PUT can also run twice on the callback page (`localAuthSetup` and `handleAuthCallback` both call `getUser$`); make sure it runs once.

## 10. Testing

**UI unit tests (Karma/Jasmine, `npm run test:ci`):**
- `OnboardingService`: a table test over eligible, settings 29 and 30, printer count, the badges, and the target URL. It also checks that errors fail open.
- `welcomeRedirectGuard`.
- `WelcomeComponent` and its steps: Continue enabled once a printer exists, and Skip writes 29 and 26.
- `QuickAddPrinterComponent`: the catalog fill, free-text split, name pre-fill that stops once edited, and slot toggle. `QuickAddMaterialComponent`: the PLA default, density fill, and load-into-printer call.
- `InlineEmailNoticeComponent.acknowledge()`.
- The checklist's show/hide and progress.
- Print form: the "+ Add a printer" option keeps the form's values and selects the new printer, the no-printers inline state, and the `onboarding=1` pre-fill.
- Navbar profile link.
- The Ads conversion fires only on a first print.

**UI end-to-end (Cypress):** with dev auth bypass and an eligible fresh user: `/prints` → `/welcome` → add a printer → skip material → log a print now → save → `/prints` without the checklist.

**API integration tests:**
- `GET /api/Users/me/onboarding` fields and eligibility across `LaunchDate` null, before and after.
- The dispatcher defers on an unverified onboarding email and still skips other campaigns.
- The refresher, with a stubbed Auth0 handler: the throttle, a verified update, and the error path.
- The resend endpoint: 204, 409, 429 and 502.
- `UserSignedUp` properties.
- CORS exposes `Request-Context`.
- The settings migration: seeded types 29 and 30 exist, checked against SQL Server via docker on port 1434 per AGENTS.md.

**Manual:**
- **Setup:** UI on 4310 and API on 5101 in worktrees, with a fresh account.
- **Screenshots** of each step at 1280px and 390px, covering:
  - a Cura hand-off with no printers;
  - the `/prints` checklist;
  - an existing account (no change).

## 11. Rollout

1. **API PR:**
   - migration
   - the `me/onboarding` endpoint
   - refresher
   - dispatcher
   - resend endpoint
   - telemetry
   - CORS
   - docs and workbook

   It only adds things; with `LaunchDate` null the feature is off. Merge it, then a `v*` tag deploys it (Chris).
2. **UI PR:** everything else. It tolerates a missing or failing `me/onboarding` endpoint (treated as ineligible and verified). A `v*` tag deploys it (Chris).
3. **Manual steps (Chris):**
   - Grant `update:users` to the Management API client in Auth0.
   - Set `Onboarding__LaunchDate` on the API App Service to the UI release time.
   - Deploy the workbook with `monitoring/deploy.ps1`.
   - Review the funnel weekly for four weeks against §2.

## 12. Out of scope

- The Auth0 sign-up screen hint: UI #350.
- A shared filament catalog. The materials empty state's "shared catalog" copy is a separate fix.
- Matching slicer usage rows to the user's spools.
- Bridge one-step pairing.
- Any change to the email schedule or content.
