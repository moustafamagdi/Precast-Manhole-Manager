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

## Review domains (schema v2)

`ReviewState` owns domain-local transitions and aggregate readiness. Geometry/Openings checks cannot resolve Dimensions, Views, Layout, Presentation or Legacy records. Dimension generation resolves only its own domain after a complete pass; a partial wall pass cannot certify all dimensions. Presentation includes section marks, viewport types and review/production 3D errors. Presence of five placed views checks documentation existence only, not current extents or drawing quality. Delivery remains **not verified** until dedicated current-model validators are implemented.

The v2 register stores one result per foundation UniqueId/domain, plus geometry evidence and manual acceptance user/reason. Ignore/Restore opens domain selection; manual acceptance is IGNORED, never RESOLVED. Changed reasons, severity, affected walls or captured evidence reopen acceptance. Evidence is refreshed on a check/action; there is no background change listener or incremental rebuild in this phase. New combined legacy-command failures may remain Legacy when their domain is ambiguous.

V1 records can contain mixed reasons: load them as Legacy without changing their original status. First v2 save keeps an exact `.v1.bak` beside the register. Legacy issues require explicit review/acceptance; Clean Scan must not silently close them. Malformed or unsupported registers fail before replacement instead of silently dropping rows. Do not run older binaries against v2 registers; they do not understand the new columns. The store remains local and path-based; moving the RVT or sharing across machines is not addressed by this phase.

`WorkflowPreflightService` checks the selected workflow's document, asset and registry prerequisites before model edits. Opening-only runs do not require sheets or drawing assets. Unloaded links are logged as outside scan coverage, not assumed required. Batch completion exports `Readiness.csv`, separate from per-operation results. An empty review queue does not certify delivery. Registry updates remain outside Revit transactions: Undo and external model edits require rechecking.

Additional checks:

- `powershell -NoProfile -ExecutionPolicy Bypass -File tests/ReviewStateRegression.ps1`
- `powershell -NoProfile -Sta -ExecutionPolicy Bypass -File tests/WorkflowUiSmoke.ps1`

Revit acceptance exercise: on a test model, retain an opening and a dimension issue on the same manhole; fix/recheck openings and confirm the dimension issue survives. Accept only the dimension domain with a reason, change the relevant geometry, rerun dimensions and confirm an observed failure reopens it. Complete dimensions and confirm unrelated layout/legacy issues survive. Verify missing global assets fail before any model transaction. Do not interpret those code-level tests as live Revit validation.
