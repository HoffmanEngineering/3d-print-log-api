# Monitoring

The 3D Print Log dashboard is an Azure Workbook with two views — **Health** ("is it working
right now") and **Usage** ("how is it being used") — reading from the API and UI Application
Insights resources. It replaces the hand-built portal dashboard. The design, and the
measurements it rests on, are in
[`docs/superpowers/specs/2026-09-19-monitoring-dashboard-design.md`](../docs/superpowers/specs/2026-09-19-monitoring-dashboard-design.md).

| File | What it is |
|---|---|
| `build_workbook.py` | Generates `workbook.json`. **Edit this, not the JSON.** |
| `workbook.json` | The workbook definition Bicep deploys. Generated; committed so the diff is reviewable. |
| `validate_queries.py` | Runs every panel's KQL against the live resources and reports parse/semantic failures. |
| `main.bicep` | The workbook resource. Substitutes the two App Insights ids into the JSON. |
| `deploy.ps1` | Resolves the resource ids and runs the deployment. Idempotent. |

## Deploying

```powershell
az login
cd monitoring
python build_workbook.py      # after any change to the generator
python validate_queries.py    # 0 failing panels, or don't deploy
./deploy.ps1                  # add -WhatIf to preview
```

`deploy.ps1` prints the portal URL. The workbook's resource name is derived from the resource
group, so every run updates the same workbook in place — it never creates a second copy.
It lives in the API App Insights resource's **Workbooks** gallery. Pin it to a portal
dashboard from the workbook's pin icon; private dashboards are not ARM resources and cannot
be scripted.

To deploy to the dev resource group instead:

```powershell
./deploy.ps1 -ResourceGroup 3d-print-log-dev -ApiInsightsName api-application-insights -UiInsightsName ui-dev-app-insights
```

## Things that are not obvious

- **Queries filter noise themselves**, duplicating what `NoiseTelemetryProcessor` drops in the
  app (CORS preflights, the unread-count poll, `/metrics`) plus `GET /` (bots hitting the
  root) and anything not from the production cloud role. That is deliberate: history from
  before the processor deploy reads the same as history after it.
- **`availabilityResults.success` is the string `"1"`**, not a bool. `success == true` matches
  nothing and reports 0 % uptime on a healthy site.
- **`{TimeRange:start}` already expands to a `datetime(...)` literal.** Wrapping it in another
  `datetime()` is a parse error. Tiles that compare with the previous period use it directly
  and carry no `timeContext` at all — a `timeContext` of `{durationMs: 0}` is read by the
  portal as a real zero-length scope, and every query under it returns nothing.
- **The resource pickers are fed by Resource Graph.** A picker only accepts a value inside the
  workbook's own scope (the API resource), so the UI resource has to arrive through a query
  with `selected = true`.
- **The Health / Usage switch is a dropdown, not a tabs control.** A tabs-style links item
  deployed from this file never switched its parameter under automated testing; the dropdown
  is a plain parameter pill that does. The groups only care that `selectedTab` changes, so if
  tabs work for you in the portal, swap the control there and export the JSON back.
- **DAU / WAU / MAU and the auth-method split read 0 before the telemetry-enrichment API
  deploy.** They need `user_AuthenticatedId` and the `authMethod` property, which
  `TelemetryEnrichmentInitializer` adds.
- **Two metric alerts already exist** on the availability tests (~$0.20/month). Neither reaches
  anyone: one has no action group, the other's action group has no receivers. Adding an email
  receiver to `Application Insights Smart Detection` is a portal click and the cheapest useful
  alerting there is. Nothing here adds alerts — see the spec for the cost reasoning.
- **The "Documentation Analytics" workbook is separate on purpose.** It is a monthly review
  (zero-result searches, unhelpful pages, scroll completion per page), not a dashboard. The
  Usage view shows its headline numbers and links to the gallery.
