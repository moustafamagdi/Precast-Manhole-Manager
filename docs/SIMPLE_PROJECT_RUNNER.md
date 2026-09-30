# Simple one-time project runner (first integration pass)

Branch: `feature/simple-project-runner`, based on
`experiment/virtual-manhole-geometry`. **Do not merge with main yet.**

## Product decision

This add-in is a one-time project production utility, not a recurring
project management platform. Normal users see **one** ribbon button:
**Hatco > Precast Tools > Precast Manholes**. All older experimental
commands remain compiled in the DLL for technical debugging via
AddInManager, but are hidden from the normal ribbon.

One resizable modal screen is reused across actions. The dialog closes
while its requested action runs in Revit's valid API context, then
reopens. This avoids live modeless WPF threading/external event errors.

The three workflow steps are intentionally compact:

1. **Scan Project:** discover only the two approved precast foundation
   types, recover each virtual 4-wall footprint, inventory edited
   profiles, manually created wall openings and visible in-place
   cutters. Skip the expensive linked MEP scan at this step. Save
   problematic cases to the existing per-project review register and
   export a readable CSV.
2. **Review Selected:** for one row, run the existing full linked MEP
   actual/virtual scanner and unified opening review, with test
   defaults 50 mm clearance, 150 mm virtual gap and 15 degree
   horizontal approach. Export UnifiedOpeningReview CSV and surface
   a short status summary; record outstanding issues. No cutting.
   **Create / Open 3D** creates or updates an isometric Revit review
   view around that selected manhole's 4 validated walls (350 mm box
   margin), falling back to foundation bbox when geometry is ambiguous.
3. **Export Excel:** use the existing workbook exporter ONLY when saved
   manhole fabrication data already exists. This does **not** generate
   missing openings, 2D drafting views or Revit sheets yet. It
   explicitly informs the user when no saved manhole data is present.

The previous isolated issue register persists locally for each saved
RVT path. It remembers severe older failed-cleanup reasons, so a later
lightweight scan does not overwrite the known pinned in-place
cascade deletion risk. Review 3D views are project View3D elements:
SAVE the RVT to retain them. CSV review files appear in the normal
Desktop\Precast Manhole Manager output folder.

## Deliberately deferred

The following are **not** treated as completed just because they
exist as experimental code: destructive cleanup of pinned in-place
voids, auto-cutting virtual sloped/skewed ducts, full Batch All write,
the 4 wall elevations, Plan view and placed fabrication sheets. All
remain separated from the main workflow until one clean prototype
manhole completes the cycle safely.

## First acceptance test

1. Switch to this branch, clean/rebuild the Revit 2024 net48 project
   and **restart Revit** to remove any old ribbon buttons left behind
   by the previous add-in load.
2. Click the single **Precast Manholes** button.
3. **Scan Project**: verify problematic foundation `5144998` is
   marked REVIEW, with its 3 edited profiles and in-place cutter
   `5046777` mentioned. Confirm other manholes are listed without
   causing the scan to stop.
4. Select `5144998`, choose **Create / Open 3D**; verify the view's
   Section Box isolates the manhole. Reopen the screen; the view should
   be listed.
5. Select `5144998`, **Review Selected**: expect the 3 virtual
   duct proposals from the previous experiment and a CSV. No element
   is modified.
6. Identify an ordinary unmodified manhole that can serve as
   **our first complete fabrication prototype**.

This first pass consolidates the UI and proven read-only services.
Next implementation target: one healthy manhole, create/sync
accepted openings using explicit transaction and clearance rules,
then Plan + W1-W4 elevations + one fabrication sheet. Only after
that first prototype should we automate the batch.

## Scope and source-of-truth note

The issue register lives on the current workstation, not cloud
storage. It is tied to the saved model path and is never silently
deleted. The generated 3D views live inside the RVT. The exporter
reads saved carriers, not raw preview rows.


## Milestone 2: first draft sheet for ONE clean manhole

The same simple window now includes **3 Draft Sheet** alongside
Scan / Review / 3D / Export existing Excel. Select ONE foundation that
is not currently isolated and whose wall audit finds no unmanaged cuts.
Use a detached/saved test RVT copy.

Click Draft Sheet and explicitly confirm that this is a **DRAFT**
model-view prototype. In a single transaction the service creates or
reuses an orthographic top **PLAN** view and four wall-facing
orthographic **W1–W4** views at initial **1:50** scale, with narrow
section boxes around each wall and a plan box around the recovered
four-wall footprint. It creates one named `MH_<FoundationId>_DRAFT`
Revit sheet, preferring an available A0/A1 title block type. Viewports
are placed in five predefined slots and all are checked for sheet
boundary overflow and overlap. A failure causes a transaction rollback;
the text log explains whether the model geometry, title block or
viewport fit needs adjustment. Existing draft view names are reused and
a manually edited existing five-viewport sheet is never deleted.

**This is intentionally NOT the final fabrication sheet**: views are
orthographic 3D (not ViewPlan/ViewSection) with no automatic dimensions
or opening annotations. It does not modify walls, void cuts, ducts,
openings, stored carriers or numbering. It provides the first visible
layout to validate view orientations and scale before switching to
true annotated Plan and four Elevations.

For first acceptance testing:
1. Update branch and restart Revit.
2. Select a **clean** foundation in the single window, preferably a
   small uncomplicated manhole with clearly visible 4 walls.
3. Click **3 Draft Sheet**. If the project lacks a sufficiently large
   titleblock, the operation will roll back and say why.
4. Verify PLAN looks down, W1–W4 look square onto the expected walls,
   the section boxes don't contain neighboring manholes, and viewports
   do not overlap. Save the RVT to keep the draft sheet.
5. Share one screenshot of the sheet and its TXT log. The subsequent
   milestone will use that geometry to generate true dimensioned
   fabrication elevations and an opening schedule, after one
   controlled clean-manhole opening-sync test.


## Milestone 2 correction — actual 2D Draft Plan + Sections

The first orthographic-3D sheet attempt was rejected because Revit's
whole-project 3D camera crop produced physically huge paper viewport
bounds. The expected workflow does **not** require 3D views on
fabrication sheets.

Draft generation now uses real Revit **ViewPlan (Floor Plan)** for PLAN
and four native **ViewSection** views W1-W4. Each view is cropped to the
validated four-wall footprint or an individual wall. The floor plan
view-range includes the base and the wall heights relative to the
nearest Level. Section view transforms face each wall from outside
with a tight depth to avoid the opposite wall. The 3D Review command
remains separate and only supports troubleshooting.

New sheet/view prefix `MH_<FoundationId>_DRAFT_2D`, to avoid ever
reusing/overwriting old 3D trial views. One transaction generates
these five views and arranges them on an A1/A0 titleblock, trying
paper scales 1:25–1:200. The previous large 3D-crop workaround is no
longer part of the draft generation path.

**Acceptance:** On a detached saved RVT, choose the same healthy
foundation 4409885 (1200×1200 internal, 1600×1600 outer), hit
Draft Sheet, verify a PLAN looking down plus four wall Sections.
Send sheet screenshot and TXT log. If its plan cut elevation or
section facing direction is wrong, fix those orientation/view-range
details before any annotation work. These are still unannotated
draft model views and not issued manufacturing drawings.


## Latest simplification: operator lays out sheet manually

Automatic draft sheet creation and viewport placement have been REMOVED
from the current branch after repeated failures in the full live project.
The new button is **3 Create 2D Views**. It creates (or reuses) exactly
one real Floor Plan and four native wall Sections (W1-W4) for one
selected, non-isolated manhole. It does NOT create any sheet, titleblock,
viewport, sheet annotation or apply sheet-fit scale trials. One
transaction commits these five views, so a sheet-layout failure cannot
cause the views to disappear. Existing named Plan/Section views are
preserved; re-running does not reset their chosen scale or crop.

Views are named `MH_<FoundationId>_DRAFT_2D_PLAN` and
`MH_<FoundationId>_DRAFT_2D_W1` through `W4`. The Plan opens
after creation. In Revit Project Browser, the operator places these
five views on a preferred sheet, adjusts view scales, crops and
annotations, then shares a screenshot and chosen arrangement.
Future automated sheet creation can be based on that actual
project-specific template, not guesses.

The **Review 3D** action is unchanged and remains strictly for
problematic manholes. The old diagnostics and failed automatic layout
experiments remain in Git history but are no longer invoked by the
one-time project runner.


## Six-row sheet automation: first controlled batch

The user's manually arranged reference sheet established **six horizontal
manhole rows**, each containing **PLAN, W1, W2, W3, W4** left to
right. Required view templates: `MH_PLAN` and `MH_SEC`; scale:
**1:25**. The new **3 Test 6-Row Sheet** button is available in the
same one-window project runner.

Scope is deliberately **one new test sheet, up to six clean
manholes per click** until the first generated layout is visually
approved. It will not change the existing reference sheet. Launch the
tool while the user's reference sheet is the ACTIVE Revit view to reuse
that titleblock FAMILY TYPE. Otherwise the code selects the first
available A0/A1-preferred titleblock, reserving a fixed **165 mm**
right-hand column and using the remaining printable area as six equal
rows. The existing five 2D views are reused where safe and new ones
are generated via the tested service; manually placed views are skipped,
not removed or duplicated.

Each test row uses PLAN and W1–W4 at 1:25 and five equal-width
layout cells. Each row is independently transactional: if one
manhole's crop, template or viewport dimensions don't fit, just that
row and its newly generated views are rolled back. No invalid row is
forced into a smaller scale. A completely empty test sheet rolls back.
The source manually formatted sheet and already-placed views are never
edited. The sheet is named `HATCO_PRECAST_6MH_TEST_01` and a
repeat run will stop rather than overwrite it.

**First test:** Open the manually formatted sheet in Revit, then
launch Precast Manholes > Test 6-Row Sheet. Save a detached test RVT
before running. Send a screenshot of the resulting sheet and timestamped
TXT log. If rows are skipped, check their exact `6MH VIEW` paper sizes
and cell bounds in the log. Only after visual approval should we expand
beyond the first sheet or add numbering and dimension annotations.


## Seven-row layout revision (2026-09-30)

The user approved the first automated sheet, then requested **seven**
manholes per sheet and centered, non-overlapping view titles. The new
**Test 7-Row Sheet** action is wired to
`SevenRowManholeSheetService`; it creates a NEW
`HATCO_PRECAST_7MH_TEST_01` sheet and never edits the manually
arranged reference sheet or the earlier six-row test sheet.

Each 1:25 row is PLAN, W1, W2, W3, W4. The generator can consider up
to **12 unplaced candidates** to fill at most **7 fitting rows**.
Each row has a dedicated paper-space label band. All five drawing
boxes align along the TOP of the row; the label of each viewport is
measured with `GetLabelOutline`, shifted to the horizontal center
below its drawing with `LabelOffset` and checked against its own
view, neighboring views, cell edges and lower row boundary.
It prefers the user's existing **NO BUBBLE NTS** viewport type and
only supplies concise title-on-sheet text for newly made views.
Existing user-entered view titles remain unchanged.

The new Plan tries to activate and tighten its annotation crop to
approximately 4 mm around the model crop, reducing viewport bounds
caused by distant section heads. If `MH_PLAN` template locks the
annotation crop, it is NOT overridden; the log reports the restriction
and an oversized manhole is skipped at 1:25. This step may crop
out-of-bound section heads; the first test must visually verify them.

Every row is atomic: if a drawing or label fails seven-row fit, its
new views/viewports roll back independently; the remaining fitting
manholes continue. Do not shrink the agreed 1:25 scale to force a fit.
The output log records each viewport's footprint and skip reason.

Run on a detached/saved test RVT with the reference sheet active
to reuse its titleblock type. Send a screenshot of the new sheet and
its timestamped TXT log. This is still a *layout* acceptance test,
not a fully dimensioned manufacturing submission.


## Exterior-facing wall Sections

New wall Sections now look **FROM OUTSIDE THE MANHOLE TOWARD
THE EXTERNAL WALL FACE**. Autodesk `ViewSection.CreateSection`
uses `sectionBox.Transform.BasisZ` as the actual view direction.
For each W1–W4, the detector calculates the outward wall normal
from the validated manhole center. The new section looks along
`-outward`, with the section origin 40 mm outside the modeled
outer wall face. Its near/far depth includes just the wall thickness
plus 60 mm behind the inner face, preventing the opposite wall from
obscuring an elevation.

To preserve manually placed legacy views and the already approved
sample sheets, newly created exterior Sections have unique names
`MH_<FoundationId>_DRAFT_2D_OUT_W1` through `OUT_W4`.
The original `..._W1` sections are left untouched. The 7-row
generator also detects any already placed legacy or exterior
Sections and skips that foundation rather than stealing views from
the user's formatted sheet.

**Next verification:** Create 2D Views for one clean manhole, open
`OUT_W1`, verify you see the outside face, and then test the
seven-row sheet with other unplaced, clean foundations. If the
MH_SEC template hides the exterior wall surface, inspect its
discipline/detail/category visibility before changing geometry.


## 2026-09-30: six-row layout v2

The seven-row live-project test (13:20–13:29) tried 12 candidates,
but its 114.7 mm row left only 99.7 mm for drawings after the title
band. Plans measuring 106.8–110.8 mm therefore failed, and the empty
sheet was rolled back.

Active action is now **Test 6-Row Sheet** using
`SixRowLayoutSheetService`. It has six 1:25 rows, each containing
PLAN and four exterior-facing wall Sections (W1–W4). It reserves a
12 mm title band and leaves approximately 118.8 mm drawing height
on the previously logged titleblock (1181.1 x 831 mm). The runner
tries at most 8 unplaced eligible foundations to fill 6 successful
rows, so it does not waste minutes trying 12 known-tight cases.
It still checks exact viewport and title outlines and skips only
non-fitting rows; it does not silently scale below 1:25. The older
six-row test and manually arranged reference sheets are preserved.
The new sheet name is `HATCO_PRECAST_6MH_LAYOUT_V2_01`, preventing
accidental overwrites. The obsolete seven-row implementation was
deleted from this branch (remains in Git history).

Test by opening the preferred manual reference sheet, restarting
Revit after updating the DLL, and selecting **Test 6-Row Sheet**.
The code has not been compiled or tested in the user's Revit host
by the assistant. Report any TXT errors and resulting sheet image.


## First physical production milestone — Generate Selected Manhole

The `Generate Selected Manhole` button is now wired into the
single-window project runner. It intentionally processes **one
explicitly selected, clean and numbered manhole** rather than an
unattended project-wide batch.

1. Read-only checks validate its four-wall footprint, review register
   and all legacy cut states; scan linked MEP for crossings and
   separately report virtual endpoint candidates in the timestamped
   unified CSV.
2. Production proceeds only when there is at least one confirmed
   **ACTUAL** crossing, all proposed actual openings pass the existing
   size/wall fit validator, source keys are unique, there are at
   most eight openings, and no pair overlaps the same wall.
   No missing/unloaded required links are permitted.
3. The dialog gives the user **actual wall numbers, source IDs and
   opening sizes** and asks for explicit confirmation before editing.
4. The existing `CleanSyncAtomicService` creates/updates native Revit
   rectangular openings with original linked source identity and
   safely preserves unmatched existing managed openings. All
   destructive cleanup toggles are disabled: no reset of wall
   sketches, no deleting manual native openings, no unattached
   void unlinking, no in-place cutter deletion. Virtual extensions
   are **deferred**, not physically cut in this milestone.
5. A dedicated one-manhole A0/A1 sheet named
   `MH_<FoundationId>_OPENINGS_R01` is generated with the existing
   1:25 `MH_PLAN` view and four 1:25 exterior-looking-in
   `MH_SEC` Sections. Physical cuts are visible in their Revit
   views. A sheet-level **PRELIMINARY OPENING SETOUT / VERIFY**
   note lists actual source ID, W1–W4, cut width x height,
   horizontal offset measured from the original wall-axis start,
   and opening bottom elevation relative to the foundation top.
   This note is not a substitute for dimension references or
   approved manufacturer fabrication detailing.
6. A single `TransactionGroup` contains the existing atomic native
   cut transaction and the subsequent view/sheet transaction. If
   creation or layout fails, the group rolls back BOTH, leaving
   neither physical cuts nor a misleading partial sheet. Existing
   manual sheets are never overwritten or moved.

For acceptance testing, use a **saved disposable RVT copy** with
the two provided view templates, a loaded titleblock and a real
manhole Mark / saved number. Select one clean foundation with known
actual linked pipes/ducts. Review the CSV, approve its cuts, inspect
all physical wall openings in 3D and W1–W4 and cross-check the
preliminary setout note against the source model before relying on
the dimensions. Send the TXT log and new sheet screenshot if the
production run aborts. **No Revit-host compilation/runtime verification
has been performed in this development environment.**

## Current internal-only manhole naming workflow

Use **Assign Internal MH IDs** on a saved/editable host RVT.
The tool finds supported precast manhole foundations, including ones
isolated from opening generation. It previews one stable ID per
foundation (`MH-001`, `MH-002`, ...), exports a timestamped
`Internal_Manhole_Numbers_*.csv`, and asks for confirmation.
It ignores project/consultant numbering for assignment. Native Revit
`Mark` and project naming parameters are **read-only references**;
the bulk action never changes them. The tool-only internal name is
stored in ExtensibleStorage on each foundation, all in a single
Revit Transaction. Existing tool-only IDs are always retained on
repeated runs. Duplicate pre-existing internal IDs stop the run
without changing anything. Click Save or Synchronize in Revit to
persist the resulting transaction.

`ManholeViewTitleService` uses that stored internal ID **first**,
ahead of saved fabrication carrier names and project Marks. It
produces `MH-001 - PLAN` and `MH-001 - WALL W1` through `W4`
in Title on Sheet. Immediately after assignment the tool also
refreshes *only recognizable auto-generated titles* in its existing
DRAFT/PROD views, leaving handwritten titles unchanged. The main
project table displays the dedicated `Internal MH ID` column and
marks unnamed foundations as `NOT ASSIGNED`.

**First production prerequisite:** number all foundations before
clicking Generate Selected Manhole. Its physical opening routine
reads the stored tool-only ID and uses separate `_PROD_2D` views,
so earlier manually arranged `_DRAFT_2D` views are not moved.
This change does not itself create or approve any wall openings.


## Production preflight diagnostics and MH_3D (2026-09-30)

Previous generic error `Block=legacy cuts or links` did not
distinguish a manually cut wall from an unrelated unloaded Revit
link. The first production button now prints **exact blocked
conditions** on screen AND as `PRODUCTION BLOCKED` in the TXT
log: audit reasons, unavailable Revit link names/IDs, edited wall
profiles, existing unmanaged native openings, unattached void
cuts, in-place cutter IDs and unsupported solid cuts. This
preflight remains READ ONLY. No old opening or cutting family
is automatically deleted; a clean/test candidate may still be
blocked pending review or verification of missing link coverage.

For all cropped **Review 3D** views, the existing model's
`MH_3D` *3D view template* is now required and assigned.
A successful one-manhole production transaction also creates a
separate cropped `MH_<FoundationId>_PROD_3D` review view with
the same `MH_3D` template; this 3D view is not placed on the
2D manufacturing sheet. If the template is missing or turns off
the per-manhole Section Box, a clear exception is thrown and
the transaction rolls back rather than leaving an uncropped
whole-project 3D view. If the template controls Section Box,
uncheck Section Box in the template's controlled-properties list
to let the tool crop each manhole individually.

**Next diagnosis:** The older error's exact reason cannot be
established from its generic screenshot; upload the matching
timestamped `PrecastManholeManager_*.txt` plus
`UnifiedOpeningReview_*.csv`. Avoid relaxing cleanup safety
until actual blockers are verified.


## Limited partial-link test — MH-001 investigation

The 2026-09-30 14:06:37 live preflight showed that Foundation
4409885 passed its four-wall audit with no edited profiles, native
openings or void cuts. The loaded plumbing link instance 3997369
reported four ACTUAL FIT PREVIEW openings: W1 twice at 268.3 x
268.3 mm, W2 once at 600 x 600 mm, W3 once at 600 x 600 mm.
Ten OTHER link instances could not be read, including some MEP
links; their relevance is **UNKNOWN**, and the tool must not
silently dismiss or treat them as covered.

The first-one-manhole command now separates physical/legacy
blockers (which always prevent editing) from unavailable-link
coverage. When the only remaining blocker is unavailable links,
it offers a **separate explicit PARTIAL TEST approval** showing
the actual loaded source link(s) and missing link names. Clicking
No exits with no changes. Clicking Yes proceeds to the regular
actual-opening approval, and only then allows the existing atomic
native-opening writer to work from the scanned ACTUAL crossings.
Virtual candidates are still excluded. All destructive cleanup
options remain disabled; unmatched managed openings are preserved.
A partial-run sheet uses the separate name
`MH_<FoundationId>_OPENINGS_PARTIAL_R01`, with the prominent
`PARTIAL LINK COVERAGE - NOT FOR ISSUE` setout heading.
The final dialog and TXT log repeat the coverage limitation.
Missing links may contain additional or different penetrations.
The produced physical openings and sheet require rechecking after
the missing relevant links have been loaded. Never send such a
sheet for fabrication/site execution.

Once complete relevant link coverage is available, create a
separate approved/revalidated output; do not mistake a partial
preview for a complete issue. When a native opening or layout
fails, the enclosing TransactionGroup still rolls back cuts,
views, 3D and the partial sheet.
