"""Populate a directory reviewer account with realistic 3D printing history.

Directory reviewers (Claude Connectors Directory, ChatGPT plugin directory) need an account whose
MCP tools return real data. This builds one through the public REST API, using an API key created
on that account. See docs/mcp-registry-listing.md, step 7.

    PRINTLOG_API_KEY=<key> python -I scripts/seed-reviewer-account.py
    PRINTLOG_API_KEY=<key> python -I scripts/seed-reviewer-account.py --dry-run

The key is read from the environment, never an argument, so it stays out of shell history.
Revoke it afterwards.

The data is fixed apart from dates, which are relative to today, so every reviewer account looks
the same. Everything is created Private. The script refuses to run against an account that
already has printers, because a second run would duplicate everything rather than top it up.

Standard library only, so there is nothing to install.
"""

import argparse
import json
import os
import random
import sys
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone

# Enum values as the API serializes them (integers, no string converter).
WEIGHT, VOLUME = 1, 3
SUCCESS, CANCELLED, FAILED, PARTIAL = 3, 4, 5, 6
PRIVATE_PRINT = 3
IN_PROGRESS, COMPLETE = 1, 2
PRIVATE_PROJECT = 3

PRINTERS = [
    dict(key="mk4", Make="Prusa Research", Model="MK4S", Name="Prusa MK4S",
         Description="Workhorse for functional parts. 0.4 mm nozzle.",
         Category="FDM", NozzleDiameter=0.4, FilamentDiameter=1.75, BedWidthMm=250,
         BedDepthMm=210, BedHeightMm=220, HasHeatedBed=True, HasHeatedChamber=False,
         WattageW=120),
    dict(key="x1c", Make="Bambu Lab", Model="X1 Carbon", Name="Bambu X1C",
         Description="Enclosed, used for multi-colour and engineering filaments.",
         Category="FDM", NozzleDiameter=0.4, FilamentDiameter=1.75, BedWidthMm=256,
         BedDepthMm=256, BedHeightMm=256, HasHeatedBed=True, HasHeatedChamber=True,
         WattageW=150),
    dict(key="saturn", Make="Elegoo", Model="Saturn 4 Ultra", Name="Saturn 4 Ultra",
         Description="Resin printer for miniatures and detailed parts.",
         Category="MSLA", BedWidthMm=218, BedDepthMm=123, BedHeightMm=220,
         ScreenResolutionXPixels=11520, ScreenResolutionYPixels=5120, WattageW=60),
]

KG = 1_000_000  # milligrams

MATERIALS = [
    dict(key="pla_black", DisplayName="Prusament PLA Galaxy Black", Brand="Prusament",
         MaterialType="PLA", MaterialCategoryNickname="filament", ColorName="Galaxy Black",
         ColorHex="1C1C24", DiameterMm=1.75, MaterialDensityGramPerCubicCm=1.24,
         Source=WEIGHT, InitialNominalWeightMg=KG, SpoolWeightMg=201_000,
         RecommendedTemp=215, RecommendedBedTemp=60, PurchasePriceValue="29.99",
         StorageLocation="Dry box A", PurchaseLocation="prusa3d.com", IsFavorite=True,
         days_ago=150),
    dict(key="pla_white", DisplayName="Polymaker PolyTerra PLA Cotton White", Brand="Polymaker",
         MaterialType="PLA", MaterialCategoryNickname="filament", ColorName="Cotton White",
         ColorHex="F2F0EB", DiameterMm=1.75, MaterialDensityGramPerCubicCm=1.31,
         Source=WEIGHT, InitialNominalWeightMg=KG, SpoolWeightMg=140_000,
         RecommendedTemp=210, RecommendedBedTemp=55, PurchasePriceValue="19.99",
         StorageLocation="Dry box A", PurchaseLocation="Amazon", days_ago=120),
    dict(key="petg_blue", DisplayName="Overture PETG Blue", Brand="Overture",
         MaterialType="PETG", MaterialCategoryNickname="filament", ColorName="Blue",
         ColorHex="1F5FBF", DiameterMm=1.75, MaterialDensityGramPerCubicCm=1.27,
         Source=WEIGHT, InitialNominalWeightMg=KG, SpoolWeightMg=180_000,
         RecommendedTemp=240, RecommendedBedTemp=80, PurchasePriceValue="21.99",
         StorageLocation="Dry box B", PurchaseLocation="Amazon", days_ago=110),
    dict(key="asa_grey", DisplayName="Bambu ASA Gray", Brand="Bambu Lab",
         MaterialType="ASA", MaterialCategoryNickname="filament", ColorName="Gray",
         ColorHex="8A8D8F", DiameterMm=1.75, MaterialDensityGramPerCubicCm=1.05,
         Source=WEIGHT, InitialNominalWeightMg=KG, SpoolWeightMg=250_000,
         RecommendedTemp=260, RecommendedBedTemp=100, PurchasePriceValue="31.99",
         StorageLocation="Dry box B", PurchaseLocation="bambulab.com", days_ago=90),
    dict(key="tpu_red", DisplayName="Sunlu TPU 95A Red", Brand="Sunlu",
         MaterialType="TPU", MaterialCategoryNickname="filament", ColorName="Red",
         ColorHex="C0262D", DiameterMm=1.75, MaterialDensityGramPerCubicCm=1.21,
         Source=WEIGHT, InitialNominalWeightMg=KG, SpoolWeightMg=160_000,
         RecommendedTemp=225, RecommendedBedTemp=50, PurchasePriceValue="24.99",
         StorageLocation="Shelf", PurchaseLocation="Amazon", days_ago=80),
    dict(key="pla_silk", DisplayName="Eryone Silk PLA Gold", Brand="Eryone",
         MaterialType="PLA", MaterialCategoryNickname="filament", ColorName="Gold",
         ColorHex="D4A537", DiameterMm=1.75, MaterialDensityGramPerCubicCm=1.24,
         Source=WEIGHT, InitialNominalWeightMg=KG, SpoolWeightMg=170_000,
         RecommendedTemp=215, RecommendedBedTemp=60, PurchasePriceValue="22.99",
         StorageLocation="Shelf", PurchaseLocation="Amazon", days_ago=60),
    dict(key="resin_grey", DisplayName="Elegoo ABS-Like Resin 3.0 Grey", Brand="Elegoo",
         MaterialType="Standard Resin", MaterialCategoryNickname="resin", ColorName="Grey",
         ColorHex="7D7F80", MaterialDensityGramPerCubicCm=1.1, Source=VOLUME,
         InitialNominalVolumeMl=1000, PurchasePriceValue="32.99", StorageLocation="Resin cabinet",
         PurchaseLocation="elegoo.com", InitialLayerTimeS=25, LayerTimeS=2.5, days_ago=100),
    dict(key="resin_clear", DisplayName="Siraya Tech Fast Clear", Brand="Siraya Tech",
         MaterialType="Standard Resin", MaterialCategoryNickname="resin", ColorName="Clear",
         ColorHex="E8EEF0", MaterialDensityGramPerCubicCm=1.1, Source=VOLUME,
         InitialNominalVolumeMl=1000, PurchasePriceValue="34.99", StorageLocation="Resin cabinet",
         PurchaseLocation="Amazon", InitialLayerTimeS=30, LayerTimeS=3, days_ago=70),
]

PROJECTS = [
    dict(key="organizer", Name="Workshop organizer", Reference="WS-01", Status=COMPLETE,
         Description="Gridfinity bins and drawer inserts for the workbench."),
    dict(key="helmet", Name="Cosplay helmet", Reference="CP-02", Status=IN_PROGRESS,
         Description="Wearable helmet printed in sections, sanded and painted."),
    dict(key="minis", Name="Tabletop miniatures", Status=IN_PROGRESS,
         Description="Resin miniatures for a weekly campaign."),
]

# (days ago, printer, title, status, project, [(material, grams or ml)], hours, notes)
PRINTS = [
    (118, "mk4", "Benchy calibration", SUCCESS, None, [("pla_black", 13)], 0.9, None),
    (115, "mk4", "Gridfinity baseplate 6x4", SUCCESS, "organizer", [("pla_black", 142)], 6.5, None),
    (112, "mk4", "Gridfinity bin 2x1 (x8)", SUCCESS, "organizer", [("pla_white", 96)], 4.8, None),
    (109, "x1c", "Drawer insert - screwdrivers", SUCCESS, "organizer", [("petg_blue", 118)], 5.2, None),
    (104, "mk4", "Gridfinity bin 3x2 (x4)", FAILED, "organizer", [("pla_white", 37)], 1.4,
     "Lost bed adhesion on the second layer. Cleaned the sheet with IPA and reprinted."),
    (103, "mk4", "Gridfinity bin 3x2 (x4)", SUCCESS, "organizer", [("pla_white", 104)], 5.0, None),
    (99, "saturn", "Calibration - Ameralabs town", SUCCESS, None, [("resin_grey", 8)], 0.8, None),
    (96, "x1c", "Raspberry Pi 5 case", SUCCESS, None, [("asa_grey", 54), ("petg_blue", 6)], 3.1, None),
    (93, "saturn", "Paladin miniature (x6)", SUCCESS, "minis", [("resin_grey", 41)], 3.6, None),
    (88, "mk4", "Cable chain links", SUCCESS, None, [("petg_blue", 33)], 2.2, None),
    (85, "x1c", "Helmet - crown section", SUCCESS, "helmet", [("asa_grey", 287)], 11.4, None),
    (81, "x1c", "Helmet - left cheek", FAILED, "helmet", [("pla_white", 96)], 4.0,
     "Spaghetti at 40%. Support interface too thin under the overhang."),
    (80, "x1c", "Helmet - left cheek", SUCCESS, "helmet", [("pla_white", 176)], 7.3,
     "Tree supports, interface layers raised to 3."),
    (76, "mk4", "Phone stand", SUCCESS, None, [("pla_silk", 41)], 1.9, None),
    (72, "saturn", "Dragon bust", PARTIAL, "minis", [("resin_grey", 63)], 5.5,
     "One wing tip didn't attach to supports. Repairable with putty."),
    (68, "x1c", "Helmet - right cheek", SUCCESS, "helmet", [("pla_white", 181)], 7.4, None),
    (64, "mk4", "TPU bumper for multimeter", SUCCESS, None, [("tpu_red", 38)], 2.6, None),
    (60, "saturn", "Clear lens covers", SUCCESS, "helmet", [("resin_clear", 22)], 1.7, None),
    (55, "mk4", "Filament spool holder", CANCELLED, None, [("pla_black", 12)], 0.5,
     "Cancelled: wrong infill setting."),
    (54, "mk4", "Filament spool holder", SUCCESS, None, [("pla_black", 88)], 3.9, None),
    (49, "x1c", "Helmet - visor frame", SUCCESS, "helmet", [("asa_grey", 132)], 6.1, None),
    (44, "saturn", "Goblin warband (x10)", SUCCESS, "minis", [("resin_grey", 37)], 3.2, None),
    (40, "mk4", "Wall hooks (x6)", SUCCESS, None, [("petg_blue", 47)], 2.4, None),
    (35, "x1c", "Multi-colour keychains (x12)", SUCCESS, None,
     [("pla_black", 18), ("pla_silk", 14), ("pla_white", 10)], 2.8, None),
    (30, "mk4", "Replacement knob for oven", SUCCESS, None, [("asa_grey", 9)], 0.7, None),
    (25, "saturn", "Wizard tower terrain", FAILED, "minis", [("resin_clear", 29)], 2.5,
     "FEP was cloudy. Partial separation at layer 120. Replaced FEP."),
    (21, "saturn", "Wizard tower terrain", SUCCESS, "minis", [("resin_grey", 58)], 4.6, None),
    (16, "x1c", "Helmet - rear section", SUCCESS, "helmet", [("asa_grey", 243)], 9.8, None),
    (10, "mk4", "Headphone hanger", SUCCESS, None, [("pla_silk", 52)], 2.3, None),
    (6, "x1c", "Helmet - chin guard", SUCCESS, "helmet", [("pla_white", 121)], 5.1, None),
    (3, "saturn", "Rogue miniature (x4)", SUCCESS, "minis", [("resin_grey", 19)], 2.1, None),
    (1, "mk4", "Gridfinity label holders", SUCCESS, None, [("pla_black", 22)], 1.3, None),
]

# (days ago, printer, category, description, done, price, notes)
MAINTENANCE = [
    (100, "mk4", "Cleaning", "Cleaned PEI sheet and lubricated Z rods", True, None, None),
    (82, "x1c", "Calibration", "Ran full calibration after moving the printer", True, None, None),
    (58, "mk4", "Nozzle", "Replaced 0.4 mm brass nozzle", True, "9.99", "Partial clog after TPU."),
    (24, "saturn", "FEP", "Replaced FEP film", True, "14.99", "Cloudy FEP caused a failed print."),
    (12, "x1c", "Belts", "Checked and re-tensioned belts", True, None, None),
    (-7, "mk4", "Cleaning", "Clean and re-grease linear rails", False, None, "Due next week."),
]


# (UserSettingTypeId, value, name) from PrintLogContext's UserSettingType seed.
SETTINGS = [
    (5, "USD", "Currency_Name"),
    (12, "0.17", "Electricity_KwhRate"),
]


class Api:
    def __init__(self, base_url, api_key, dry_run):
        self.base_url = base_url.rstrip("/")
        self.api_key = api_key
        self.dry_run = dry_run
        self.fake_id = 0

    def request(self, method, path, body=None):
        if self.dry_run and method != "GET":
            self.fake_id += 1
            print(f"  [dry-run] {method} {path}")
            return {"id": f"00000000-0000-0000-0000-{self.fake_id:012d}"}
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(self.base_url + path, data=data, method=method, headers={
            "X-Api-Key": self.api_key,
            "Content-Type": "application/json",
            "Accept": "application/json",
        })
        try:
            with urllib.request.urlopen(req, timeout=60) as resp:
                raw = resp.read()
                return json.loads(raw) if raw else None
        except urllib.error.HTTPError as e:
            sys.exit(f"{method} {path} failed: {e.code}\n{e.read().decode(errors='replace')}")


def iso(days_ago, hour=10):
    day = datetime.now(timezone.utc).replace(hour=hour, minute=0, second=0, microsecond=0)
    return (day - timedelta(days=days_ago)).isoformat()


def strip(d, *keys):
    return {k: v for k, v in d.items() if k not in keys}


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--base-url", default="https://api.3dprintlog.com")
    parser.add_argument("--dry-run", action="store_true",
                        help="check the key and the account, then print what would be created")
    args = parser.parse_args()

    api_key = os.environ.get("PRINTLOG_API_KEY")
    if not api_key:
        sys.exit("Set PRINTLOG_API_KEY to an API key created on the reviewer account.")
    api = Api(args.base_url, api_key, args.dry_run)

    existing = api.request("GET", "/api/Printers/summary?includeInactive=true&PageSize=1")
    if existing["paging"]["totalCount"]:
        sys.exit("This account already has printers. Refusing to run, because a second run "
                 "would duplicate everything. Use a fresh account.")

    # Costs need a currency and an electricity rate; without them every cost tile reports
    # RateMissing and a null currency.
    print("Settings")
    for type_id, value, label in SETTINGS:
        api.request("POST", "/api/Users/me/user-settings", {"UserSettingTypeId": type_id, "Value": value})
        print(f"  {label} = {value}")

    rng = random.Random(128)

    print("Printers")
    printers = {}
    for p in PRINTERS:
        created = api.request("POST", "/api/Printers", {**strip(p, "key"), "IsActive": True})
        printers[p["key"]] = created["id"]
        print(f"  {p['Name']}")

    print("Materials")
    materials = {}
    for m in MATERIALS:
        body = {**strip(m, "key", "days_ago"), "IsActive": True, "PurchasePriceCurrency": "USD",
                "PurchaseDate": iso(m["days_ago"])}
        created = api.request("POST", "/api/Filaments", body)
        materials[m["key"]] = (created["id"], m["Source"])
        print(f"  {m['DisplayName']}")

    print("Projects")
    projects = {}
    for p in PROJECTS:
        created = api.request("POST", "/api/Projects",
                              {**strip(p, "key"), "ViewStatus": PRIVATE_PROJECT})
        projects[p["key"]] = created["id"]
        print(f"  {p['Name']}")

    # Oldest first, so the last print on each printer leaves its material loaded.
    print("Prints")
    for days_ago, printer, title, status, project, usage, hours, notes in PRINTS:
        rows = []
        for material, amount in usage:
            material_id, source = materials[material]
            estimate = round(amount * rng.uniform(0.93, 1.06), 1)
            row = {"Filament": {"Id": material_id}, "Source": source, "EstimatedSource": source}
            if source == VOLUME:
                row |= {"VolumeMl": amount, "EstimatedVolumeMl": estimate}
            else:
                row |= {"AmountMg": int(amount * 1000), "EstimatedAmountMg": int(estimate * 1000)}
            rows.append(row)
        seconds = int(hours * 3600)
        api.request("POST", "/api/Prints", {
            "PrinterId": printers[printer],
            "Title": title,
            "Status": status,
            "ViewStatus": PRIVATE_PRINT,
            "AllowComments": False,
            "StartDate": iso(days_ago, hour=rng.choice([8, 9, 13, 18, 20])),
            "PrintTimeInSeconds": seconds,
            "EstimatedPrintTimeInSeconds": int(seconds * rng.uniform(0.9, 1.05)),
            "FilamentUsage": rows,
            "ProjectId": projects[project] if project else None,
            "Notes": notes,
        })
        print(f"  {iso(days_ago)[:10]}  {title}")

    print("Maintenance")
    for days_ago, printer, category, description, done, price, notes in MAINTENANCE:
        api.request("POST", "/api/PrinterMaintenance", {
            "PrinterId": printers[printer],
            "Date": iso(days_ago),
            "Category": category,
            "Description": description,
            "Done": done,
            "PriceValue": price,
            "Notes": notes,
        })
        print(f"  {description}")

    print(f"\nDone: {len(PRINTERS)} printers, {len(MATERIALS)} materials, {len(PROJECTS)} projects, "
          f"{len(PRINTS)} prints, {len(MAINTENANCE)} maintenance entries. Revoke the API key now.")


if __name__ == "__main__":
    main()
