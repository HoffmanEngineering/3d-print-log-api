"""Runs every KQL query in workbook.json against the real App Insights resources.

A workbook does not validate its queries when it is deployed; a broken one only shows up
as a red box in the portal, panel by panel. This runs each query through the Application
Insights REST API with the workbook parameters substituted the way the portal would, and
reports the ones that fail. Requires an authenticated Azure CLI.

    python validate_queries.py [--resource-group 3d-print-log-production]
                               [--api-name 3d-print-log-api-insights] [--ui-name 3d-print-log-ui-insights]

Exit code is the number of failing panels, so it can gate a deploy.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path


def az(*args: str) -> str:
    result = subprocess.run(["az", *args], capture_output=True, text=True, encoding="utf-8", errors="replace", shell=True)
    if result.returncode != 0:
        raise SystemExit(result.stderr.strip() or f"az {' '.join(args)} failed")
    return result.stdout


def component(resource_group: str, name: str) -> tuple[str, str]:
    raw = az("resource", "show", "--resource-group", resource_group, "--name", name,
             "--resource-type", "Microsoft.Insights/components", "--query", "{id:id,appId:properties.AppId}", "-o", "json")
    info = json.loads(raw)
    return info["id"], info["appId"]


def substitute(query: str, params: dict[str, str]) -> str:
    def repl(match: re.Match) -> str:
        key = match.group(1)
        if key not in params:
            raise KeyError(f"no substitution for {{{key}}}")
        return params[key]
    return re.sub(r"\{([A-Za-z]+(?::[a-z]+)?)\}", repl, query)


def run(app_id: str, query: str, timespan: str) -> str | None:
    # The body goes through a file: KQL is full of pipes, and `az` on Windows is a batch
    # file, so a body on the command line would be parsed by cmd first.
    body_file = Path(tempfile.gettempdir()) / "printlog-workbook-query.json"
    body_file.write_text(json.dumps({"query": query, "timespan": timespan}), encoding="utf-8")
    result = subprocess.run(
        ["az", "rest", "--method", "post", "--resource", "https://api.applicationinsights.io",
         "--url", f"https://api.applicationinsights.io/v1/apps/{app_id}/query", "--body", f"@{body_file}", "-o", "none"],
        capture_output=True, text=True, encoding="utf-8", errors="replace", shell=True)
    if result.returncode == 0:
        return None
    text = result.stderr
    match = re.search(r'"message":"(Query could not be parsed[^"]*)"', text)
    if match:
        return match.group(1)
    match = re.search(r'"innererror":\{"code":"([^"]+)","message":"([^"]*)"', text)
    return f"{match.group(1)}: {match.group(2)}" if match else text.strip()[:300]


def queries(items: list[dict]):
    for item in items:
        if item["type"] == 3:
            yield item["name"], item["content"]
        elif item["type"] == 12:
            yield from queries(item["content"]["items"])


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--resource-group", default="3d-print-log-production")
    parser.add_argument("--api-name", default="3d-print-log-api-insights")
    parser.add_argument("--ui-name", default="3d-print-log-ui-insights")
    args = parser.parse_args()

    api_id, api_app = component(args.resource_group, args.api_name)
    ui_id, ui_app = component(args.resource_group, args.ui_name)
    apps = {"{ApiResource}": api_app, "{UiResource}": ui_app}

    # What the portal substitutes for each token. The two windows mirror the workbook's
    # defaults: 24 h with a 15 min grain for Health, 30 d with a 1 d grain for Usage.
    params = {
        "ApiResource": api_id, "UiResource": ui_id, "ApiRole": "3d-print-log-api-prod",
        "TimeRange:start": "ago(1d)", "TimeRange:end": "now()", "TimeRange:grain": "15m",
        "UsageTimeRange:start": "ago(30d)", "UsageTimeRange:end": "now()", "UsageTimeRange:grain": "1d",
    }

    workbook = json.loads(Path(__file__).with_name("workbook.json").read_text(encoding="utf-8"))
    failures = 0
    for name, content in queries(workbook["items"]):
        resource = content["crossComponentResources"][0]
        timespan = "P30D" if content.get("timeContextFromParameter") == "UsageTimeRange" else "P1D"
        if "timeContextFromParameter" not in content:
            timespan = "P60D"  # set-in-query panels reach back a full previous period
        try:
            query = substitute(content["query"], params)
        except KeyError as error:
            print(f"FAIL {name}: {error}")
            failures += 1
            continue
        error = run(apps[resource], query, timespan)
        if error:
            print(f"FAIL {name}: {error}")
            failures += 1
        else:
            print(f"ok   {name}")
    print(f"\n{failures} failing panel(s)")
    return failures


if __name__ == "__main__":
    sys.exit(main())
