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
