# World Of Wild Waters

The current project uses Unity `2022.3.62f3c1` (see `ProjectSettings/ProjectVersion.txt`).

## LS-DYNA landslide data workflow

The project now provides strict, timestamp-based playback of converted LS-DYNA nodal exports, unit and coordinate conversion, integrity checks, and an explicitly labelled analytical verification scene. Genuine LS-DYNA results have not yet been supplied. Existing RAMMS data and the authored late-stage animation are not LS-DYNA results.

A separate rainfall-site preparation tool accepts surveyed terrain and layer surfaces plus time-varying rainfall, builds a mesh-only LS-DYNA geometry fragment, and audits externally computed water budgets and saturated slip-patch screening. It does not solve infiltration or certify slope failure; the current project has no real site inputs for that workflow.

Open **Landslide → LS-DYNA → Open setup** in Unity. The default Hunnedalen mode requires a genuine solver-export dataset and reports missing data until one is imported. The original RAMMS demonstration remains available through an explicit mode selection.

- [接入操作与验证 / Getting started](docs/LSDyna.md)
- [数据格式 / Data format](docs/LSDynaDataFormat.md)
- [物理建模与验证 / Physics workflow](docs/LSDynaPhysicsWorkflow.md)
- [降雨场地准备 / Rainfall-site preparation](docs/RainfallCasePreparation.md)

## Documentation

-   [Creating a new scene](docs/NewScene.md)
-   [Scenes](docs/Scenes.md)
-   [Import](docs/Import.md)
-   [Scenario](docs/Scenario.md)
-   [GeNa](docs/GeNA.md)
-   [Weather](docs/Weather.md)
-   [Structures](docs/Structures.md)
-   [Water Interactables](docs/WaterInteractables.md)
