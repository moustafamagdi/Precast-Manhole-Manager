# Code structure

## Supported workflow

`App` exposes one ribbon entry point, `ProjectRunnerCommand`. Its partial files separate responsibilities:

- `ProjectRunnerCommand.cs`: UI dispatch, selection and diagnostic actions.
- `ProjectRunnerProduction.cs`: production preflight, opening update, views and dimensions for one manhole.
- `ProjectRunnerBatch.cs`: unattended execution, reserved rows, checkpoints and reports.

Production returns a typed `ProductionManholeResult`; batch decisions must use its properties, never search the report text.

## Shared services

- `ProductionPreflightService`: physical blockers shared by Clean Scan and production. Production additionally validates the requested output and sheet constraints.
- `LinkedMepScanCache`: command-scoped linked Pipe/Duct geometry cache shared by actual and virtual scanners. Do not retain host geometry across edits.
- `CleanSyncPlanService` / `CleanSyncAtomicService`: audit and transactional application of managed openings.
- `TransactionFailureHandling`: transaction failure setup used by selected and batch production.
- `BatchSheetLayoutService`: six fixed rows per sheet, persistent slot identity, placement and boundary validation.
- `OpeningDimensionService` / `OpeningDimensionService.Body.cs`: partial implementation for opening and body/base dimensions.
- `ManholeReviewRegistry`: review persistence and resolution; this external registry is separate from Revit transactions.

## Compatibility boundaries

Public command classes are callable through Add-in Manager even without C# callers. Diagnostic and earlier sheet commands remain available for that reason; they are not the unattended production entry point. Do not delete them based solely on reference counts.

Preserve Extensible Storage GUIDs, source keys, view names and slot indices during refactoring: saved RVTs depend on them. Previous three-row batch storage is deliberately rejected; six-row runs start from the original pre-batch model.

## Validation

Run `dotnet build src/PrecastManholeManager/PrecastManholeManager.csproj -c Release -p:DeployRevitAddin=false`, then `powershell -NoProfile -ExecutionPolicy Bypass -File tests/ClearanceRegression.ps1`.

Normal Visual Studio builds retain automatic deployment. Disable it with `DeployRevitAddin=false` for checks while Revit is open. Regression checks exercise pure policies; model geometry, rollback, dimension references and viewport layout still need integration testing in Revit.
