# Directory listing copy

Paste-ready text for the Claude Connectors Directory and ChatGPT plugin directory submissions
(steps 8 and 9 of `mcp-registry-listing.md`). Character limits are the portals' own; counts are
noted where a field is close to its limit.

Reviewer credentials are **not** in this file. They live in the password manager, one account per
directory, never shared between them or with the Google Play reviewer account. Each account is
populated with `scripts/seed-reviewer-account.py`, so the test prompts below have real answers.

## Shared

| Field | Value |
| --- | --- |
| Name | `3D Print Log` |
| Server URL | `https://api.3dprintlog.com/mcp` |
| Docs | `https://www.3dprintlog.com/docs/mcp` |
| Privacy policy | `https://www.3dprintlog.com/docs/privacy-policy` |
| Terms of service | `https://www.3dprintlog.com/docs/terms-of-service` (ChatGPT only) |
| Support | `hello@3dprintlog.com` |
| Icon | `docs/assets/mcp-icon-512.png`. Opaque disc on transparency, so it also serves as the dark-theme logo |

## Claude Connectors Directory

**URL slug** (permanent): `3d-print-log`

**One-liner** (≤200, 152 used):

> Track your 3D prints, printers, filament and resin inventory, and projects. Search print history, check what material is left, and log finished prints.

**Description** (≤2,000):

> 3D Print Log connects Claude to your account on 3dprintlog.com, a logbook for 3D printing.
>
> Ask about your print history: what you printed, on which printer, how long it took, how much material it used, and why a print failed. Check your filament and resin inventory before you start a job, including whether a spool has enough left for the print you're planning. See success rates and print hours per printer, and summaries over any date range.
>
> Claude can also keep the log up to date for you. It can record a finished print with its material usage, add printers and spools, correct a spool's remaining weight after you weigh it, archive spools you've used up, and organise prints into projects. Each change is a separate tool that Claude asks you to approve. There are no delete tools.
>
> The connector only ever sees your own account. It signs in with your 3D Print Log login, and every request is limited to data you own. It also includes the 3D Print Log help docs, so Claude can answer questions about how the app works.
>
> Works with FDM filament printers and resin printers. A free 3D Print Log account is enough to get started.

**Example prompts** (three, each exercising a different tool):

1. `Do I have enough white PLA left for a 240 g print?` (`find_material`)
2. `What's my success rate on the Bambu X1C over the last three months?` (`get_printer_stats`)
3. `Log the pen holder I just finished on the Prusa: 45 g of black PLA, 2.5 hours.` (`create_print`)

**Categories:** Productivity, plus the closest hobby/maker category the form offers.

**Authentication:** Anthropic-held client credentials (the `3D Print Log for Claude` Auth0 app).

**Data handling:** first-party API, the data stays in our own service. No health, financial or
children's data. No advertising or sponsored content. Writes happen only on explicit tool calls;
nothing is deleted.

**Test & launch:**

> Connect via the directory listing and sign in with the test account below (email and password, no 2FA). The account has 3 printers, 8 materials, 3 projects, 32 prints from the last four months (including failed and partial prints with notes) and maintenance history. Try asking which spool is running lowest, why a helmet print failed, or for a three-month summary. Write tools are safe to use on this account.
>
> Email: `<reviewer email>` / Password: `<reviewer password>`

## ChatGPT plugin directory

**Display name** (≤30): `3D Print Log`

**Short description** (≤30, 24 used): `Your 3D printing logbook`

**Long description** (≤4,000): the Claude description above, with "Claude" replaced by "ChatGPT".

**Positive test cases** (prompt → tool expected to run):

| Prompt | Expected tool |
| --- | --- |
| Which of my spools is running lowest? | `get_material_inventory` |
| Do I have enough white PLA left for a 240 g print? | `find_material` |
| Why did my helmet print fail? | `search_prints`, then `get_print` |
| What's my success rate on each printer? | `get_printer_stats` |
| Log a 2.5-hour pen holder on the Prusa using 45 g of black PLA. | `create_print` |

**Negative test cases** (the server should not act):

| Prompt | Expected behaviour |
| --- | --- |
| Delete all my failed prints. | No tool runs. There are no delete tools; ChatGPT should say so. |
| Show me the prints of another 3D Print Log user. | No tool runs. Every tool is scoped to the signed-in account. |
| Order me a new spool of black PLA. | No tool runs. The server tracks inventory; it does not purchase. |

**Demo video:** a screen recording of the five positive prompts against the reviewer account.
