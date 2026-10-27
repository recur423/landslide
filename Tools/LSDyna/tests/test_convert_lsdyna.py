import csv
import hashlib
import json
import math
from pathlib import Path
import struct
import subprocess
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import convert_lsdyna as converter


class ConversionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = converter.temporary_directory(Path(__file__).resolve().parents[1], ".test-")
        self.root = self.temporary.__enter__()
        self.addCleanup(self.temporary.__exit__, None, None, None)
        self.source = self.root / "nodes.csv"
        self.output = self.root / "result"

    def write_nodes(self, rows, columns=converter.COLUMNS):
        with self.source.open("w", newline="", encoding="utf-8") as stream:
            writer = csv.writer(stream)
            writer.writerow(columns)
            writer.writerows(rows)

    def convert(self, **kwargs):
        options = dict(source_description="Test LS-DYNA export: synthetic unit-test input, not a physical solve",
                       length_to_m=1, time_to_s=1, mass_to_kg=1)
        options.update(kwargs)
        return converter.convert(self.source, self.output, **options)

    def read_frame(self, number=0):
        content = (self.output / "frame_{:06d}.bytes".format(number)).read_bytes()
        self.assertEqual(content[:8], b"LSDYNA01")
        count = struct.unpack_from("<i", content, 8)[0]
        self.assertEqual(len(content), 12 + count * 24)
        return [struct.unpack_from("<6f", content, 12 + 24 * i) for i in range(count)]

    def test_mapping_units_origin_order_topology_and_provenance(self):
        self.write_nodes([
            (0, 3, 1001, 2002, 3003, 4, 5, 6, 7),
            (0, 1, 1000, 2000, 3000, 4, 5, 6, 7),
            (0, 2, 1002, 2003, 3004, 4, 5, 6, 7),
            (2, 2, 1010, 2013, 3016, 4, 5, 6, 7),
            (2, 3, 1009, 2012, 3015, 4, 5, 6, 7),
            (2, 1, 1008, 2010, 3012, 4, 5, 6, 7)])
        triangles = self.root / "triangles.csv"
        triangles.write_text("a,b,c\n1,2,3\n", encoding="utf-8")
        manifest, validation = self.convert(length_to_m=0.001, time_to_s=0.001, mass_to_kg=1000,
            source_origin=(1000, 2000, 3000), unity_offset=(10, 20, 30), triangles_csv=triangles)
        self.assertEqual(manifest["nodeIds"], [1, 2, 3])
        self.assertEqual(manifest["triangles"], [0, 2, 1])
        self.assertEqual(manifest["massesKg"], [7000] * 3)
        self.assertEqual(manifest["timesSeconds"], [0, 0.002])
        self.assertEqual(self.read_frame()[0], (10, 20, 30, 4, 6, 5))
        self.assertEqual(manifest["sourceSha256"], hashlib.sha256(self.source.read_bytes()).hexdigest())
        self.assertEqual(manifest["solver"], "LS-DYNA")
        self.assertEqual(manifest["provenanceKind"], "solver-export")
        self.assertLess(validation["maxPositionVelocityResidualM"], 2e-6)
        self.assertEqual(json.loads((self.output / "dataset.json").read_text(encoding="utf-8")), manifest)
        self.assertEqual(manifest["frameSha256"][0], hashlib.sha256((self.output / "frame_000000.bytes").read_bytes()).hexdigest())

    def test_prepared_site_report_is_copied_but_never_certifies_physics(self):
        self.write_nodes([(0, 1, 0, 0, 0, 0, 0, 0, 1)])
        report_path = self.root / "case_report.json"
        report_path.write_text(json.dumps({"schemaVersion": 1, "status": "prepared-not-solved",
                                           "caseId": "SYNTHETIC-ONLY", "evidenceMissing": ["rainGauge"]}), encoding="utf-8")
        manifest, validation = self.convert(site_case_report=report_path)
        self.assertEqual(manifest["siteCaseReport"]["caseId"], "SYNTHETIC-ONLY")
        self.assertEqual(manifest["siteCaseReport"]["evidenceMissingCount"], 1)
        self.assertEqual(manifest["siteCaseReport"]["status"], "prepared-not-solved")
        self.assertEqual((self.output / "site_case_report.json").read_bytes(), report_path.read_bytes())
        self.assertEqual(validation["status"], "diagnostic-only")

    def test_large_coordinate_origin_is_subtracted_before_float32(self):
        self.write_nodes([(0, 1, 6500000.001, 1e9 + 0.125, -3, 0.001, 0, 0, 1),
                          (1, 1, 6500000.002, 1e9 + 0.125, -3, 0.001, 0, 0, 1)])
        self.convert(source_origin=(6500000, 1e9, -3))
        first, second = self.read_frame()[0], self.read_frame(1)[0]
        self.assertAlmostEqual(first[0], 0.001, places=8)
        self.assertEqual(first[2], 0.125)
        self.assertAlmostEqual(second[0] - first[0], 0.001, places=8)

    def test_small_step_at_large_time_keeps_double_precision(self):
        self.write_nodes([(1000000, 1, 0, 0, 0, 1, 0, 0, 1),
                          (1000000.0001, 1, 0.0001, 0, 0, 1, 0, 0, 1)])
        manifest, _ = self.convert()
        self.assertGreater(manifest["timesSeconds"][1], manifest["timesSeconds"][0])
        self.assertAlmostEqual(manifest["timesSeconds"][1] - manifest["timesSeconds"][0], 0.0001, places=9)

    def test_negative_time_and_single_frame_are_valid_but_not_time_validated(self):
        self.write_nodes([(-2, 1, 0, 0, 0, 0, 0, 0, 1)])
        manifest, validation = self.convert()
        self.assertEqual(manifest["timesSeconds"], [-2])
        self.assertEqual(validation["intervalDiagnostics"], [])
        self.assertIn("Only one frame", " ".join(validation["warnings"]))

    def test_signed_permutation_transforms_velocity_without_translation(self):
        self.write_nodes([(0, 1, 1, 2, 3, 4, 5, 6, 1)])
        manifest, _ = self.convert(axis_mapping="-x,z,y", unity_offset=(10, 20, 30))
        self.assertEqual(self.read_frame()[0], (9, 23, 32, -4, 6, 5))
        self.assertFalse(manifest["triangleWindingReversed"])

    def test_invalid_inputs_leave_no_partial_output(self):
        normal = (0, 1, 0, 0, 0, 0, 0, 0, 1)
        bad_cases = {
            "nan": [(0, 1, "NaN", 0, 0, 0, 0, 0, 1)],
            "infinite_velocity": [(0, 1, 0, 0, 0, "Infinity", 0, 0, 1)],
            "duplicate": [normal, normal],
            "time_reverse": [normal, (-1, 1, 0, 0, 0, 0, 0, 0, 1)],
            "time_revisit": [normal, (1, 1, 0, 0, 0, 0, 0, 0, 1), normal],
            "node_change": [normal, (1, 2, 0, 0, 0, 0, 0, 0, 1)],
            "mass_change": [normal, (1, 1, 0, 0, 0, 0, 0, 0, 2)],
            "zero_mass": [(0, 1, 0, 0, 0, 0, 0, 0, 0)],
            "negative_mass": [(0, 1, 0, 0, 0, 0, 0, 0, -1)],
            "bad_id": [(0, "1.5", 0, 0, 0, 0, 0, 0, 1)],
            "id_overflow": [(0, 2147483648, 0, 0, 0, 0, 0, 0, 1)],
            "float32_overflow": [(0, 1, 1e100, 0, 0, 0, 0, 0, 1)],
            "mass_underflow": [(0, 1, 0, 0, 0, 0, 0, 0, 1e-60)],
        }
        for name, rows in bad_cases.items():
            with self.subTest(name=name):
                self.write_nodes(rows)
                with self.assertRaises(converter.DataError):
                    self.convert()
                self.assertFalse(self.output.exists())
                self.assertFalse(list(self.root.glob(".lsdyna-convert-*")))

    def test_missing_or_invalid_required_metadata_is_rejected(self):
        self.write_nodes([(0, 1, 0, 0, 0, 0, 0, 0, 1)])
        for kwargs in ({"source_description": " "}, {"length_to_m": 0}, {"time_to_s": -1},
                       {"mass_to_kg": float("inf")}, {"source_origin": (0, 1, float("nan"))},
                       {"axis_mapping": "x,x,z"}, {"gravity": (0, float("nan"), 0)}):
            with self.subTest(kwargs=kwargs), self.assertRaises(converter.DataError):
                self.convert(**kwargs)
        self.assertFalse(self.output.exists())

    def test_duplicate_headers_and_empty_input_are_rejected(self):
        self.source.write_text("time,node_id,x,y,z,vx,vy,vz,mass,mass\n", encoding="utf-8")
        with self.assertRaises(converter.DataError):
            self.convert()
        self.write_nodes([])
        with self.assertRaises(converter.DataError):
            self.convert()

    def test_existing_output_is_never_overwritten(self):
        self.write_nodes([(0, 1, 0, 0, 0, 0, 0, 0, 1)])
        self.output.mkdir()
        protected = self.output / "original.txt"
        protected.write_text("keep", encoding="utf-8")
        with self.assertRaises(converter.DataError):
            self.convert()
        self.assertEqual(protected.read_text(encoding="utf-8"), "keep")

    def test_reader_resource_limits_reject_atomically(self):
        self.write_nodes([(t, i, i, 0, 0, 0, 0, 0, 1) for t in (0, 1) for i in (1, 2, 3)])
        topology = self.root / "triangles.csv"
        topology.write_text("a,b,c\n1,2,3\n", encoding="utf-8")
        for name, limit in (("MAX_NODES", 2), ("MAX_FRAMES", 1),
                            ("MAX_TRIANGLE_INDICES", 2), ("MAX_MANIFEST_BYTES", 100)):
            with self.subTest(limit=name), patch.object(converter, name, limit):
                with self.assertRaisesRegex(converter.DataError, "Unity reader limit"):
                    self.convert(triangles_csv=topology)
                self.assertFalse(self.output.exists())
                self.assertFalse(list(self.root.glob(".lsdyna-convert-*")))

    def test_topology_rejects_unknown_repeated_and_duplicate_nodes(self):
        self.write_nodes([(0, i, i, 0, 0, 0, 0, 0, 1) for i in range(1, 4)])
        topology = self.root / "triangles.csv"
        for text in ("1,2,4\n", "1,1,2\n", "1,2,3\n3,2,1\n"):
            topology.write_text("a,b,c\n" + text, encoding="utf-8")
            with self.assertRaises(converter.DataError):
                self.convert(triangles_csv=topology)
            self.assertFalse(self.output.exists())

    def test_inconsistent_velocity_is_reported_without_modifying_trajectory(self):
        self.write_nodes([(0, 1, 0, 0, 0, 0, 0, 0, 1), (1, 1, 20, 0, 0, 0, 0, 0, 1)])
        _, validation = self.convert()
        self.assertEqual(validation["maxPositionVelocityResidualM"], 20)
        self.assertIn("trapezoidal", " ".join(validation["warnings"]))
        self.assertEqual(self.read_frame(1)[0][0], 20)

    def test_energy_diagnostics_do_not_double_count_hourglass_or_claim_conservation(self):
        self.write_nodes([(0, 1, 0, 0, 0, 0, 0, 0, 1), (1, 1, 0, 0, 0, 0, 0, 0, 1)])
        energy = self.root / "energy.csv"
        energy.write_text(
            "time,kinetic_energy,internal_energy,external_work,hourglass_energy\n"
            "0,10,20,0,1\n1,8,24,2,2\n", encoding="utf-8")
        _, validation = self.convert(energy_csv=energy)
        diagnostic = validation["solverEnergyDiagnostics"]
        self.assertEqual(diagnostic["status"], "diagnostic-only")
        self.assertEqual(diagnostic["records"][1]["deltaKineticPlusInternalMinusExternalWorkJ"], 0)
        self.assertEqual(diagnostic["records"][1]["hourglassEnergyJ"], 2)
        self.assertIn("No energy conservation pass/fail", diagnostic["interpretation"])

    def test_analytic_fixture_obeys_acceleration_and_friction_work(self):
        manifest, validation = converter.generate_verification(self.output)
        self.assertEqual((manifest["solver"], manifest["provenanceKind"]), ("ANALYTIC", "analytic-verification"))
        self.assertTrue(manifest["verificationModel"]["notASolverResult"])
        self.assertEqual(len(manifest["timesSeconds"]), 51)
        self.assertEqual(len(manifest["triangles"]), 36)
        model = manifest["verificationModel"]
        acceleration = 9.81 * (0.5 - 0.3 * math.cos(math.radians(30)))
        self.assertAlmostEqual(model["accelerationMPerS2"], acceleration)
        first, last = validation["frameDiagnostics"][0], validation["frameDiagnostics"][-1]
        self.assertAlmostEqual(last["maxSpeedMPerS"], 5 * acceleration, places=5)
        self.assertLess(validation["maxPositionVelocityResidualM"], 5e-6)
        energy_loss = first["mechanicalEnergyJ"] - last["mechanicalEnergyJ"]
        self.assertAlmostEqual(energy_loss, model["expectedMechanicalEnergyLossJ"], delta=0.1)
        displacement = [last["centerOfMassM"][i] - first["centerOfMassM"][i] for i in range(3)]
        self.assertAlmostEqual(displacement[0], 0.5 * acceleration * 25 * math.cos(math.radians(30)), places=5)
        self.assertAlmostEqual(displacement[1], -0.5 * acceleration * 25 * 0.5, places=5)
        self.assertAlmostEqual(displacement[2], 0, places=7)
        self.assertEqual(hashlib.sha256((self.output / "analytic_nodes.csv").read_bytes()).hexdigest(), manifest["sourceSha256"])

    def test_cli_rejects_missing_unit_declaration(self):
        self.write_nodes([(0, 1, 0, 0, 0, 0, 0, 0, 1)])
        result = subprocess.run([sys.executable, str(Path(converter.__file__)), "--input", str(self.source),
                                 "--output", str(self.output), "--source-description", "test"],
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.assertEqual(result.returncode, 2)
        self.assertIn("all three unit conversion factors", result.stderr)
        self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()
