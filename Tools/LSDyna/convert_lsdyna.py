#!/usr/bin/env python3
"""Convert a declared, normalized LS-DYNA nodal export to Unity replay data.

This does not parse d3plot or run LS-DYNA. See docs/LSDynaDataFormat.md.
Only the Python standard library is needed (Python 3.8+).
"""

import argparse
from contextlib import contextmanager
import csv
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import struct
import sys
import uuid


MAGIC = b"LSDYNA01"
COLUMNS = ("time", "node_id", "x", "y", "z", "vx", "vy", "vz", "mass")
ENERGY_COLUMNS = ("time", "kinetic_energy", "internal_energy", "external_work",
                  "hourglass_energy")
FLOAT_RECORD = struct.Struct("<6f")
MAX_NODES = 200000
MAX_FRAMES = 100000
MAX_TRIANGLE_INDICES = 3000000
MAX_MANIFEST_BYTES = 32 * 1024 * 1024


class DataError(ValueError):
    """An export is incomplete, ambiguous, or not valid for this schema."""


@contextmanager
def temporary_directory(parent, prefix):
    """Inherit the parent's ACL, including in Windows app-container sandboxes.

    Python 3.13's Windows mkdir(0700) ACL can deny access to child paths inside
    a sandbox, so TemporaryDirectory may loop on PermissionError. A private,
    unpredictable child of the caller-selected parent avoids that behavior.
    """
    parent = Path(parent).resolve()
    location = parent / (prefix + uuid.uuid4().hex)
    location.mkdir(mode=0o777)
    try:
        yield location
    finally:
        if location.exists():
            resolved = location.resolve()
            if resolved.parent != parent or not resolved.name.startswith(prefix):
                raise DataError("refusing to clean a temporary directory outside its parent")
            shutil.rmtree(str(resolved))


def finite(value, label):
    try:
        number = float(value)
    except (ValueError, TypeError, OverflowError) as exc:
        raise DataError("{} must be a finite number: {!r}".format(label, value)) from exc
    if not math.isfinite(number):
        raise DataError("{} must be finite".format(label))
    return number


def positive(value, label):
    number = finite(value, label)
    if number <= 0:
        raise DataError("{} must be greater than zero".format(label))
    return number


def node_id(value, label="node_id"):
    try:
        number = int(value)
    except (ValueError, TypeError, OverflowError) as exc:
        raise DataError("{} must be an integer".format(label)) from exc
    if str(value).strip() not in (str(number), "+" + str(number)):
        raise DataError("{} must use integer notation".format(label))
    if not 1 <= number <= 2147483647:
        raise DataError("{} must be in [1, 2147483647]".format(label))
    return number


def f32(value, label):
    try:
        rounded = struct.unpack("<f", struct.pack("<f", finite(value, label)))[0]
    except (OverflowError, struct.error) as exc:
        raise DataError("{} exceeds float32 range".format(label)) from exc
    if not math.isfinite(rounded):
        raise DataError("{} exceeds float32 range".format(label))
    return rounded


def vector(values, label):
    if len(values) != 3:
        raise DataError("{} requires three numbers".format(label))
    return tuple(finite(v, label) for v in values)


def axis_spec(values):
    if isinstance(values, str):
        values = values.split(",")
    values = tuple(v.strip().lower() for v in values)
    if len(values) != 3 or {v.lstrip("+-") for v in values} != {"x", "y", "z"}:
        raise DataError("axis mapping must be a signed permutation, e.g. x,z,y")
    if any(v not in ("x", "y", "z", "+x", "+y", "+z", "-x", "-y", "-z")
           for v in values):
        raise DataError("invalid signed axis mapping")
    return values


def mapped(values, axes):
    return tuple((-1.0 if a.startswith("-") else 1.0) * values["xyz".index(a[-1])]
                 for a in axes)


def determinant(axes):
    permutation = ["xyz".index(a[-1]) for a in axes]
    inversions = sum(permutation[i] > permutation[j] for i in range(3)
                     for j in range(i + 1, 3))
    return (-1 if inversions % 2 else 1) * math.prod(-1 if a.startswith("-") else 1 for a in axes)


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def csv_rows(path, required):
    with Path(path).open("r", encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if reader.fieldnames is None or len(set(reader.fieldnames)) != len(reader.fieldnames):
            raise DataError("{} has missing or duplicate CSV headers".format(path))
        if set(reader.fieldnames) != set(required):
            raise DataError("{} needs exactly these CSV headers: {}".format(path, ",".join(required)))
        for row in reader:
            if None in row or any(v is None or not v.strip() for v in row.values()):
                raise DataError("{} line {} has an incomplete or oversized row".format(path, reader.line_num))
            yield reader.line_num, row


def source_frames(path):
    """One frame in memory; interleaved/unordered times are deliberately rejected."""
    current_time = None
    nodes = {}
    for line, row in csv_rows(path, COLUMNS):
        prefix = "line {}".format(line)
        time = finite(row["time"], prefix + " time")
        if current_time is None:
            current_time = time
        elif time != current_time:
            if time <= current_time:
                raise DataError("{} time must increase between contiguous frame groups".format(prefix))
            yield current_time, nodes
            current_time, nodes = time, {}
        identity = node_id(row["node_id"], prefix + " node_id")
        if identity in nodes:
            raise DataError("{} repeats node {} in a frame".format(prefix, identity))
        coords = tuple(finite(row[c], prefix + " " + c) for c in COLUMNS[2:8])
        mass = positive(row["mass"], prefix + " mass")
        nodes[identity] = coords + (mass,)
        if len(nodes) > MAX_NODES:
            raise DataError("node count exceeds Unity reader limit of {}".format(MAX_NODES))
    if current_time is None:
        raise DataError("nodal CSV contains no frames")
    yield current_time, nodes


def read_triangles(path, ids, axes):
    if path is None:
        return []
    index = {identity: i for i, identity in enumerate(ids)}
    result, seen = [], set()
    for line, row in csv_rows(path, ("a", "b", "c")):
        source = tuple(node_id(row[c], "triangle line {}".format(line)) for c in ("a", "b", "c"))
        if len(set(source)) != 3 or any(i not in index for i in source):
            raise DataError("triangle line {} contains repeated or unknown node IDs".format(line))
        if tuple(sorted(source)) in seen:
            raise DataError("triangle line {} duplicates a triangle".format(line))
        seen.add(tuple(sorted(source)))
        tri = [index[i] for i in source]
        # Preserve the source face orientation when the signed axis map reflects space.
        if determinant(axes) < 0:
            tri[1], tri[2] = tri[2], tri[1]
        result.extend(tri)
        if len(result) > MAX_TRIANGLE_INDICES:
            raise DataError("triangle index count exceeds Unity reader limit of {}".format(MAX_TRIANGLE_INDICES))
    if not result:
        raise DataError("triangle CSV contains no triangles")
    return result


def frame_diagnostics(time, frame, masses, gravity):
    total = math.fsum(masses)
    center = [math.fsum(m * state[a] for m, state in zip(masses, frame)) / total for a in range(3)]
    momentum = [math.fsum(m * state[a + 3] for m, state in zip(masses, frame)) for a in range(3)]
    kinetic = math.fsum(0.5 * m * math.fsum(v * v for v in state[3:])
                        for m, state in zip(masses, frame))
    potential = math.fsum(-m * math.fsum(g * p for g, p in zip(gravity, state[:3]))
                          for m, state in zip(masses, frame))
    return {"timeSeconds": time, "totalMassKg": total, "centerOfMassM": center,
            "momentumKgMPerS": momentum, "kineticEnergyJ": kinetic,
            "gravitationalPotentialEnergyJ": potential,
            "mechanicalEnergyJ": kinetic + potential,
            "maxSpeedMPerS": max(math.sqrt(math.fsum(v * v for v in state[3:])) for state in frame)}


def interval_diagnostics(t0, t1, previous, current, ids):
    dt = positive(t1 - t0, "time interval in seconds")
    max_error, worst_id, squares = -1.0, None, []
    for identity, left, right in zip(ids, previous, current):
        residual = [right[a] - left[a] - 0.5 * (left[a + 3] + right[a + 3]) * dt for a in range(3)]
        squared = math.fsum(v * v for v in residual)
        error = math.sqrt(squared)
        squares.append(squared)
        if error > max_error:
            max_error, worst_id = error, identity
    return {"startTimeSeconds": t0, "endTimeSeconds": t1,
            "rmsPositionVelocityResidualM": math.sqrt(math.fsum(squares) / len(ids)),
            "maxPositionVelocityResidualM": max_error, "worstNodeId": worst_id}


def energy_diagnostics(path, length_to_m, time_to_s, mass_to_kg):
    """Inspect explicit energy columns without assuming a solver energy convention."""
    factor = finite(mass_to_kg * (length_to_m / time_to_s) ** 2, "energy unit conversion")
    records, previous = [], None
    for line, row in csv_rows(path, ENERGY_COLUMNS):
        time = finite(finite(row["time"], "energy time") * time_to_s, "energy time seconds")
        if previous is not None and time <= previous:
            raise DataError("energy line {} time must increase".format(line))
        previous = time
        values = {c: finite(finite(row[c], "energy " + c) * factor, "energy SI " + c)
                  for c in ENERGY_COLUMNS[1:]}
        if values["kinetic_energy"] < 0:
            raise DataError("energy kinetic_energy must not be negative")
        internal = values["internal_energy"]
        records.append({"timeSeconds": time, "kineticEnergyJ": values["kinetic_energy"],
                        "internalEnergyJ": internal, "externalWorkJ": values["external_work"],
                        "hourglassEnergyJ": values["hourglass_energy"],
                        "absoluteHourglassToInternalRatio": abs(values["hourglass_energy"] / internal)
                        if abs(internal) > 1e-20 else None})
    if not records:
        raise DataError("energy CSV contains no rows")
    first = records[0]
    for row in records:
        row["deltaKineticPlusInternalMinusExternalWorkJ"] = (
            row["kineticEnergyJ"] + row["internalEnergyJ"] - row["externalWorkJ"]
            - first["kineticEnergyJ"] - first["internalEnergyJ"] + first["externalWorkJ"])
    return {"status": "diagnostic-only", "energyUnitToJ": factor, "records": records,
            "interpretation": "Delta(K+internal)-Delta(external work) is only a diagnostic. "
            "Confirm gravity, contact, damping, erosion, added mass and hourglass accounting in the solver. "
            "Hourglass energy is reported separately, not blindly added to internal energy. "
            "No energy conservation pass/fail is inferred."}


def write_json(path, payload):
    with Path(path).open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(payload, stream, ensure_ascii=False, indent=2, allow_nan=False)
        stream.write("\n")


def convert(input_csv, output, *, source_description, length_to_m, time_to_s, mass_to_kg,
            source_origin=(0, 0, 0), axis_mapping=("x", "z", "y"), unity_offset=(0, 0, 0),
            triangles_csv=None, energy_csv=None, gravity=(0, -9.81, 0),
            residual_warning_m=0.05, provenance_kind="solver-export", extra_metadata=None,
            site_case_report=None):
    input_csv, output = Path(input_csv), Path(output)
    if not source_description or not source_description.strip():
        raise DataError("an explicit source description is required; include solver job and export details")
    if provenance_kind not in ("solver-export", "analytic-verification"):
        raise DataError("unsupported provenance kind")
    if site_case_report is not None and provenance_kind != "solver-export":
        raise DataError("a site case report cannot be attached to analytical verification data")
    length_to_m = positive(length_to_m, "length_to_m")
    time_to_s = positive(time_to_s, "time_to_s")
    mass_to_kg = positive(mass_to_kg, "mass_to_kg")
    residual_warning_m = positive(residual_warning_m, "residual_warning_m")
    origin, offset, gravity = vector(source_origin, "source_origin"), vector(unity_offset, "unity_offset"), vector(gravity, "gravity")
    axes = axis_spec(axis_mapping)
    velocity_factor = positive(length_to_m / time_to_s, "velocity unit conversion")
    if output.exists():
        raise DataError("output directory already exists; choose a new directory: {}".format(output))
    if not input_csv.is_file():
        raise DataError("nodal CSV does not exist: {}".format(input_csv))
    hashes = {"nodalCsv": sha256(input_csv)}
    if triangles_csv is not None:
        hashes["trianglesCsv"] = sha256(triangles_csv)
    if energy_csv is not None:
        hashes["energyCsv"] = sha256(energy_csv)
    site_case = None
    if site_case_report is not None:
        site_case_report = Path(site_case_report)
        with site_case_report.open("r", encoding="utf-8") as stream:
            report = json.load(stream)
        if (not isinstance(report, dict) or report.get("schemaVersion") != 1 or
                report.get("status") != "prepared-not-solved" or
                not isinstance(report.get("caseId"), str) or not report["caseId"].strip() or
                not isinstance(report.get("evidenceMissing"), list)):
            raise DataError("site case report must be a prepare_rainfall_case.py report; it is not physical validation")
        hashes["siteCaseReport"] = sha256(site_case_report)
        site_case = {"file": "site_case_report.json", "sha256": hashes["siteCaseReport"],
                     "caseId": report["caseId"], "evidenceMissingCount": len(report["evidenceMissing"]),
                     "status": "prepared-not-solved"}
    output.parent.mkdir(parents=True, exist_ok=True)
    with temporary_directory(output.parent, ".lsdyna-convert-") as temporary:
        staging = Path(temporary) / "dataset"
        staging.mkdir()
        ids = masses = source_masses = previous = None
        times, files, stats, intervals, frame_hashes = [], [], [], [], []
        max_quantization = 0.0
        for frame_number, (source_time, nodes) in enumerate(source_frames(input_csv)):
            if frame_number >= MAX_FRAMES:
                raise DataError("frame count exceeds Unity reader limit of {}".format(MAX_FRAMES))
            time = finite(source_time * time_to_s, "time in seconds")
            if times and time <= times[-1]:
                raise DataError("converted timestamps are not strictly increasing")
            if ids is None:
                ids = sorted(nodes)
                source_masses = [nodes[i][6] for i in ids]
                masses = [positive(f32(m * mass_to_kg, "mass in kg"), "float32 mass in kg") for m in source_masses]
            elif set(nodes) != set(ids):
                raise DataError("frame {} has a different node set".format(frame_number))
            frame = []
            for i, identity in enumerate(ids):
                source = nodes[identity]
                if source[6] != source_masses[i]:
                    raise DataError("frame {} changes mass of node {}".format(frame_number, identity))
                # Subtract in double precision BEFORE float32 encoding or unit conversion.
                position = mapped(tuple((source[a] - origin[a]) * length_to_m for a in range(3)), axes)
                position = tuple(p + offset[a] for a, p in enumerate(position))
                velocity = mapped(tuple(source[a + 3] * velocity_factor for a in range(3)), axes)
                transformed = position + velocity
                rounded = tuple(f32(v, "frame {} node {} coordinate/velocity".format(frame_number, identity)) for v in transformed)
                max_quantization = max(max_quantization, math.sqrt(math.fsum((position[a] - rounded[a]) ** 2 for a in range(3))))
                frame.append(rounded)
            file_name = "frame_{:06d}.bytes".format(frame_number)
            with (staging / file_name).open("wb") as stream:
                stream.write(MAGIC)
                stream.write(struct.pack("<i", len(ids)))
                for state in frame:
                    stream.write(FLOAT_RECORD.pack(*state))
            frame_hashes.append(sha256(staging / file_name))
            stats.append(frame_diagnostics(time, frame, masses, gravity))
            if previous is not None:
                intervals.append(interval_diagnostics(times[-1], time, previous, frame, ids))
            times.append(time)
            files.append(file_name)
            previous = frame
        triangles = read_triangles(triangles_csv, ids, axes)
        energy = energy_diagnostics(energy_csv, length_to_m, time_to_s, mass_to_kg) if energy_csv else None
        max_residual = max((row["maxPositionVelocityResidualM"] for row in intervals), default=0.0)
        warnings = []
        if not intervals:
            warnings.append("Only one frame was supplied; position/velocity time consistency cannot be checked.")
        if max_residual > residual_warning_m:
            warnings.append("Position/velocity trapezoidal integration residual exceeds {} m; "
                            "check timestamps, units, component conventions and output sampling.".format(residual_warning_m))
        if max_quantization > residual_warning_m:
            warnings.append("float32 position quantization exceeds the residual warning threshold; "
                            "use a source origin closer to the model and a small Unity offset.")
        if energy is None:
            warnings.append("No solver energy history was supplied; conservation and energy balance are not verified.")
        validation = {"schemaVersion": 1, "status": "diagnostic-only", "warnings": warnings,
                      "gravityMPerS2": list(gravity), "residualWarningThresholdM": residual_warning_m,
                      "maxPositionQuantizationErrorM": max_quantization,
                      "maxPositionVelocityResidualM": max_residual,
                      "frameDiagnostics": stats, "intervalDiagnostics": intervals,
                      "solverEnergyDiagnostics": energy,
                      "limitations": [
                          "Checks use the actual float32 replay values and constant nodal masses.",
                          "Trapezoidal residual includes output sampling/truncation error; a small residual is not physical validation.",
                          "Nodal kinetic energy omits rotational/internal degrees of freedom and can differ from solver global energy.",
                          "Gravity must be stated in Unity axes. Potential energy has an arbitrary origin.",
                          "Material behavior, contacts, pore pressure, mesh convergence, erosion and solver stability require solver/model review.",
                          "No position, velocity, energy or mass is altered to force conservation."]}
        summary = "{} nodes; {} frames; {:.9g} s to {:.9g} s; max position/velocity residual {:.6g} m. Diagnostics only; physics not certified.".format(
            len(ids), len(times), times[0], times[-1], max_residual)
        manifest = {"schemaVersion": 1, "solver": "LS-DYNA" if provenance_kind == "solver-export" else "ANALYTIC",
                    "provenanceKind": provenance_kind, "sourceDescription": source_description.strip(),
                    "sourceSha256": hashes["nodalCsv"], "sourceHashes": hashes,
                    "coordinateConvention": "Unity-local-m", "timeUnit": "s",
                    "nodeIds": ids, "massesKg": masses, "triangles": triangles,
                    "timesSeconds": times, "frameFiles": files, "frameSha256": frame_hashes,
                    "validationSummary": summary,
                    "unitConversion": {"lengthToM": length_to_m, "timeToS": time_to_s, "massToKg": mass_to_kg},
                    "sourceOrigin": list(origin), "axisMapping": list(axes), "unityOffsetM": list(offset),
                    "triangleWindingReversed": determinant(axes) < 0,
                    "gravityMPerS2": list(gravity)}
        if extra_metadata:
            manifest["verificationModel"] = extra_metadata
        if site_case is not None:
            manifest["siteCaseReport"] = site_case
        # Ensure sources did not change during a long conversion.
        if sha256(input_csv) != hashes["nodalCsv"]:
            raise DataError("nodal CSV changed during conversion")
        for label, path in (("trianglesCsv", triangles_csv), ("energyCsv", energy_csv),
                            ("siteCaseReport", site_case_report)):
            if path is not None and sha256(path) != hashes[label]:
                raise DataError("{} changed during conversion".format(label))
        if site_case_report is not None:
            shutil.copyfile(site_case_report, staging / "site_case_report.json")
        write_json(staging / "validation.json", validation)
        write_json(staging / "dataset.json", manifest)
        if (staging / "dataset.json").stat().st_size > MAX_MANIFEST_BYTES:
            raise DataError("dataset.json exceeds Unity reader limit of {} bytes".format(MAX_MANIFEST_BYTES))
        if output.exists():
            raise DataError("output directory appeared during conversion")
        os.rename(str(staging), str(output))
    return manifest, validation


def generate_verification(output):
    """A synthetic sliding rigid block: never represented as an LS-DYNA solve."""
    output = Path(output)
    if output.exists():
        raise DataError("output directory already exists; choose a new directory")
    output.parent.mkdir(parents=True, exist_ok=True)
    theta, friction, gravity, duration, step = math.radians(30), 0.3, 9.81, 5.0, 0.1
    acceleration = gravity * (math.sin(theta) - friction * math.cos(theta))
    downhill, normal, side = (math.cos(theta), 0, -math.sin(theta)), (math.sin(theta), 0, math.cos(theta)), (0, 1, 0)
    # Eight material nodes form a 2 x 2 x 1 m rigid block, density 2500 kg/m^3.
    corners = [(-1, -1, -0.5), (1, -1, -0.5), (1, 1, -0.5), (-1, 1, -0.5),
               (-1, -1, 0.5), (1, -1, 0.5), (1, 1, 0.5), (-1, 1, 0.5)]
    faces = [(1, 3, 2), (1, 4, 3), (5, 6, 7), (5, 7, 8), (1, 2, 6), (1, 6, 5),
             (2, 3, 7), (2, 7, 6), (3, 4, 8), (3, 8, 7), (4, 1, 5), (4, 5, 8)]
    # Coordinates mimic a large survey origin to exercise double precision subtraction.
    origin = (500000.0, 6500000.0, 100.0)
    with temporary_directory(output.parent, ".analytic-input-") as temporary:
        source, topology = Path(temporary) / "analytic_nodes.csv", Path(temporary) / "analytic_triangles.csv"
        with source.open("w", newline="", encoding="utf-8") as stream:
            writer = csv.writer(stream)
            writer.writerow(COLUMNS)
            for index in range(round(duration / step) + 1):
                t = index * step
                s, speed = 0.5 * acceleration * t * t, acceleration * t
                for identity, (u, v, w) in enumerate(corners, 1):
                    pos = [origin[a] + (u + s) * downhill[a] + v * side[a] + (w + 0.5) * normal[a] for a in range(3)]
                    vel = [speed * downhill[a] for a in range(3)]
                    writer.writerow([format(t, ".17g"), identity] + [format(n, ".17g") for n in pos + vel] + [1250])
        with topology.open("w", newline="", encoding="utf-8") as stream:
            writer = csv.writer(stream)
            writer.writerow(("a", "b", "c"))
            writer.writerows(faces)
        model = {"kind": "rigid-block-on-infinite-incline", "gravityMPerS2": gravity,
                 "slopeAngleDegrees": 30, "kineticFrictionCoefficient": friction,
                 "accelerationMPerS2": acceleration, "durationSeconds": duration,
                 "samplingIntervalSeconds": step, "massKg": 10000,
                 "slopeOriginUnityM": [0, 10, 0], "downhillDirectionUnity": [math.cos(theta), -math.sin(theta), 0],
                 "equations": "a=g*(sin(theta)-mu*cos(theta)); s=0.5*a*t^2; v=a*t",
                 "assumptions": "Initially at rest at incipient sliding; constant Coulomb kinetic friction, "
                 "rigid block, no pore pressure, fragmentation, entrainment or impact. Infinite planar slope.",
                 "expectedMechanicalEnergyLossJ": 10000 * friction * gravity * math.cos(theta)
                 * 0.5 * acceleration * duration * duration,
                 "notASolverResult": True}
        result = convert(source, output, source_description="ANALYTIC VERIFICATION ONLY: closed-form Coulomb sliding block, "
                         "30 degree incline, mu=0.3, g=9.81 m/s^2. Generated by convert_lsdyna.py; NOT an LS-DYNA result "
                         "and NOT a Hunnedalen landslide prediction.", length_to_m=1, time_to_s=1, mass_to_kg=1,
                         source_origin=origin, unity_offset=(0, 10, 0), triangles_csv=topology,
                         provenance_kind="analytic-verification", extra_metadata=model)
        # Retain the exact hash-addressed synthetic inputs for reproducibility.
        (output / source.name).write_bytes(source.read_bytes())
        (output / topology.name).write_bytes(topology.read_bytes())
        return result


def parser():
    command = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    command.add_argument("--input", type=Path, help="normalized nodal CSV")
    command.add_argument("--output", type=Path, required=True, help="new output directory (must not exist)")
    command.add_argument("--source-description", help="solver job/version/export provenance; required for conversion")
    command.add_argument("--length-to-m", type=float)
    command.add_argument("--time-to-s", type=float)
    command.add_argument("--mass-to-kg", type=float)
    command.add_argument("--source-origin", nargs=3, type=float, help="required source-coordinate origin; explicitly use 0 0 0 for an already local model")
    command.add_argument("--axis-mapping", default="x,z,y", help="signed source axes for Unity x,y,z; use --axis-mapping=-x,z,y")
    command.add_argument("--unity-offset", nargs=3, type=float, default=(0, 0, 0), help="Unity-axis translation in metres")
    command.add_argument("--triangles", type=Path)
    command.add_argument("--energy", type=Path, help="normalized solver energy CSV (optional)")
    command.add_argument("--site-case-report", type=Path, help="optional prepared rainfall-site report for provenance; never a physics pass")
    command.add_argument("--gravity", nargs=3, type=float, default=(0, -9.81, 0), help="Unity-axis gravity m/s^2 for diagnostics")
    command.add_argument("--residual-warning-m", type=float, default=0.05)
    command.add_argument("--generate-verification", action="store_true", help="generate a clearly labelled analytic incline fixture")
    return command


def main(argv=None):
    command = parser()
    args = command.parse_args(argv)
    try:
        if args.generate_verification:
            if args.input or args.triangles or args.energy or args.site_case_report or args.source_description or any(
                    v is not None for v in (args.length_to_m, args.time_to_s, args.mass_to_kg)):
                raise DataError("verification generation cannot be combined with solver input arguments")
            manifest, validation = generate_verification(args.output)
        else:
            if args.input is None or args.source_description is None or any(
                    v is None for v in (args.length_to_m, args.time_to_s, args.mass_to_kg)) or args.source_origin is None:
                raise DataError("conversion requires --input, --source-description, --source-origin and all three unit conversion factors")
            manifest, validation = convert(args.input, args.output, source_description=args.source_description,
                length_to_m=args.length_to_m, time_to_s=args.time_to_s, mass_to_kg=args.mass_to_kg,
                source_origin=args.source_origin, axis_mapping=args.axis_mapping, unity_offset=args.unity_offset,
                triangles_csv=args.triangles, energy_csv=args.energy, gravity=args.gravity,
                residual_warning_m=args.residual_warning_m, site_case_report=args.site_case_report)
        print("{}: {}".format(manifest["solver"], manifest["validationSummary"]))
        for warning in validation["warnings"]:
            print("WARNING: " + warning)
        print("Wrote " + str(args.output.resolve()))
        return 0
    except (DataError, OSError, OverflowError) as exc:
        print("ERROR: " + str(exc), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
