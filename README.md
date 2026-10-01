# Precast Manhole Manager

Revit 2024 add-in for linked Pipe/Duct penetrations, managed openings, dimensions and manhole sheets. Build with Visual Studio 2022 / .NET Framework 4.8. Open **Hatco → Precast Tools → Precast Manholes**.

## Current production workflow

1. Scan and number manholes. Use Clean Scan to reassess all review items after model corrections.
2. Set clearance per side. Existing managed openings are matched by source identity and updated on reruns.
3. Generate a selected manhole or use **Generate / Update All** for unattended processing.

Batch sheets contain **six manholes, one per row**: Plan, W1, W2, W3, W4. Failed manholes retain reserved positions for later repair. The run saves the currently open RVT in place with periodic checkpoints; only reports are written to the Batch log folder. Actual wall crossings drive production; virtual candidates remain diagnostic. Conduits and cable trays are excluded.

Views and titles that exceed their reserved row are placed at the assigned row and reported as **LAYOUT REVIEW** for manual adjustment; overflow can overlap adjacent rows. These size warnings do not roll back openings. Missing views, invalid sheet ownership and failed Revit transactions remain blocking errors. The batch command exits after its results dialog instead of reopening the main tool window.

Batch execution first prepares and commits plan/section views on sheets for every identifiable manhole, then attempts openings in a separate pass. Opening failures retain those body views for manual completion, including manholes with edited profiles or no actual crossings. An unidentifiable four-wall body retains its labeled reserved row for manual view creation. The report distinguishes `VIEWS READY` / `VIEWS REVIEW` from subsequent opening results. Reruns reuse tool view names and stored sheet slots from the input RVT; they save that same RVT in place. Save the model to its intended path before starting; unsaved, detached and cloud documents are not supported by this local batch command. Workshared files use Save, without automatic Synchronize with Central.

Required project resources: `Manhole Sec` section type, `MH_PLAN`, `MH_SEC`, `MH_3D` view templates, and linear dimension type `HTC_DIM_1.8mm`. Dimensions cover openings, internal/external body sizes and foundations. Individual sheets use W1–W4 detail numbers; shared sheets require unique manhole-prefixed detail numbers.

## Build and verification

```powershell
dotnet build src/PrecastManholeManager/PrecastManholeManager.csproj -c Release -p:DeployRevitAddin=false
powershell -NoProfile -ExecutionPolicy Bypass -File tests/ClearanceRegression.ps1
```

Revit 2024 API assemblies are referenced from the local Autodesk installation. Normal builds automatically copy the add-in to the user's Revit add-in folder; `DeployRevitAddin=false` skips that copy for verification.

Pure regression checks do not replace Revit integration testing of geometry, dimension references, rollback, saves and sheet layout.

## Documentation

- [Code structure and compatibility boundaries](docs/CODE_STRUCTURE.md)
- [Runner behavior and implementation history](docs/SIMPLE_PROJECT_RUNNER.md)
- [Legacy workflows and earlier milestones](docs/LEGACY_WORKFLOWS.md) — historical context, not the current production specification.
