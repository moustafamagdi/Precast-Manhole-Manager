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
