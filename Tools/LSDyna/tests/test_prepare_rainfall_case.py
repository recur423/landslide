"""Small synthetic fixtures test data plumbing only, never a site prediction."""

import csv
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import prepare_rainfall_case as tool


class RainfallCaseTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tool.temporary_directory(Path(__file__).resolve().parents[1], ".rain-test-")
        self.root = self.temporary.__enter__()
        self.addCleanup(self.temporary.__exit__, None, None, None)
        self.case_path = self.root / "case.json"
        self.output = self.root / "output"
        self.case = {"schemaVersion": 1, "caseId": "SYNTHETIC-TEST-NOT-A-SITE",
                     "horizontalCrs": "synthetic projected metric grid", "verticalDatum": "synthetic local",
                     "lengthUnitToM": 1, "localOriginSourceUnits": [1000, 2000, 0],
                     "eventStartSeconds": 0, "surfaceGridCsv": "surface.csv",
                     "layers": [{"name": "synthetic-layer", "partId": 11, "bottomGridCsv": "bottom.csv"}],
                     "rainfallCsv": "rain.csv", "evidence": {}}
        self.write_grid("surface.csv", 10)
        self.write_grid("bottom.csv", 8)
        self.write_csv("rain.csv", tool.RAIN_COLUMNS,
                       [("one", -10, 0, 1e-6), ("one", 0, 10, 2e-6)])

    def write_csv(self, name, columns, data):
        with (self.root / name).open("w", encoding="utf-8", newline="") as stream:
            writer = csv.writer(stream)
            writer.writerow(columns)
            writer.writerows(data)

    def write_grid(self, name, z):
        self.write_csv(name, tool.GRID_COLUMNS,
                       [(i, j, 1000 + i, 2000 + j, z - i) for j in range(2) for i in range(2)])

    def run_case(self):
        self.case_path.write_text(json.dumps(self.case), encoding="utf-8")
        return tool.prepare(self.case_path, self.output)

    def test_geometry_rainfall_budget_and_saturated_screening(self):
        self.write_csv("budget.csv", tool.BUDGET_COLUMNS,
                       [(0, 10, 2e-5, 0, 1e-5, 0, 0, 1e-5, 5e-6)])
        self.write_csv("slip.csv", tool.SLIP_COLUMNS,
                       [(0, "a", 100, 50, 0, 0, 45, 1),
                        (10, "a", 100, 50, 60, 0, 45, 1)])
        self.case["hydroBudgetCsv"] = "budget.csv"
        self.case["slipPatchesCsv"] = "slip.csv"
        report = self.run_case()
        self.assertEqual(report["nodeCount"], 8)
        self.assertEqual(report["solidElementCount"], 1)
        self.assertEqual(report["layersTopToBottom"][0]["minimumThicknessM"], 2)
        self.assertAlmostEqual(report["rainfall"]["one"]["antecedentDepthM"], 1e-5)
        self.assertAlmostEqual(report["rainfall"]["one"]["totalRainfallVolumeM3"], 3e-5)
        self.assertAlmostEqual(report["hydroBudgetDiagnostics"][0]["wholeDomainWaterBalanceResidualM3"], 0)
        self.assertAlmostEqual(report["hydroBudgetDiagnostics"][0]["reportedPrecipitationDifferenceM3"], 0)
        self.assertEqual(report["slipPatchDiagnostics"]["firstFSOneCrossingBracketSeconds"], [0, 10])
        self.assertEqual(report["status"], "prepared-not-solved")
        mesh = (self.output / "mesh_only.k").read_text(encoding="ascii")
        self.assertIn("1,11,5,6,8,7,1,2,4,3", mesh)
        self.assertIn("NOT A RUNNABLE", mesh)
        self.assertEqual(json.loads((self.output / "case_report.json").read_text(encoding="utf-8")), report)

    def test_negative_layer_thickness_rejected(self):
        self.write_grid("bottom.csv", 11)
        with self.assertRaisesRegex(tool.DataError, "thickness"):
            self.run_case()

    def test_rain_gap_rejected(self):
        self.write_csv("rain.csv", tool.RAIN_COLUMNS,
                       [("one", -10, -1, 1e-6), ("one", 0, 10, 2e-6)])
        with self.assertRaisesRegex(tool.DataError, "gap or overlap"):
            self.run_case()

    def test_multiple_rain_zones_need_complete_cell_assignment(self):
        self.write_csv("rain.csv", tool.RAIN_COLUMNS,
                       [("one", -10, 10, 1e-6), ("two", -10, 10, 2e-6)])
        with self.assertRaisesRegex(tool.DataError, "cellZonesCsv"):
            self.run_case()

    def test_two_rainfall_zones_and_toe_feature_follow_grid_edges(self):
        self.write_csv("rain.csv", tool.RAIN_COLUMNS,
                       [("upslope", -10, 10, 1e-6), ("toe", -10, 10, 2e-6)])
        self.write_csv("zones.csv", tool.ZONE_COLUMNS, [(0, 0, "toe")])
        self.write_csv("features.csv", tool.FEATURE_COLUMNS,
                       [("toe-line", "toe", 0, 0, 0), ("toe-line", "toe", 1, 1, 0)])
        self.case["cellZonesCsv"] = "zones.csv"
        self.case["terrainFeaturesCsv"] = "features.csv"
        # Every named rainfall zone must have actual contributing area.
        with self.assertRaisesRegex(tool.DataError, "at least one cell"):
            self.run_case()
        self.write_csv("rain.csv", tool.RAIN_COLUMNS, [("toe", -10, 10, 2e-6)])
        report = self.run_case()
        self.assertEqual(report["terrainFeatureCount"], 1)
        self.assertFalse(report["terrainFeaturesMissing"])
        self.assertIn("toe-line,toe,0,1", (self.output / "terrain_feature_vertices.csv").read_text())
        with (self.output / "surface_cells.csv").open(newline="", encoding="utf-8") as stream:
            cell = next(csv.DictReader(stream))
        self.assertAlmostEqual(float(cell["slope_deg"]), 45)
        self.assertAlmostEqual(float(cell["downhill_x"]), 1)
        self.assertEqual(cell["rain_zone_id"], "toe")

    def test_feature_jump_is_rejected(self):
        self.write_csv("features.csv", tool.FEATURE_COLUMNS,
                       [("gully", "gully", 0, 0, 0), ("gully", "gully", 1, 1, 1)])
        self.case["terrainFeaturesCsv"] = "features.csv"
        with self.assertRaisesRegex(tool.DataError, "grid edges"):
            self.run_case()

    def test_budget_residual_is_reported_and_not_hidden(self):
        self.write_csv("budget.csv", tool.BUDGET_COLUMNS,
                       [(0, 10, 2e-5, 0, 0, 0, 0, 0, 2e-5)])
        self.case["hydroBudgetCsv"] = "budget.csv"
        report = self.run_case()
        self.assertAlmostEqual(report["hydroBudgetDiagnostics"][0]["wholeDomainWaterBalanceResidualM3"], 2e-5)

    def test_unsaturated_negative_pore_pressure_is_not_silently_used_as_saturated_strength(self):
        self.write_csv("slip.csv", tool.SLIP_COLUMNS,
                       [(0, "a", 100, 50, -10, 0, 45, 1)])
        self.case["slipPatchesCsv"] = "slip.csv"
        with self.assertRaisesRegex(tool.DataError, "saturated"):
            self.run_case()


if __name__ == "__main__":
    unittest.main()
