# Precast Manhole Manager

Revit 2024 add-in for linked Pipe/Duct penetrations, managed openings, dimensions and manhole sheets. Build with Visual Studio 2022 / .NET Framework 4.8. Open **Hatco → Precast Tools → Precast Manholes**.

## Current production workflow

1. **Check & Review → Run Check**: choose all manholes (Clean Scan), review cases only, or the selected row. Inspect individual cases or create review 3Ds here.
2. **Openings & Repair → Run Openings**: choose the scope (defaults to picking bases), then create/update cuts or retry wholly missing openings only. Missing-only mode respects the chosen scope and disables wall-profile reset. Base repair is in the same tab.
3. **Drawings → Run Drawing Task**: select **Sheets + dimensions - All** for unattended preparation followed by dimensions, or run sheet preparation, dimensions, or plan-marker/viewport cleanup separately. Reviews do not exclude manholes from sheet preparation. Existing view extents are not automatically refreshed when a manhole moves.
4. **Setup & Advanced**: numbering and Excel export. Older geometry inventory, views-only generation, test sheets and the combined sheets/openings workflow are retained under a collapsed legacy/diagnostics section.

Search, review filter, selected manhole and active tab are retained while reopening the tool in the same Revit session. Clearance is per side. The combined sheets/openings workflow below is optional; it is not the default drawing task.

Batch sheets contain **six manholes, one per row**: Plan, W1, W2, W3, W4. Failed manholes retain reserved positions for later repair. The run saves the currently open RVT in place with periodic checkpoints; only reports are written to the Batch log folder. Actual wall crossings drive production; virtual candidates remain diagnostic. Conduits and cable trays are excluded.

Views and titles that exceed their reserved row are placed at the assigned row and reported as **LAYOUT REVIEW** for manual adjustment; overflow can overlap adjacent rows. These size warnings do not roll back openings. Missing views, invalid sheet ownership and failed Revit transactions remain blocking errors. The batch command exits after its results dialog instead of reopening the main tool window.

Batch execution first prepares and commits plan/section views on sheets for every identifiable manhole, then attempts openings in a separate pass. Opening failures retain those body views for manual completion, including manholes with edited profiles or no actual crossings. An unidentifiable four-wall body retains its labeled reserved row for manual view creation. The report distinguishes `VIEWS READY` / `VIEWS REVIEW` from subsequent opening results. Reruns reuse tool view names and stored sheet slots from the input RVT; they save that same RVT in place. Save the model to its intended path before starting; unsaved, detached and cloud documents are not supported by this local batch command. Workshared files use Save, without automatic Synchronize with Central.

Required project resources: `Manhole Sec` section type, `MH_PLAN`, `MH_SEC`, `MH_3D` view templates, linear dimension type `HTC_DIM_1.8mm`, and viewport type `NO BUBBLE NTS`. Dimensions cover openings, internal/external body sizes and foundations. Individual sheets use W1–W4 detail numbers; shared sheets require unique manhole-prefixed detail numbers.

## Complete one manhole

In **Drawings**, choose **Complete one: Selected row** or **Pick one base in Revit**, then click **Complete Manhole**. This runs sheet/view preparation, opening creation/update, recheck, dimensions and presentation for that manhole only. The merge checkbox next to Full Automation also applies here. Existing views and reserved sheet positions are reused; missing views and slots are created. Numbering checks the project for uniqueness but writes only the chosen foundation. Pick accepts one recognized host-model base; Esc cancels. The current saved local RVT is checkpointed as in Full Automation. Geometry failures are reported while independent documentation work continues. Existing moved-view extents are not automatically refreshed.

## Full automation and acknowledged reviews

**Drawings > Full Automation - All** runs five phases: prepare/reuse sheets and views; create/update openings for every manhole (even without prepared views); recheck current geometry; dimension prepared manholes; clean plan section markers and apply NO BUBBLE NTS. Opening dimensions are deferred to the dedicated dimension phase. One initial confirmation covers the run; no prompts are inserted between phases. The current RVT is saved at phase boundaries and every 10 changed items or 5 minutes between items. Stop takes effect between items; a single Revit API call cannot be interrupted safely. Per-manhole errors are recorded and later manholes continue; a save failure or global setup failure stops the run. RunReport.csv and RunSummary.txt are written beneath the batch log folder. This does not automatically repair bases, reset profiles, relocate manholes, refresh moved view extents or certify sheet appearance.

**Check & Review > Ignore / Restore Review** toggles the selected OPEN review to IGNORED or restores an IGNORED review to OPEN. Ignored rows remain visible with their reason when the review-only filter is off. Rechecking identical reasons preserves the acknowledgment; a different reason reopens the review. A clean geometric check resolves it. Ignore does not bypass opening-fit validation or authorize model edits. Acknowledgments use the existing local per-RVT-path review register, not shared RVT storage.

## Build and verification

On reruns, valid existing sheet reservations are checked without regenerating, rewriting storage or resetting note text. Only new reservations and missing notes are written. Duplicate indices, inconsistent page mappings and notes belonging to another sheet still block reservation. The log reports retained/created/repaired counts and separate reservation, commit and save timings.

Optional **Timing diagnostic: 3 new manholes only (slower)** is unchecked by default. It prepares up to three not-yet-complete manhole rows, saves the current RVT, and skips the opening stage. Existing complete rows are reused and do not consume the three-attempt limit. Sheet/slot reservation still covers the full project. The diagnostic adds measured PreRegen/PostRegen around instrumented calls inside transactions and writes a `.performance.csv` beside the TXT log. It is a model-changing diagnostic, not a read-only benchmark. Explicit regeneration, transaction commit and save calls are timed without additional regeneration. Diagnostic timings must not be compared directly to production totals; controlled before/after comparisons require identical starting models.

`PERF_CALL` records time individual creation, template, viewport setup, layout regeneration, documentation commit and save calls. `Returned=True` means the API returned normally, not that a returned transaction status was Committed. Stage-level `PERF` totals contain these call timings and must not be added to them. Use several newly created manholes for comparison, excluding the reuse path.

Batch layout applies positions for all five viewports, regenerates once, applies all label offsets, then regenerates for final validation. Existing complete five-view rows are reused during documentation preparation. The opening pass reuses prepared views and only performs its final layout after dimensions. `PERF` log entries separate documentation, viewport creation/setup and row arrangement; runtime speed must be measured in Revit.

```powershell
dotnet build src/PrecastManholeManager/PrecastManholeManager.csproj -c Release -p:DeployRevitAddin=false
powershell -NoProfile -ExecutionPolicy Bypass -File tests/ClearanceRegression.ps1
powershell -NoProfile -Sta -ExecutionPolicy Bypass -File tests/WorkflowUiSmoke.ps1
```

Revit 2024 API assemblies are referenced from the local Autodesk installation. Normal builds automatically copy the add-in to the user's Revit add-in folder; `DeployRevitAddin=false` skips that copy for verification.

Pure regression checks do not replace Revit integration testing of geometry, dimension references, rollback, saves and sheet layout.

## Documentation

- [Code structure and compatibility boundaries](docs/CODE_STRUCTURE.md)
- [Runner behavior and implementation history](docs/SIMPLE_PROJECT_RUNNER.md)
- [Legacy workflows and earlier milestones](docs/LEGACY_WORKFLOWS.md) — historical context, not the current production specification.

Pipe/duct openings near wall ends retain their full projected size and clearance and shift along the wall into its usable span. Perpendicular native wall-end joins reserve the neighboring wall half-thickness (plus a 1 mm separation), avoiding cuts through the join. Source services are not moved. The review report records the signed site-adjustment distance along the host wall axis. Openings too large for the usable span stay in review rather than being clipped.
