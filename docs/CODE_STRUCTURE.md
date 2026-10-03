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

## Drawing quality gate (exterior W1-W4)

The user-approved convention is **four exterior wall elevations**, not new interior cutting planes. This phase does not change section geometry, templates, manual annotations, sheet positions or model geometry. Existing MH_PLAN / MH_SEC (including project-specific derivatives), 1:25, HTC_DIM_1.8mm and NO BUBBLE NTS remain the drawing conventions. Six reserved rows remain the layout contract.

In Drawings, select `Check drawings - Selected` or `Check drawings - All`, then `Run Drawing Task`. Full Automation runs this check as phase 6 after saving the editing phases. Stop is available between manholes. The audit creates a `.drawing-checks.csv` beside the log and updates only the **DrawingValidation** review domain. Passing this audit does not close generation, legacy, geometry or opening reviews, and does not certify delivery. A failed/unavailable reference or bounds check is reported as unverified rather than deleting/recreating dimensions.

Checks cover required views and placements, scale/template presence, current wall alignment and crop/depth, host visibility candidates, section markers, six-row/titleblock containment, viewport/title overlap, viewport type/detail number, tool dimension counts/roles, references, measured segment values, value overrides, visibility candidates and annotation crop. A model-space wall/base bounding envelope is deliberately conservative: unusual geometry may require manual acceptance. Revit's visible-element collector is not pixel-level rendering proof; visual acceptance remains necessary. Manual dimensions are preserved and require separate review. Layout rectangles include view titles, but not arbitrary sheet notes, schedules or titleblock graphics. Titleblock bounding geometry is the current paper proxy; a nonstandard family or multiple titleblocks returns an unverified result. Custom/split crops are not certified.

Run `tests/DrawingAuditRegression.ps1` in addition to the existing regression and UI checks. These exercise finite bounds, six-row containment, neighboring overlap, title overflow, moved/depth-clipped/rotated local body bounds and invalid data. They do not execute native Revit cuts or transactions.

### Revit acceptance fixtures

`ValidateDrawingFixturesCommand` is a read-only Add-in Manager entry point. It opens a reviewed XML manifest, checks the exact model title and foundation UniqueIds, and compares actual audit issue codes with the complete expected set. Optional expected view count, managed opening count and base pin state catch additional regressions. It writes `.fixture-results.csv`, without changing the RVT or review register. A missing fixture is a failure, never an automatic pass. Fixture expectations must be inspected and approved, not generated from the same run being tested.

Manifest format (replace placeholders using a controlled test model):

```xml
<drawing-fixtures model-title="EXACT TEST MODEL TITLE">
  <case name="approved-exterior-baseline"
        foundation-unique-id="FOUNDATION UNIQUEID"
        expected-codes="" expected-views="5"
        expected-managed-openings="4" expected-base-pinned="true" />
</drawing-fixtures>
```

Use snapshots of a test RVT, with identical links and assets, and preserve one approved PDF/export for comparison. For every case below, run the relevant selected operation, verify the geometry visually, save the test snapshot, then run the fixture command twice. Compare both the expected issue codes and the exported drawings. No fixture RVT or reference PDF has been fabricated, and this release's live Revit acceptance is still pending.

| Fixture | Operation and acceptance |
|---|---|
| Normal rectangular body, four services | Complete selected; five correctly framed views, full body/base/opening dimensions, no overflow or duplicate cuts on repeat. |
| Rotated body and 45-degree service | Run openings, dimensions, then drawing checks; widths and clearances match physical geometry, faces remain referenced. |
| Shared corner / joined wall end (MH317 pattern) | Run openings; inspect each affected wall and full-width shifted cut, retain site-adjustment review. Source MEP stays unchanged. |
| Two overlapping services, merge ON/OFF | Verify intended combined/individual native cuts and width/height coverage. No duplicate openings on rerun. |
| One rejected wall, three valid walls | Confirm valid committed cuts survive, failed wall remains reviewed and its prior good geometry is preserved. |
| Copied/renumbered adjacent manholes | Unique identity, separate views/reservations, no foreign section marks. |
| Moved body with old views | Audit reports framing/orientation/visibility problems; it must not silently move views or claim they are current. |
| Pinned base and walls / failed base repair | Compare pin states and geometry before/after. A failed repair restores the entire repair group. |
| Hidden workset/filter or wrong crop depth | Audit reports hidden/unverified body rather than accepting a blank section. |
| Deleted or overridden dimension / closed view | Detect missing role/reference/value; inability to obtain visible bounds remains unverified. Existing dimensions are not deleted by audit. |
| Bottom-row overflow / overlapping viewport title | Detect overflow and intersection using title-inclusive bounds; preserve reserved sequence. |
| Missing view, missing titleblock, custom crop | Report incomplete/unverified checks, keep processing the other manholes. |
| Cancellation and rerun | Retain prior completed work, identify unchecked items, and produce no model changes during either audit. |
