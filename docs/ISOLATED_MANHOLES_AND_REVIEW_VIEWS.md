# Isolated problematic manholes and per-manhole 3D review

Experimental branch only: `experiment/virtual-manhole-geometry`. Production
`main` remains unchanged.

## Known test case (2026-09-30)

- Foundation: **5144998**; four-wall virtual geometry was accepted.
- **3 edited wall profiles**, plus pinned model-in-place cutter instance
  **5046777** on wall **5044417**.
- Revit dry-run deletion of 5046777 reported 25 additional deleted elements,
  including walls **5047464** and **5047465**. This is **BLOCKED**; do not
  bypass the destructive cascade guard.
- Linked ducts: **26866172** (W3 straight), **28477345** (W2 tip within
  wall), **31521241** (W4 sloped). These are geometric review proposals,
  not permission to create openings.

## How to use

Ribbon: **Hatco > Precast Tools > Review Queue / 3D**.

1. **Scan All & update issue register** performs a light read-only
   geometry / existing-cuts audit on the two approved manhole foundation
   types. Newly found issues are upserted, and old issues are **retained**.
   This command does not rerun all detailed MEP scan tests.
2. The list displays one row per problematic foundation with issue reason,
   element ID, wall IDs, status, last detection date, and any previously
   created 3D view.
3. Select the desired rows, choose a Section Box margin (default
   **350 mm**) and click **Create / Update 3D Views**.
4. Each selected manhole gets **MH_REVIEW_<FoundationId>** as a normal
   isometric 3D view. Section Box XY bounds come from its four validated
   walls, **not the possibly cropped foundation bounding-box center**.
   Z extends over the detected wall and foundation vertical bounds with
   the same margin. A failed geometry detection falls back to the
   foundation bounding box. Existing views are reused by stored ViewId.
   View creation happens in a transaction per manhole; if any one
   fails, others can still complete.
5. The first created view opens after finishing. All views are available
   in Revit's Project Browser for navigation and can be renamed/moved
   by the user; the stored ViewId supports subsequent update.
6. After an issue is corrected, select its row and choose **Mark Selected
   Resolved**. Use **Reopen Selected** if it needs more work.

## Persistency and Batch behavior

The issue register is saved **locally** under
`Desktop\Precast Manhole Manager\Review Register` with a separate
file for each SAVED RVT full path (a sanitized filename and hash).
An adjacent human-readable `.csv` is exported on opening or updating
the review queue. The register contains the foundation's `UniqueId`,
last known ElementId, reason, severity, status, wall IDs and any view ID.
It survives closing Revit and new ChatGPT sessions on this same PC.
Changing the RVT path creates a separate register; this is deliberate
to prevent confusing detached project copies.

The existing **experimental Batch All preview** now reads this queue
and **skips OPEN isolated cases**, but continues other manholes.
New invalid footprint, existing-profile/manual/void audit failures, or
processing exceptions are appended to the queue. The legacy Batch
Selected path and production `main` are unchanged.

The **Test Clean Sync** command also records blocked plans, in-place
cutters, and failed atomic-cleanup transactions automatically in the
same register. A rollback never marks a manhole resolved.

Review views are ordinary RVT view elements, so **save the RVT** after
creating them. Issue registers are independent local files. View creation
is not destructive opening cleanup and does not edit wall/MEP geometry.

## First acceptance test

1. In a **saved test copy**, update the branch and open **Review Queue / 3D**.
2. Choose **Scan All** and verify the register contains foundation
   `5144998`, including the pinned in-place cutter `5046777`.
3. Select only that row, default margin 350 mm, and generate 3D view.
4. Confirm the section box isolates its own four walls even when a
   neighboring foundation is cropped or adjacent. Neighboring objects
   inside the 350 mm margin might still appear.
5. Reopen **Review Queue / 3D** using **Open saved issue register**,
   confirm the same record / ViewId persists. No rerun of the costly
   MEP scanner is required just to reopen this list.
6. Send the short TXT log and a screenshot of the generated 3D view
   if anything is incorrect.

**No automated deletion of known problematic in-place cutters** is
authorized by this feature. Isolation takes priority over broad cleanup.
