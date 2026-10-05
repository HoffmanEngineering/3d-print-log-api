"""Generates workbook.json — the Azure Workbook that is the 3D Print Log dashboard.

Run `python build_workbook.py` from this folder and commit the regenerated JSON alongside
any change here. The JSON is what Bicep deploys; this file exists because hand-editing a
2,000-line workbook definition is how dashboards drift, and because the queries read far
better as Python strings than as escaped JSON.

Layout is two tabs, Health and Usage, driven by three global parameters. The design and
the reasoning behind every panel is in
docs/superpowers/specs/2026-09-19-monitoring-dashboard-design.md.

Formatter and visualisation codes below were taken from Microsoft's published gallery
templates (microsoft/Application-Insights-Workbooks), not from memory: text 1, bar 3,
hidden 5, date 6, heatmap 8, spark line 9, big number 12, thresholds 18.
"""

from __future__ import annotations

import json
import uuid
from pathlib import Path

# Bicep substitutes these two before deploying; nothing else in the file is
# environment-specific.
API_RESOURCE = "__API_RESOURCE_ID__"
UI_RESOURCE = "__UI_RESOURCE_ID__"

# ---------------------------------------------------------------------------------------
# Shared style
# ---------------------------------------------------------------------------------------

# One colour per named series, reused by every chart, so the same thing looks the same
# everywhere. Names are the Workbooks palette names.
SERIES_COLOURS = {
    # auth methods
    "jwt": "blue",
    "apiKey": "turquoise",
    "mcp": "purple",
    "anonymous": "gray",
    "unstamped": "lightBlue",
    # entities
    "Print": "blue",
    "Filament": "orange",
    "Printer": "green",
    "Project": "purple",
    "Maintenance": "brown",
    "Comment": "gray",
    # integrations
    "Moonraker": "turquoise",
    "OctoPrint": "orange",
    "Cura settings saved": "blue",
    "Cura first load": "lightBlue",
    "Print sent from Cura": "orange",
    # outcomes and status classes
    "ok": "green",
    "warn": "orange",
    "error": "red",
    "4xx": "orange",
    "5xx": "red",
    # percentiles
    "p50": "green",
    "p95": "blue",
    "p99": "purple",
    # dependencies
    "SQL": "blue",
    "Blob storage": "orange",
    "Auth0": "purple",
    "Other": "gray",
    # users
    "Active users": "blue",
    "Signups": "green",
    "Sessions": "blue",
    "Page views": "lightBlue",
}


def series_labels(*names: str) -> list[dict]:
    return [{"seriesName": n, "label": n, "color": SERIES_COLOURS[n]} for n in names]


def chart_settings(*series: str, y_label: str | None = None) -> dict:
    settings: dict = {
        "showLegend": True,
        "showMetrics": False,
        "simpleLegendSettings": {"position": "bottom"},
    }
    if series:
        settings["seriesLabelSettings"] = series_labels(*series)
    if y_label:
        settings["ySettings"] = {"label": y_label}
    return settings


# ---------------------------------------------------------------------------------------
# KQL fragments
# ---------------------------------------------------------------------------------------

# Every API query starts from these. The exclusions duplicate what NoiseTelemetryProcessor
# drops in the app, on purpose: history from before that deploy then reads the same as
# history after it. `GET /` is bots and platform probes hitting the root, which 404s.
API_REQUESTS = """let apiRequests = requests
| where cloud_RoleName == '{ApiRole}'
| where not(name startswith 'OPTIONS ')
| where name !in ('GET Notifications/GetUnreadCount', 'GET /metrics', 'GET /');
"""

API_DEPENDENCIES = """let apiDependencies = dependencies
| where cloud_RoleName == '{ApiRole}'
| where operation_Name != 'GET Notifications/GetUnreadCount'
| extend Target = case(
    type == 'SQL', 'SQL',
    type has 'Storage' or type has 'blob', 'Blob storage',
    target has 'auth0', 'Auth0',
    'Other');
"""

API_EVENTS = """let apiEvents = customEvents
| where cloud_RoleName == '{ApiRole}';
"""

# `success` on availabilityResults is the string "1"/"0", not a bool; `success == true`
# matches nothing and reports 0% uptime on a healthy site. Found the hard way.
AVAILABILITY_OK = "success == '1'"

# Tiles compare the selected window with the one before it. The window bounds come from
# the time range parameter rather than the query's time context so the previous period
# is reachable.
def window(param: str) -> str:
    # {X:start} and {X:end} already expand to datetime(...) literals; wrapping them again
    # is a parse error.
    return f"""let windowStart = {{{param}:start}};
let windowEnd = {{{param}:end}};
let previousStart = windowStart - (windowEnd - windowStart);
let grain = {{{param}:grain}};
"""


# ---------------------------------------------------------------------------------------
# Item builders
# ---------------------------------------------------------------------------------------


# Stable, sequential ids keep the diff readable when only a query changes.
_counter = iter(range(1, 10_000))


def _id() -> str:
    return str(uuid.UUID(int=next(_counter)))


def text(markdown: str, name: str) -> dict:
    return {"type": 1, "content": {"json": markdown}, "name": name}


def query(
    name: str,
    title: str,
    kql: str,
    visualization: str,
    *,
    resources: list[str],
    width: int | None = None,
    time_param: str | None = "TimeRange",
    chart: dict | None = None,
    grid: dict | None = None,
    tiles: dict | None = None,
) -> dict:
    content: dict = {
        "version": "KqlItem/1.0",
        "query": kql,
        "size": 0,
        "title": title,
        "queryType": 0,
        "resourceType": "microsoft.insights/components",
        "crossComponentResources": resources,
        "visualization": visualization,
    }
    # No time_param means "set in query": the item carries no timeContext key at all. A
    # timeContext of {durationMs: 0} is not the same thing — the portal reads it as a real,
    # zero-length scope and every query under it returns nothing.
    if time_param:
        content["timeContextFromParameter"] = time_param
    if chart:
        content["chartSettings"] = chart
    if grid:
        content["gridSettings"] = grid
    if tiles:
        content["tileSettings"] = tiles
    item: dict = {"type": 3, "content": content, "name": name}
    if width:
        item["customWidth"] = str(width)
    return item


def tile_settings(*, invert_delta: bool = False, palette: str = "none", min_: float | None = None,
                  max_: float | None = None, sort_field: str | None = None, delta: bool = True) -> dict:
    # A query that returns one tile fills its column ("full"); one returning several lays
    # them out side by side ("auto") — "full" there would stack them one per line.
    size = "auto" if sort_field else "full"
    up, down = ("red", "green") if invert_delta else ("green", "red")
    left: dict = {
        "columnMatch": "Value",
        "formatter": 12,
        "formatOptions": {"palette": palette},
        "numberFormat": {"unit": 0, "options": {"style": "decimal", "maximumFractionDigits": 2}},
    }
    if min_ is not None:
        left["formatOptions"]["min"] = min_
    if max_ is not None:
        left["formatOptions"]["max"] = max_
    settings: dict = {
        "titleContent": {"columnMatch": "Title", "formatter": 1},
        "subtitleContent": {"columnMatch": "Subtitle", "formatter": 1},
        "leftContent": left,
        "rightContent": {"columnMatch": "Trend", "formatter": 9, "formatOptions": {"palette": "blue"}},
        "secondaryContent": {
            "columnMatch": "Delta",
            "formatter": 18,
            "formatOptions": {
                "thresholdsOptions": "colors",
                "thresholdsGrid": [
                    # Tag text is tight; "vs the previous period" is in the workbook heading instead.
                    {"operator": ">", "thresholdValue": "0", "representation": up, "text": "+{0}{1}"},
                    {"operator": "<", "thresholdValue": "0", "representation": down, "text": "{0}{1}"},
                    {"operator": "Default", "thresholdValue": None, "representation": "gray", "text": "\u00b10"},
                ],
                "showAsTag": True,
            },
            "numberFormat": {"unit": 0, "options": {"style": "decimal", "maximumFractionDigits": 2}},
        },
        "showBorder": True,
        "size": size,
    }
    if not delta:
        del settings["secondaryContent"]
    if sort_field:
        settings["sortCriteriaField"] = sort_field
        settings["sortOrderField"] = 1
    return settings


def bar(column: str, palette: str = "blue") -> dict:
    return {"columnMatch": column, "formatter": 3, "formatOptions": {"min": 0, "palette": palette}}


def heat(column: str, palette: str, min_: float, max_: float) -> dict:
    return {"columnMatch": column, "formatter": 8, "formatOptions": {"min": min_, "max": max_, "palette": palette}}


def date(column: str) -> dict:
    return {"columnMatch": column, "formatter": 6, "dateFormat": {"formatName": "shortDateTimePattern"}}


def links(name: str, items: list[dict]) -> dict:
    return {"type": 11, "content": {"version": "LinkItem/1.0", "style": "list", "links": items}, "name": name}


def resource_link(label: str, resource_param: str, blade: str) -> dict:
    return {"id": _id(), "cellValue": f"{{{resource_param}}}", "linkTarget": "Resource",
            "linkLabel": label, "subTarget": blade, "style": "link"}


def group(name: str, items: list[dict], tab: str | None = None) -> dict:
    item: dict = {
        "type": 12,
        "content": {"version": "NotebookGroup/1.0", "groupType": "editable", "items": items},
        "name": name,
    }
    if tab:
        item["conditionalVisibility"] = {"parameterName": "selectedTab", "comparison": "isEqualTo", "value": tab}
    return item


# ---------------------------------------------------------------------------------------
# Global parameters and tabs
# ---------------------------------------------------------------------------------------

DURATIONS = [3600000, 14400000, 43200000, 86400000, 172800000, 604800000, 1209600000, 2592000000, 7776000000]


def time_parameter(name: str, label: str, default_ms: int) -> dict:
    return {
        "id": _id(), "version": "KqlParameterItem/1.0", "name": name, "label": label, "type": 4,
        "isRequired": True, "value": {"durationMs": default_ms},
        "typeSettings": {"selectableValues": [{"durationMs": d} for d in DURATIONS], "allowCustom": True},
    }


def resource_parameter(name: str, label: str, resource_id: str) -> dict:
    # A resource picker only accepts values inside the workbook's own scope, which is the
    # API resource alone. Feeding it from Resource Graph, with the wanted resource marked
    # selected, is how the gallery templates default a picker to something else.
    return {
        "id": _id(), "version": "KqlParameterItem/1.0", "name": name, "label": label, "type": 5,
        "isRequired": True, "isHiddenWhenLocked": True,
        "query": f"resources | where type =~ 'microsoft.insights/components' and id =~ '{resource_id}' "
                 "| project value = id, label = name, selected = true",
        "crossComponentResources": ["value::all"],
        "queryType": 1,
        "resourceType": "microsoft.resourcegraph/resources",
        "typeSettings": {"resourceTypeFilter": {"microsoft.insights/components": True}, "additionalResourceOptions": []},
    }


GLOBAL_PARAMETERS = {
    "type": 9,
    "content": {
        "version": "KqlParameterItem/1.0",
        "parameters": [
            time_parameter("TimeRange", "Time range", 86400000),
            resource_parameter("ApiResource", "API App Insights", API_RESOURCE),
            resource_parameter("UiResource", "UI App Insights", UI_RESOURCE),
            {
                # The app's cloud role name. Rows from anything else — CI runners, a local
                # run with the wrong key — are excluded from every API query.
                "id": _id(), "version": "KqlParameterItem/1.0", "name": "ApiRole", "label": "API role name",
                "type": 1, "isRequired": True, "value": "3d-print-log-api-prod", "isHiddenWhenLocked": True,
            },
        ],
        "style": "pills",
    },
    "name": "global-parameters",
}

# A dropdown rather than a tabs-style links item. Tabs are the prettier control, but a
# tabs item deployed from this file never switched its parameter under test, and a
# dropdown is a plain parameter pill that verifiably does. Swap it for tabs in the portal
# if they behave for you; the groups only care that `selectedTab` changes.
TABS = {
    "type": 9,
    "content": {
        "version": "KqlParameterItem/1.0",
        "parameters": [
            {
                "id": _id(), "version": "KqlParameterItem/1.0", "name": "selectedTab", "label": "View",
                "type": 2, "isRequired": True, "value": "health",
                "typeSettings": {"additionalResourceOptions": [], "showDefault": False},
                "jsonData": json.dumps([
                    {"value": "health", "label": "Health", "selected": True},
                    {"value": "usage", "label": "Usage"},
                ]),
            },
        ],
        "style": "pills",
    },
    "name": "view",
}

# ---------------------------------------------------------------------------------------
# Health tab
# ---------------------------------------------------------------------------------------


def uptime_tile(name: str, title: str, resource: str) -> dict:
    kql = window("TimeRange") + f"""availabilityResults
| where timestamp between (previousStart .. windowEnd)
| extend current = timestamp >= windowStart
| summarize ok = countif({AVAILABILITY_OK}), n = count() by current, bin(timestamp, grain)
| order by timestamp asc
| summarize Trend = make_list_if(round(100.0 * ok / n, 1), current),
            Value = round(100.0 * sumif(ok, current) / sumif(n, current), 2),
            Previous = round(100.0 * sumif(ok, not(current)) / sumif(n, not(current)), 2)
| extend Delta = round(Value - Previous, 2), Title = '{title}', Subtitle = 'availability tests, %'
"""
    return query(name, "", kql, "tiles", resources=[resource], width=16, time_param=None,
                 tiles=tile_settings(palette="redGreen", min_=98, max_=100))


def requests_per_minute_tile() -> dict:
    kql = API_REQUESTS + window("TimeRange") + """apiRequests
| where timestamp between (previousStart .. windowEnd)
| extend current = timestamp >= windowStart
| summarize n = count() by current, bin(timestamp, grain)
| order by timestamp asc
| summarize Trend = make_list_if(n, current),
            Value = round(sumif(n, current) / ((windowEnd - windowStart) / 1m), 2),
            Previous = round(sumif(n, not(current)) / ((windowEnd - windowStart) / 1m), 2)
| extend Delta = round(Value - Previous, 2), Title = 'Requests / min', Subtitle = 'excluding preflight and polling'
"""
    return query("tile-rpm", "", kql, "tiles", resources=["{ApiResource}"], width=16, time_param=None,
                 tiles=tile_settings(palette="blue"))


def error_rate_tile() -> dict:
    kql = API_REQUESTS + window("TimeRange") + """apiRequests
| where timestamp between (previousStart .. windowEnd)
| extend current = timestamp >= windowStart
| summarize errors = countif(toint(resultCode) >= 500), n = count() by current, bin(timestamp, grain)
| order by timestamp asc
| summarize Trend = make_list_if(round(100.0 * errors / n, 2), current),
            Value = round(100.0 * sumif(errors, current) / sumif(n, current), 2),
            Previous = round(100.0 * sumif(errors, not(current)) / sumif(n, not(current)), 2)
| extend Delta = round(Value - Previous, 2), Title = '5xx rate', Subtitle = '% of API requests'
"""
    return query("tile-5xx", "", kql, "tiles", resources=["{ApiResource}"], width=16, time_param=None,
                 tiles=tile_settings(invert_delta=True, palette="greenRed", min_=0, max_=2))


def p95_tile() -> dict:
    kql = API_REQUESTS + window("TimeRange") + """let inWindow = apiRequests
| where name !has 'GetImage'
| where timestamp between (previousStart .. windowEnd)
| extend current = timestamp >= windowStart;
let trend = inWindow
| where current
| summarize p = percentile(duration, 95) by bin(timestamp, grain)
| order by timestamp asc
| summarize Trend = make_list(round(p));
let totals = inWindow
| summarize p = percentile(duration, 95) by current
| summarize Value = round(take_anyif(p, current)), Previous = round(take_anyif(p, not(current)));
trend
| extend k = 1
| join kind=inner (totals | extend k = 1) on k
| project Title = 'API p95', Subtitle = 'ms, excluding image serving', Value = todouble(Value), Trend, Delta = Value - Previous
"""
    return query("tile-p95", "", kql, "tiles", resources=["{ApiResource}"], width=16, time_param=None,
                 tiles=tile_settings(invert_delta=True, palette="greenRed", min_=500, max_=3000))


def failed_dependencies_tile() -> dict:
    kql = API_DEPENDENCIES + window("TimeRange") + """apiDependencies
| where timestamp between (previousStart .. windowEnd)
| where success == false
| extend current = timestamp >= windowStart
| summarize n = count() by current, bin(timestamp, grain)
| order by timestamp asc
| summarize Trend = make_list_if(n, current), Value = sumif(n, current), Previous = sumif(n, not(current))
| extend Delta = Value - Previous, Title = 'Failed dependencies', Subtitle = 'SQL, blob, Auth0'
"""
    return query("tile-deps", "", kql, "tiles", resources=["{ApiResource}"], width=16, time_param=None,
                 tiles=tile_settings(invert_delta=True, palette="greenRed", min_=0, max_=20))


def health_tab() -> dict:
    items = [
        uptime_tile("tile-api-uptime", "API uptime", "{ApiResource}"),
        uptime_tile("tile-ui-uptime", "UI uptime", "{UiResource}"),
        requests_per_minute_tile(),
        error_rate_tile(),
        p95_tile(),
        failed_dependencies_tile(),
        query(
            "requests-by-auth", "Requests per minute by auth method",
            API_REQUESTS + """apiRequests
| extend authMethod = tostring(customDimensions.authMethod)
| extend authMethod = iff(isempty(authMethod), 'unstamped', authMethod)
| summarize n = count() by bin(timestamp, {TimeRange:grain}), authMethod
| extend ['req/min'] = round(n / ({TimeRange:grain} / 1m), 2)
| project timestamp, authMethod, ['req/min']
""",
            "areachart", resources=["{ApiResource}"], width=50,
            chart=chart_settings("jwt", "apiKey", "mcp", "anonymous", "unstamped", y_label="requests / min"),
        ),
        query(
            "errors-by-class", "Errors by status class",
            API_REQUESTS + """apiRequests
| where toint(resultCode) >= 400
| extend class = iff(toint(resultCode) >= 500, '5xx', '4xx')
| summarize Errors = count() by bin(timestamp, {TimeRange:grain}), class
""",
            "barchart", resources=["{ApiResource}"], width=50,
            chart=chart_settings("4xx", "5xx", y_label="errors"),
        ),
        query(
            "latency-percentiles", "API latency p50 / p95 / p99 (ms), excluding image serving",
            API_REQUESTS + """// GetImage streams blobs and runs at seconds, not milliseconds; on the same
// axis it flattens every other route into the baseline. It gets its own row in the
// routes grid instead.
apiRequests
| where name !has 'GetImage'
| summarize p50 = percentile(duration, 50), p95 = percentile(duration, 95), p99 = percentile(duration, 99)
    by bin(timestamp, {TimeRange:grain})
""",
            "timechart", resources=["{ApiResource}"], width=50,
            chart=chart_settings("p50", "p95", "p99", y_label="ms"),
        ),
        query(
            "slowest-routes", "Slowest routes (≥ 10 requests)",
            API_REQUESTS + """apiRequests
| summarize Requests = count(), ['p95 ms'] = round(percentile(duration, 95)),
            ['Fail %'] = round(100.0 * countif(success == false) / count(), 1)
    by Route = name
| where Requests >= 10
| top 15 by ['p95 ms'] desc
""",
            "table", resources=["{ApiResource}"], width=50,
            grid={"formatters": [bar("Requests", "blue"), bar("p95 ms", "orange"), heat("Fail %", "greenRed", 0, 10)]},
        ),
        query(
            "dependency-p95", "Dependency p95 (ms) by target",
            API_DEPENDENCIES + """apiDependencies
| summarize ['p95 ms'] = round(percentile(duration, 95)) by bin(timestamp, {TimeRange:grain}), Target
""",
            "timechart", resources=["{ApiResource}"], width=50,
            chart=chart_settings("SQL", "Blob storage", "Auth0", "Other", y_label="ms"),
        ),
        query(
            "dependency-failures", "Dependency failures",
            API_DEPENDENCIES + """apiDependencies
| where success == false
| summarize Failures = count(), ['Last seen'] = max(timestamp), ['p95 ms'] = round(percentile(duration, 95))
    by Target, Host = target, Result = resultCode
| top 15 by Failures desc
""",
            "table", resources=["{ApiResource}"], width=50,
            grid={"formatters": [bar("Failures", "red"), date("Last seen"), bar("p95 ms", "orange")]},
        ),
        query(
            "top-exceptions", "Top exceptions, both apps (known noise filtered)",
            """// BadImageFormatException is the runtime failing to format a stack trace, not an app
// fault; the ad-slot and refresh-token errors are the browser's ad blocker and Auth0's
// normal session expiry. All three are volume without information.
union (exceptions | extend App = 'API'), (app('{UiResource}').exceptions | extend App = 'UI')
| where type !has 'BadImageFormatException'
| where problemId !has 'adsbygoogle' and outerMessage !has 'refresh token'
| summarize Count = count(), ['Last seen'] = max(timestamp) by App, Problem = problemId
| top 20 by Count desc
""",
            "table", resources=["{ApiResource}"], width=50,
            grid={"formatters": [bar("Count", "red"), date("Last seen")]},
        ),
        query(
            "availability-heatmap", "Availability by region (%)",
            f"""union (availabilityResults | extend App = 'API'), (app('{{UiResource}}').availabilityResults | extend App = 'UI')
| summarize pct = round(100.0 * countif({AVAILABILITY_OK}) / count(), 2) by Test = strcat(App, ' · ', name), location
| evaluate pivot(location, max(pct), Test)
""",
            "table", resources=["{ApiResource}"], width=50,
            grid={"formatters": [{"columnMatch": "Test", "formatter": 0}, heat("^(?!Test$).*", "redGreen", 95, 100)]},
        ),
        query(
            "page-load", "Browser page load p50 / p95 (ms)",
            """browserTimings
| summarize p50 = percentile(totalDuration, 50), p95 = percentile(totalDuration, 95) by bin(timestamp, {TimeRange:grain})
""",
            "timechart", resources=["{UiResource}"], width=50,
            chart=chart_settings("p50", "p95", y_label="ms"),
        ),
        query(
            "browser-api-calls", "API calls from the browser",
            """// Every other dependency the browser reports is an ad or analytics call blocked
// client-side — 100% failure by design, and not ours.
dependencies
| where target has 'api.3dprintlog.com' and name !has 'unread-count'
| summarize Calls = count(), ['Fail %'] = round(100.0 * countif(success == false) / count(), 1),
            ['p95 ms'] = round(percentile(duration, 95))
    by Call = name
| top 15 by Calls desc
""",
            "table", resources=["{UiResource}"], width=50,
            grid={"formatters": [bar("Calls", "blue"), heat("Fail %", "greenRed", 0, 10), bar("p95 ms", "orange")]},
        ),
        links("health-links", [
            resource_link("Live Metrics", "ApiResource", "quickPulse"),
            resource_link("Failures", "ApiResource", "failures"),
            resource_link("Performance", "ApiResource", "performance"),
            resource_link("Application Map", "ApiResource", "applicationMap"),
            resource_link("UI failures", "UiResource", "failures"),
        ]),
    ]
    return group("health", items, tab="health")


# ---------------------------------------------------------------------------------------
# Usage tab
# ---------------------------------------------------------------------------------------

USAGE_PARAMETERS = {
    "type": 9,
    "content": {
        "version": "KqlParameterItem/1.0",
        "parameters": [time_parameter("UsageTimeRange", "Usage time range", 2592000000)],
        "style": "pills",
    },
    "name": "usage-parameters",
}

PHOTO_EVENTS = "('PrintPictureAdded', 'FilamentPictureAdded', 'ProjectPictureAdded', 'UserProfilePictureUploaded', 'UserCoverPictureUploaded')"


def usage_tab() -> dict:
    items = [
        USAGE_PARAMETERS,
        query(
            "usage-tiles", "",
            API_EVENTS + window("UsageTimeRange") + f"""let titles = datatable(Title: string, Order: int) [
    'Signups', 1, 'Prints added', 2, 'Filaments added', 3, 'Photos uploaded', 4];
let counts = apiEvents
| where timestamp between (previousStart .. windowEnd)
| extend Title = case(
    name == 'UserSignedUp', 'Signups',
    name == 'PrintAdded', 'Prints added',
    name == 'FilamentAdd', 'Filaments added',
    name in {PHOTO_EVENTS}, 'Photos uploaded',
    '')
| where isnotempty(Title)
| extend current = timestamp >= windowStart
| summarize n = count() by Title, current, bin(timestamp, 1d)
| order by timestamp asc
| summarize Trend = make_list_if(n, current), Value = sumif(n, current), Previous = sumif(n, not(current)) by Title;
// A title with no events in either period still gets a tile, reading 0.
titles
| join kind=leftouter counts on Title
| project Title, Order, Trend, Value = todouble(coalesce(Value, 0)), Delta = todouble(coalesce(Value, 0) - coalesce(Previous, 0))
""",
            "tiles", resources=["{ApiResource}"], width=50, time_param=None,
            tiles=tile_settings(palette="blue", sort_field="Order"),
        ),
        query(
            "active-user-tiles", "",
            API_EVENTS + window("UsageTimeRange") + """// Counts users who did something, not users who merely polled: every write path
// emits an event, and the initializer stamps the user on it. Reads 0 for any period
// before the enrichment deploy.
let active = apiEvents | where isnotempty(user_AuthenticatedId);
let daily = active
| where timestamp between (windowStart .. windowEnd)
| summarize d = dcount(user_AuthenticatedId) by bin(timestamp, 1d)
| order by timestamp asc
| summarize Value = round(avg(d), 1), Trend = make_list(d)
// avg() over no rows is NaN, which the tile would print verbatim.
| extend Value = iff(isnan(Value), 0.0, Value)
| extend Title = 'DAU', Subtitle = 'average per day', Order = 1;
let weekly = active
| where timestamp > windowEnd - 7d
| summarize Value = todouble(dcount(user_AuthenticatedId))
| extend Title = 'WAU', Subtitle = 'last 7 days', Order = 2;
let monthly = active
| where timestamp > windowEnd - 30d
| summarize Value = todouble(dcount(user_AuthenticatedId))
| extend Title = 'MAU', Subtitle = 'last 30 days', Order = 3;
union daily, weekly, monthly
""",
            "tiles", resources=["{ApiResource}"], width=50, time_param=None,
            tiles=tile_settings(palette="blue", sort_field="Order", delta=False),
        ),
        query(
            "active-users-per-day", "Active users and signups per day",
            API_EVENTS + """apiEvents
| summarize ['Active users'] = dcountif(user_AuthenticatedId, isnotempty(user_AuthenticatedId)),
            Signups = countif(name == 'UserSignedUp')
    by bin(timestamp, 1d)
""",
            "timechart", resources=["{ApiResource}"], width=50, time_param="UsageTimeRange",
            chart=chart_settings("Active users", "Signups", y_label="users"),
        ),
        query(
            "activity-by-auth", "Writes per day by auth method",
            API_EVENTS + """apiEvents
| extend authMethod = tostring(customDimensions.authMethod)
| extend authMethod = iff(isempty(authMethod), 'unstamped', authMethod)
| summarize Events = count() by bin(timestamp, 1d), authMethod
""",
            "barchart", resources=["{ApiResource}"], width=50, time_param="UsageTimeRange",
            chart=chart_settings("jwt", "apiKey", "mcp", "unstamped", y_label="events"),
        ),
        query(
            "content-created", "Content created per day",
            API_EVENTS + """apiEvents
| extend Entity = case(
    name == 'PrintAdded', 'Print',
    name == 'FilamentAdd', 'Filament',
    name in ('PrinterAdded', 'McpPrinterAdded'), 'Printer',
    name == 'ProjectAdded', 'Project',
    name == 'PrinterMaintenanceAdd', 'Maintenance',
    name == 'CommentAdded', 'Comment',
    '')
| where isnotempty(Entity)
| summarize Created = count() by bin(timestamp, 1d), Entity
""",
            "barchart", resources=["{ApiResource}"], width=50, time_param="UsageTimeRange",
            chart=chart_settings("Print", "Filament", "Printer", "Project", "Maintenance", "Comment", y_label="created"),
        ),
        query(
            "content-changed", "Edits and deletes per day",
            API_EVENTS + """apiEvents
| where name in ('PrintEdit', 'PrintStatusEdit', 'PrintsBulkUpdated', 'FilamentEdit', 'PrinterEdit', 'McpPrinterEdit',
                 'ProjectEdit', 'PrinterMaintenanceEdit', 'CommentEdit',
                 'PrintDeleted', 'PrintsBulkDeleted', 'FilamentDelete', 'PrinterDelete', 'ProjectDelete',
                 'PrinterMaintenanceDelete', 'CommentDelete')
| summarize Changes = count() by bin(timestamp, 1d), name
""",
            "barchart", resources=["{ApiResource}"], width=50, time_param="UsageTimeRange",
            chart=chart_settings(y_label="changes"),
        ),
        query(
            "cura", "Cura plugin",
            API_EVENTS + """union
    (apiEvents | where name == 'CuraSettingsSaved' | extend Series = 'Cura settings saved'),
    (apiEvents | where name == 'CuraSettingsFirstLoad' | extend Series = 'Cura first load'),
    (app('{UiResource}').customEvents | where name == 'PrintSentFromCura' | extend Series = 'Print sent from Cura')
| summarize Events = count() by bin(timestamp, 1d), Series
""",
            "timechart", resources=["{ApiResource}"], width=50, time_param="UsageTimeRange",
            chart=chart_settings("Cura settings saved", "Cura first load", "Print sent from Cura", y_label="events"),
        ),
        query(
            "webhooks", "Printer webhooks by outcome",
            API_EVENTS + """apiEvents
| where name startswith 'Moonraker_Webhook_' or name startswith 'OctoPrint_Webhook_'
| extend Outcome = case(
    name endswith '_Error' or name endswith '_Unhandled', 'error',
    name endswith '_Cancelled' or name endswith '_Failed', 'warn',
    'ok')
| summarize Webhooks = count() by bin(timestamp, 1d), Outcome
""",
            "barchart", resources=["{ApiResource}"], width=50, time_param="UsageTimeRange",
            chart=chart_settings("ok", "warn", "error", y_label="webhooks"),
        ),
        query(
            "webhooks-by-source", "Webhooks by source",
            API_EVENTS + """apiEvents
| where name startswith 'Moonraker_Webhook_' or name startswith 'OctoPrint_Webhook_'
| extend Source = iff(name startswith 'Moonraker', 'Moonraker', 'OctoPrint'), Event = substring(name, indexof(name, '_Webhook_') + 9)
| summarize Count = count() by Source, Event
| evaluate pivot(Event, sum(Count), Source)
""",
            "table", resources=["{ApiResource}"], width=50, time_param="UsageTimeRange",
            grid={"formatters": [{"columnMatch": "Source", "formatter": 0}, bar("^(?!Source$).*", "blue")]},
        ),
        query(
            "mcp-tools", "MCP tool calls by tool and outcome",
            API_EVENTS + """apiEvents
| where name == 'Mcp_ToolCalled'
| summarize Calls = count() by Tool = tostring(customDimensions.tool), Outcome = tostring(customDimensions.outcome)
| order by Calls desc
""",
            "categoricalbar", resources=["{ApiResource}"], width=50, time_param="UsageTimeRange",
            chart={"xAxis": "Tool", "yAxis": ["Calls"], "group": "Outcome", "showLegend": True, "showMetrics": False,
                   "simpleLegendSettings": {"position": "bottom"}},
        ),
        query(
            "integration-tiles", "",
            API_EVENTS + """let titles = datatable(Title: string, Order: int) [
    'MCP tool calls', 1, 'Agents revoked', 2, 'API keys generated', 3, 'API keys deleted', 4,
    'Files uploaded', 5, 'Uploaded MB', 6];
let counts = apiEvents
| extend Title = case(
    name == 'Mcp_ToolCalled', 'MCP tool calls',
    name == 'ConnectedAgentRevoked', 'Agents revoked',
    name == 'NewApiKeyGenerated', 'API keys generated',
    name == 'ApiKeyDeleted', 'API keys deleted',
    name == 'PrintFileUploaded', 'Files uploaded',
    '')
| where isnotempty(Title)
| summarize Value = todouble(count()) by Title
| union (apiEvents
    | where name == 'PrintFileUploaded'
    | summarize Value = round(sum(tolong(customDimensions.sizeBytes)) / 1048576.0, 1)
    | extend Title = 'Uploaded MB');
titles
| join kind=leftouter counts on Title
| project Title, Order, Value = coalesce(Value, 0.0)
""",
            "tiles", resources=["{ApiResource}"], width=50, time_param="UsageTimeRange",
            tiles=tile_settings(palette="purple", sort_field="Order", delta=False),
        ),
        query(
            "uploads-by-extension", "Print file uploads by extension",
            API_EVENTS + """apiEvents
| where name == 'PrintFileUploaded'
| summarize Uploads = count() by bin(timestamp, 1d), Extension = tostring(customDimensions.extension)
""",
            "barchart", resources=["{ApiResource}"], width=50, time_param="UsageTimeRange",
            chart=chart_settings(y_label="uploads"),
        ),
        query(
            "sessions", "Sessions and page views per day",
            """pageViews
| summarize Sessions = dcount(session_Id), ['Page views'] = count() by bin(timestamp, 1d)
""",
            "timechart", resources=["{UiResource}"], width=50, time_param="UsageTimeRange",
            chart=chart_settings("Sessions", "Page views"),
        ),
        query(
            "top-pages", "Top pages",
            """pageViews
| summarize Views = count(), Sessions = dcount(session_Id) by Page = name
| join kind=leftouter (browserTimings | summarize ['p95 load ms'] = round(percentile(totalDuration, 95)) by Page = name) on Page
| project Page, Views, Sessions, ['p95 load ms']
| top 15 by Views desc
""",
            "table", resources=["{UiResource}"], width=50, time_param="UsageTimeRange",
            grid={"formatters": [bar("Views", "blue"), bar("Sessions", "lightBlue"), bar("p95 load ms", "orange")]},
        ),
        query(
            "funnels", "Feature funnels",
            """let step = (started: string, completed: string, label: string) {
    customEvents
    | where name in (started, completed)
    | summarize Started = countif(name == started), Completed = countif(name == completed)
    | extend Feature = label
};
union
    step('PrintBulkActionBar_SetProjectStarted', 'PrintBulkActionBar_SetProjectCompleted', 'Bulk: set project'),
    step('PrintBulkActionBar_SetStatusStarted', 'PrintBulkActionBar_SetStatusCompleted', 'Bulk: set status'),
    step('PrintBulkActionBar_DeleteConfirmed', 'PrintBulkActionBar_DeleteCompleted', 'Bulk: delete'),
    step('QrScanner_Opened', 'QrScanner_Success', 'QR scanner'),
    step('ProjectDetail_EditStarted', 'ProjectDetail_Saved', 'Project edit')
| extend ['Completion %'] = iff(Started > 0, round(100.0 * Completed / Started, 1), 0.0)
| project Feature, Started, Completed, ['Completion %']
""",
            "table", resources=["{UiResource}"], width=50, time_param="UsageTimeRange",
            grid={"formatters": [bar("Started", "lightBlue"), bar("Completed", "blue"), heat("Completion %", "redGreen", 0, 100)]},
        ),
        query(
            "cta-clicks", "Calls to action",
            """customEvents
| where name in ('PrintEmptyState_AddPrinter', 'PrintEmptyState_AddPrint', 'PrintEmptyState_ImportGcode',
                 'Home_SignupClicked', 'Pricing_PageViewed', 'PrintGroupedView_ShareClicked')
| summarize Clicks = count() by Action = name
| order by Clicks desc
""",
            "categoricalbar", resources=["{UiResource}"], width=50, time_param="UsageTimeRange",
            chart={"xAxis": "Action", "yAxis": ["Clicks"], "showLegend": False, "showMetrics": False},
        ),
        query(
            "docs", "Documentation",
            """// Headline numbers only. The monthly review — zero-result searches, unhelpful pages,
// scroll completion per page — is the 'Documentation Analytics' workbook, linked below.
let docViews = customEvents | where name == 'Docs_PageView' | summarize Value = todouble(count()) | extend Title = 'Docs page views', Order = 1;
let docReaders = customEvents | where name == 'Docs_PageView' | summarize Value = todouble(dcount(user_Id)) | extend Title = 'Docs readers', Order = 2;
let docSearches = customEvents | where name == 'Docs_Search' | summarize Value = todouble(count()) | extend Title = 'Docs searches', Order = 3;
let docCompletion = customEvents
| where name in ('Docs_PageView', 'Docs_ScrollDepth')
| summarize opened = countif(name == 'Docs_PageView'), finished = countif(name == 'Docs_ScrollDepth' and toint(customMeasurements.bucket) == 100)
| project Title = 'Read to the end %', Value = iff(opened > 0, round(100.0 * finished / opened, 1), 0.0), Order = 4;
union docViews, docReaders, docSearches, docCompletion
""",
            "tiles", resources=["{UiResource}"], width=50, time_param="UsageTimeRange",
            tiles=tile_settings(palette="green", sort_field="Order", delta=False),
        ),
        links("docs-links", [resource_link("Documentation Analytics workbook (Workbooks gallery)", "UiResource", "workbooks")]),
        query(
            "money-tiles", "",
            API_EVENTS + """let titles = datatable(Title: string, Order: int) [
    'Checkouts started', 1, 'Subscriptions activated', 2, 'Subscriptions canceled', 3,
    'Payments failed', 4, 'Feedback received', 5];
let counts = apiEvents
| extend Title = case(
    name == 'Subscription_CheckoutSessionCreated', 'Checkouts started',
    name == 'Subscription_Activated', 'Subscriptions activated',
    name in ('Subscription_Canceled', 'Subscription_CanceledImmediately'), 'Subscriptions canceled',
    name == 'Subscription_PaymentFailed', 'Payments failed',
    name == 'FeedbackAdded', 'Feedback received',
    '')
| where isnotempty(Title)
| summarize Value = todouble(count()) by Title;
titles
| join kind=leftouter counts on Title
| project Title, Order, Value = coalesce(Value, 0.0)
""",
            "tiles", resources=["{ApiResource}"], width=100, time_param="UsageTimeRange",
            tiles=tile_settings(palette="green", sort_field="Order", delta=False),
        ),
    ]
    return group("usage", items, tab="usage")


# ---------------------------------------------------------------------------------------
# Assembly
# ---------------------------------------------------------------------------------------


def build() -> dict:
    return {
        "version": "Notebook/1.0",
        "items": [
            text("## 3D Print Log\nHealth is *is it working right now*; Usage is *how is it being used*. "
                 "Tile tags compare with the previous period of the same length. "
                 "Nothing here auto-refreshes — reload for fresh numbers, or open Live Metrics from the links row.",
                 "heading"),
            GLOBAL_PARAMETERS,
            TABS,
            health_tab(),
            usage_tab(),
        ],
        "fallbackResourceIds": [API_RESOURCE],
        "$schema": "https://github.com/Microsoft/Application-Insights-Workbooks/blob/master/schema/workbook.json",
    }


def main() -> None:
    out = Path(__file__).with_name("workbook.json")
    out.write_text(json.dumps(build(), indent=2, ensure_ascii=True) + "\n", encoding="utf-8")
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
