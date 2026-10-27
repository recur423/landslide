#!/usr/bin/env python3
"""Prepare surveyed 3-D geometry and audit rainfall-driven landslide inputs.

This is a data/mesh preparation tool, not a seepage, stability, or LS-DYNA solver.
The generated keyword file deliberately has no material, section, or boundary cards.
Python 3.8+ standard library only.
"""

import argparse
from collections import defaultdict
import csv
import hashlib
import json
import math
import os
from pathlib import Path
import sys

from convert_lsdyna import DataError, finite, positive, temporary_directory, write_json


GRID_COLUMNS = ("i", "j", "x", "y", "z")
RAIN_COLUMNS = ("zone_id", "start_s", "end_s", "intensity_m_per_s")
ZONE_COLUMNS = ("i", "j", "zone_id")
FEATURE_COLUMNS = ("feature_id", "kind", "sequence", "i", "j")
BUDGET_COLUMNS = ("start_s", "end_s", "precipitation_m3", "groundwater_in_m3",
                  "runoff_out_m3", "groundwater_out_m3", "evapotranspiration_m3",
                  "storage_change_m3", "infiltration_m3")
SLIP_COLUMNS = ("time_s", "patch_id", "total_normal_stress_pa", "shear_stress_pa",
                "pore_pressure_pa", "cohesion_pa", "friction_angle_deg", "area_m2")
EVIDENCE_KEYS = ("preEventSurvey", "geologicInvestigation", "rainGauge",
                 "groundwaterAndMoisture", "hydraulicTesting", "strengthTesting",
                 "gravityEquilibrium", "seepageModel", "stabilityModel",
                 "dynaModel", "dynaResults", "fieldValidation")


def required(data, key):
    value = data.get(key)
    if value is None or value == "":
        raise DataError("case requires {}".format(key))
    return value


def source_file(root, value, label):
    if not isinstance(value, str) or not value.strip():
        raise DataError("{} must be a nonempty file path".format(label))
    path = (root / value).resolve()
    if not path.is_file():
        raise DataError("{} file does not exist: {}".format(label, path))
    return path


def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def rows(path, columns):
    with path.open("r", encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if reader.fieldnames is None or set(reader.fieldnames) != set(columns) or len(reader.fieldnames) != len(columns):
            raise DataError("{} needs exactly these headers: {}".format(path, ",".join(columns)))
        for row in reader:
            if None in row or any(v is None or not v.strip() for v in row.values()):
                raise DataError("{} line {} has an incomplete row".format(path, reader.line_num))
            yield reader.line_num, row


def nonnegative_int(value, label):
    try:
        result = int(value)
    except (ValueError, TypeError) as exc:
        raise DataError("{} must be a nonnegative integer".format(label)) from exc
    if str(result) != str(value).strip() or result < 0:
        raise DataError("{} must be a nonnegative integer".format(label))
    return result


def read_grid(path):
    grid = {}
    for line, row in rows(path, GRID_COLUMNS):
        key = (nonnegative_int(row["i"], "grid i"), nonnegative_int(row["j"], "grid j"))
        if key in grid:
            raise DataError("{} line {} repeats grid vertex {}".format(path, line, key))
        grid[key] = tuple(finite(row[k], "grid {}".format(k)) for k in ("x", "y", "z"))
    if not grid:
        raise DataError("{} has no grid vertices".format(path))
    ni = max(i for i, _ in grid) + 1
    nj = max(j for _, j in grid) + 1
    if ni < 2 or nj < 2 or len(grid) != ni * nj:
        raise DataError("{} must be a complete grid of at least 2 by 2 vertices".format(path))
    xs = [grid[i, 0][0] for i in range(ni)]
    ys = [grid[0, j][1] for j in range(nj)]
    if any(xs[i + 1] <= xs[i] for i in range(ni - 1)) or any(ys[j + 1] <= ys[j] for j in range(nj - 1)):
        raise DataError("{} x and y coordinates must increase with i and j".format(path))
    for (i, j), (x, y, _) in grid.items():
        if not math.isclose(x, xs[i], rel_tol=0, abs_tol=1e-8) or not math.isclose(y, ys[j], rel_tol=0, abs_tol=1e-8):
            raise DataError("{} must use an axis-aligned, registered projected grid".format(path))
    return grid, ni, nj


def read_zones(path, ni, nj, rain_zone_ids):
    if path is None:
        if len(rain_zone_ids) != 1:
            raise DataError("multiple rainfall zones require cellZonesCsv")
        return {(i, j): next(iter(rain_zone_ids)) for j in range(nj - 1) for i in range(ni - 1)}
    zones = {}
    for line, row in rows(path, ZONE_COLUMNS):
        i, j = nonnegative_int(row["i"], "zone i"), nonnegative_int(row["j"], "zone j")
        key = (i, j)
        zone = row["zone_id"].strip()
        if i >= ni - 1 or j >= nj - 1 or key in zones or zone not in rain_zone_ids:
            raise DataError("{} line {} has an invalid cell/zone".format(path, line))
        zones[key] = zone
    if len(zones) != (ni - 1) * (nj - 1):
        raise DataError("cellZonesCsv must assign every surface cell exactly once")
    return zones


def read_features(path, ni, nj):
    if path is None:
        return []
    features = defaultdict(list)
    kinds = {"crown", "toe", "gully", "breakline", "scarp"}
    for line, row in rows(path, FEATURE_COLUMNS):
        identity, kind = row["feature_id"].strip(), row["kind"].strip().lower()
        sequence = nonnegative_int(row["sequence"], "feature sequence")
        i, j = nonnegative_int(row["i"], "feature i"), nonnegative_int(row["j"], "feature j")
        if not identity or kind not in kinds or i >= ni or j >= nj:
            raise DataError("{} line {} has an invalid feature kind or grid vertex".format(path, line))
        features[identity].append((sequence, kind, i, j))
    result = []
    for identity, points in features.items():
        points.sort()
        if len(points) < 2 or {p[1] for p in points} != {points[0][1]}:
            raise DataError("feature {} needs at least two vertices of one kind".format(identity))
        if [p[0] for p in points] != list(range(len(points))):
            raise DataError("feature {} sequence must start at zero without gaps".format(identity))
        for left, right in zip(points, points[1:]):
            if abs(left[2] - right[2]) + abs(left[3] - right[3]) != 1:
                raise DataError("feature {} must follow grid edges without jumps".format(identity))
        result.extend((identity, *p) for p in points)
    return result


def read_rain(path, event_start):
    by_zone = defaultdict(list)
    for line, row in rows(path, RAIN_COLUMNS):
        zone = row["zone_id"].strip()
        start, end = finite(row["start_s"], "rain start"), finite(row["end_s"], "rain end")
        intensity = finite(row["intensity_m_per_s"], "rain intensity")
        if end <= start or intensity < 0:
            raise DataError("{} line {} has nonpositive duration or negative intensity".format(path, line))
        by_zone[zone].append((start, end, intensity))
    if not by_zone:
        raise DataError("rainfallCsv has no intervals")
    extents = set()
    for zone, intervals in by_zone.items():
        intervals.sort()
        for previous, current in zip(intervals, intervals[1:]):
            if not math.isclose(previous[1], current[0], rel_tol=0, abs_tol=1e-9):
                raise DataError("rainfall zone {} has a gap or overlap".format(zone))
        start, end = intervals[0][0], intervals[-1][1]
        if not start <= event_start < end:
            raise DataError("rainfall zone {} does not span eventStartSeconds".format(zone))
        extents.add((start, end))
    if len(extents) != 1:
        raise DataError("all rainfall zones must cover the same time extent")
    return dict(by_zone)


def rain_volume(rain, zone_areas, start, end):
    volume = 0.0
    for zone, intervals in rain.items():
        area = zone_areas.get(zone, 0.0)
        for a, b, intensity in intervals:
            volume += area * intensity * max(0.0, min(end, b) - max(start, a))
    return volume


def audit_budget(path, rain, zone_areas):
    report, last_end = [], None
    rain_start = next(iter(rain.values()))[0][0]
    rain_end = next(iter(rain.values()))[-1][1]
    for line, row in rows(path, BUDGET_COLUMNS):
        start, end = finite(row["start_s"], "budget start"), finite(row["end_s"], "budget end")
        if end <= start or start < rain_start or end > rain_end or (last_end is not None and start < last_end):
            raise DataError("{} line {} has an invalid or overlapping time interval".format(path, line))
        last_end = end
        values = {key: finite(row[key], "budget {}".format(key)) for key in BUDGET_COLUMNS[2:]}
        if any(values[k] < 0 for k in values if k != "storage_change_m3"):
            raise DataError("{} line {} has a negative unsigned water volume".format(path, line))
        expected = rain_volume(rain, zone_areas, start, end)
        residual = (values["precipitation_m3"] + values["groundwater_in_m3"]
                    - values["runoff_out_m3"] - values["groundwater_out_m3"]
                    - values["evapotranspiration_m3"] - values["storage_change_m3"])
        report.append({"startSeconds": start, "endSeconds": end,
                       "rainfallVolumeFromHyetographM3": expected,
                       "reportedPrecipitationDifferenceM3": values["precipitation_m3"] - expected,
                       "wholeDomainWaterBalanceResidualM3": residual,
                       "infiltrationM3DiagnosticOnly": values["infiltration_m3"]})
    if not report:
        raise DataError("hydroBudgetCsv has no intervals")
    return report


def audit_slip(path):
    by_time = defaultdict(list)
    for line, row in rows(path, SLIP_COLUMNS):
        time = finite(row["time_s"], "slip time")
        patch = row["patch_id"].strip()
        normal = finite(row["total_normal_stress_pa"], "normal stress")
        shear = finite(row["shear_stress_pa"], "shear stress")
        pore = finite(row["pore_pressure_pa"], "pore pressure")
        cohesion = finite(row["cohesion_pa"], "cohesion")
        friction = finite(row["friction_angle_deg"], "friction angle")
        area = finite(row["area_m2"], "patch area")
        if normal < 0 or shear < 0 or pore < 0 or cohesion < 0 or not 0 <= friction < 90 or area <= 0 or pore > normal:
            raise DataError("{} line {} is outside the saturated, compressed slip-patch screening domain".format(path, line))
        if any(p[0] == patch for p in by_time[time]):
            raise DataError("{} line {} repeats a patch at one time".format(path, line))
        resistance = (cohesion + (normal - pore) * math.tan(math.radians(friction))) * area
        driving = shear * area
        by_time[time].append((patch, resistance, driving))
    if not by_time:
        raise DataError("slipPatchesCsv has no rows")
    ids = {patch for patch, _, _ in next(iter(by_time.values()))}
    report = []
    for time in sorted(by_time):
        records = by_time[time]
        if {patch for patch, _, _ in records} != ids:
            raise DataError("slip patch set changes between times")
        resistance = math.fsum(p[1] for p in records)
        driving = math.fsum(p[2] for p in records)
        report.append({"timeSeconds": time, "saturatedSlipPatchScreeningFS": resistance / driving if driving > 0 else None,
                       "resistingForceN": resistance, "drivingForceN": driving})
    bracket = None
    for previous, current in zip(report, report[1:]):
        if previous["saturatedSlipPatchScreeningFS"] is not None and current["saturatedSlipPatchScreeningFS"] is not None:
            if previous["saturatedSlipPatchScreeningFS"] > 1 >= current["saturatedSlipPatchScreeningFS"]:
                bracket = [previous["timeSeconds"], current["timeSeconds"]]
                break
    return {"records": report, "firstFSOneCrossingBracketSeconds": bracket,
            "limitation": "Saturated prescribed-surface force screening only; not a 3-D failure or onset prediction."}


def prepare(case_path, output):
    case_path, output = Path(case_path).resolve(), Path(output).resolve()
    if output.exists():
        raise DataError("output already exists; select a new directory")
    with case_path.open("r", encoding="utf-8") as stream:
        case = json.load(stream)
    if case.get("schemaVersion") != 1:
        raise DataError("case schemaVersion must be 1")
    root = case_path.parent
    for key in ("caseId", "horizontalCrs", "verticalDatum"):
        if not isinstance(required(case, key), str):
            raise DataError("{} must be text".format(key))
    scale = positive(required(case, "lengthUnitToM"), "lengthUnitToM")
    origin = required(case, "localOriginSourceUnits")
    if not isinstance(origin, list) or len(origin) != 3:
        raise DataError("localOriginSourceUnits must be three numbers")
    origin = [finite(v, "local origin") for v in origin]
    event_start = finite(required(case, "eventStartSeconds"), "eventStartSeconds")
    surface_path = source_file(root, required(case, "surfaceGridCsv"), "surfaceGridCsv")
    surface, ni, nj = read_grid(surface_path)
    layer_specs = required(case, "layers")
    if not isinstance(layer_specs, list) or not layer_specs:
        raise DataError("layers must contain at least one surveyed layer bottom")
    paths = {"surfaceGridCsv": surface_path}
    surfaces, layers, part_ids = [surface], [], set()
    for index, spec in enumerate(layer_specs):
        if not isinstance(spec, dict) or not isinstance(spec.get("name"), str) or not spec["name"].strip():
            raise DataError("each layer needs a name")
        part_id = nonnegative_int(spec.get("partId"), "layer partId")
        if part_id == 0 or part_id in part_ids:
            raise DataError("layer part IDs must be unique positive integers")
        part_ids.add(part_id)
        path = source_file(root, required(spec, "bottomGridCsv"), "layer bottomGridCsv")
        bottom, bi, bj = read_grid(path)
        if (bi, bj) != (ni, nj):
            raise DataError("layer grids must match surface grid dimensions")
        for key in surface:
            if not math.isclose(bottom[key][0], surface[key][0], rel_tol=0, abs_tol=1e-8) or not math.isclose(bottom[key][1], surface[key][1], rel_tol=0, abs_tol=1e-8):
                raise DataError("all layer surfaces must share registered x/y coordinates")
            if not bottom[key][2] < surfaces[-1][key][2]:
                raise DataError("layer {} has zero/negative thickness at {}".format(spec["name"], key))
        surfaces.append(bottom)
        layers.append({"name": spec["name"], "partId": part_id,
                       "minimumThicknessM": min((surfaces[-2][key][2] - bottom[key][2]) * scale for key in surface),
                       "maximumThicknessM": max((surfaces[-2][key][2] - bottom[key][2]) * scale for key in surface)})
        paths["layerBottom{}".format(index)] = path
    rain_path = source_file(root, required(case, "rainfallCsv"), "rainfallCsv")
    rain = read_rain(rain_path, event_start)
    paths["rainfallCsv"] = rain_path
    zone_path = source_file(root, case["cellZonesCsv"], "cellZonesCsv") if case.get("cellZonesCsv") else None
    if zone_path:
        paths["cellZonesCsv"] = zone_path
    zones = read_zones(zone_path, ni, nj, set(rain))
    feature_path = source_file(root, case["terrainFeaturesCsv"], "terrainFeaturesCsv") if case.get("terrainFeaturesCsv") else None
    if feature_path:
        paths["terrainFeaturesCsv"] = feature_path
    features = read_features(feature_path, ni, nj)
    zone_areas = defaultdict(float)
    for (i, j), zone in zones.items():
        zone_areas[zone] += (surface[i + 1, j][0] - surface[i, j][0]) * (surface[i, j + 1][1] - surface[i, j][1]) * scale * scale
    if set(zone_areas) != set(rain):
        raise DataError("each rainfall zone must cover at least one cell")
    evidence = case.get("evidence", {})
    if not isinstance(evidence, dict):
        raise DataError("evidence must be an object")
    present, missing = [], []
    for key in EVIDENCE_KEYS:
        if evidence.get(key):
            paths["evidence." + key] = source_file(root, evidence[key], "evidence." + key)
            present.append(key)
        else:
            missing.append(key)
    budget = None
    if case.get("hydroBudgetCsv"):
        path = source_file(root, case["hydroBudgetCsv"], "hydroBudgetCsv")
        paths["hydroBudgetCsv"] = path
        budget = audit_budget(path, rain, zone_areas)
    slip = None
    if case.get("slipPatchesCsv"):
        path = source_file(root, case["slipPatchesCsv"], "slipPatchesCsv")
        paths["slipPatchesCsv"] = path
        slip = audit_slip(path)
    # Surface order is top-to-bottom. Shared interfaces have one node set each.
    node_id = lambda level, i, j: 1 + level * ni * nj + j * ni + i
    cell_count = (ni - 1) * (nj - 1)
    if len(surfaces) * ni * nj > 2147483647 or cell_count * len(layers) > 2147483647:
        raise DataError("mesh IDs exceed LS-DYNA integer range")
    output.parent.mkdir(parents=True, exist_ok=True)
    with temporary_directory(output.parent, ".rainfall-case-") as temp:
        staging = temp / "package"
        staging.mkdir()
        mesh = staging / "mesh_only.k"
        with mesh.open("w", encoding="ascii", newline="\n") as stream:
            stream.write("$ GEOMETRY ONLY; NOT A RUNNABLE OR VALIDATED LANDSLIDE MODEL\n*NODE\n")
            for level, grid in enumerate(surfaces):
                for j in range(nj):
                    for i in range(ni):
                        x, y, z = grid[i, j]
                        p = ((x - origin[0]) * scale, (y - origin[1]) * scale, (z - origin[2]) * scale)
                        stream.write("{},{:.12g},{:.12g},{:.12g}\n".format(node_id(level, i, j), *p))
            stream.write("*ELEMENT_SOLID\n$ eid,pid,n1,n2,n3,n4,n5,n6,n7,n8\n")
            eid = 1
            for level, layer in enumerate(layers):
                for j in range(nj - 1):
                    for i in range(ni - 1):
                        # LS-DYNA hex: lower face 1-4 and upper face 5-8.
                        corners = ((i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1))
                        nodes = [node_id(level + 1, a, b) for a, b in corners]
                        nodes += [node_id(level, a, b) for a, b in corners]
                        stream.write(",".join(str(n) for n in (eid, layer["partId"], *nodes)) + "\n")
                        eid += 1
        cells_out = staging / "surface_cells.csv"
        with cells_out.open("w", encoding="utf-8", newline="") as stream:
            writer = csv.writer(stream)
            writer.writerow(("i", "j", "center_x_local_m", "center_y_local_m", "slope_deg",
                             "downhill_x", "downhill_y", "projected_area_m2", "rain_zone_id"))
            for j in range(nj - 1):
                for i in range(ni - 1):
                    z00, z10 = surface[i, j][2], surface[i + 1, j][2]
                    z01, z11 = surface[i, j + 1][2], surface[i + 1, j + 1][2]
                    dx = surface[i + 1, j][0] - surface[i, j][0]
                    dy = surface[i, j + 1][1] - surface[i, j][1]
                    gx = (z10 + z11 - z00 - z01) / (2 * dx)
                    gy = (z01 + z11 - z00 - z10) / (2 * dy)
                    gradient = math.hypot(gx, gy)
                    downhill = (-gx / gradient, -gy / gradient) if gradient else (0.0, 0.0)
                    writer.writerow((i, j, ((surface[i, j][0] + surface[i + 1, j][0]) / 2 - origin[0]) * scale,
                                     ((surface[i, j][1] + surface[i, j + 1][1]) / 2 - origin[1]) * scale,
                                     math.degrees(math.atan(gradient)), *downhill, dx * dy * scale * scale, zones[i, j]))
        if features:
            with (staging / "terrain_feature_vertices.csv").open("w", encoding="utf-8", newline="") as stream:
                writer = csv.writer(stream)
                writer.writerow(("feature_id", "kind", "sequence", "node_id", "x_local_m", "y_local_m", "z_local_m"))
                for identity, sequence, kind, i, j in features:
                    x, y, z = surface[i, j]
                    writer.writerow((identity, kind, sequence, node_id(0, i, j),
                                     (x - origin[0]) * scale, (y - origin[1]) * scale, (z - origin[2]) * scale))
        with (staging / "cell_rainfall_zones.csv").open("w", encoding="utf-8", newline="") as stream:
            writer = csv.writer(stream)
            writer.writerow(ZONE_COLUMNS)
            for (i, j), zone in sorted(zones.items()):
                writer.writerow((i, j, zone))
        rain_out = staging / "rainfall_intervals.csv"
        with rain_out.open("w", encoding="utf-8", newline="") as stream:
            writer = csv.writer(stream)
            writer.writerow(RAIN_COLUMNS)
            for zone in sorted(rain):
                for interval in rain[zone]:
                    writer.writerow((zone, *interval))
        rainfall = {}
        for zone, intervals in rain.items():
            area = zone_areas[zone]
            rainfall[zone] = {"projectedAreaM2": area,
                              "antecedentDepthM": sum(intensity * max(0, min(end, event_start) - start)
                                                      for start, end, intensity in intervals if start < event_start),
                              "totalDepthM": sum((end - start) * intensity for start, end, intensity in intervals),
                              "totalRainfallVolumeM3": rain_volume({zone: intervals}, zone_areas, intervals[0][0], intervals[-1][1])}
        report = {"schemaVersion": 1, "status": "prepared-not-solved", "caseId": case["caseId"],
                  "coordinateReference": {"horizontalCrs": case["horizontalCrs"], "verticalDatum": case["verticalDatum"],
                                          "lengthUnitToM": scale, "localOriginSourceUnits": origin},
                  "gridVertices": [ni, nj], "nodeCount": len(surfaces) * ni * nj,
                  "solidElementCount": cell_count * len(layers), "layersTopToBottom": layers,
                  "terrainFeatureCount": len({f[0] for f in features}),
                  "terrainFeaturesMissing": feature_path is None,
                  "rainfall": rainfall, "eventStartSeconds": event_start,
                  "hydroBudgetDiagnostics": budget, "slipPatchDiagnostics": slip,
                  "evidencePresent": present, "evidenceMissing": missing,
                  "inputSha256": {name: digest(path) for name, path in paths.items()},
                  "limitations": ["mesh_only.k is an include fragment with no material, section, contact, gravity, hydraulic or boundary cards and must not be solved as-is.",
                                  "Gridded interfaces are interpolated survey products, not proof of soil thickness between observations.",
                                  "Toe, gully and crown features must be surveyed and snapped to grid edges; they are not inferred from slope alone.",
                                  "Rainfall volumes are atmospheric input, not automatically infiltrated water.",
                                  "Water budget and slip-patch results are audits of externally computed data, not solver calculations.",
                                  "A slip-patch FS crossing alone does not establish 3-D failure or an exact onset time.",
                                  "No unsaturated seepage or pore-pressure coupling is solved here."]}
        write_json(staging / "case_report.json", report)
        if any(digest(path) != report["inputSha256"][name] for name, path in paths.items()):
            raise DataError("an input changed during preparation")
        os.rename(str(staging), str(output))
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--case", type=Path, required=True, help="site case JSON")
    parser.add_argument("--output", type=Path, required=True, help="new output directory")
    args = parser.parse_args(argv)
    try:
        report = prepare(args.case, args.output)
        print("Prepared {} nodes, {} solids; {} evidence items missing. No physics solve performed.".format(
            report["nodeCount"], report["solidElementCount"], len(report["evidenceMissing"])))
        print("Wrote " + str(args.output.resolve()))
        return 0
    except (DataError, OSError, ValueError, OverflowError) as exc:
        print("ERROR: " + str(exc), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
