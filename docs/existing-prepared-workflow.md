# Stabilize existing manholes

Use **Openings → Run Openings**. **Apply to** defaults to **Pick bases in Revit**; selected row, current model view and all detected manholes are also available. Openings no longer require sheets or views. Missing PLAN/W1–W4 defers dimensions.

These operations validate current physical blockers again, update managed pipe/duct openings with the selected per-side clearance, refresh the opening note and regenerate tool-owned dimensions. Manual cuts remain protected. They do not assign IDs, reserve rows, create views/sheets/3D views, move viewports or rearrange rows. Updating dimensions can change viewport extents, so inspect spacing afterward.

`Dimensions - Selected` updates the selected manhole only. `Dimensions - All Prepared` runs dimensions independently of opening preflight, so body/base dimensions can be attempted even when cuts need manual work. Neither creates missing views. Only managed openings receive automatic opening dimensions; manual openings are not adopted.

The prepared-row runs save the current local RVT every ten items or five minutes between items, and at completion/cancellation. There is no SaveAs or synchronize. The adjacent `.existing.csv` records each attempted foundation and its outcome. Skips are in the TXT log. Completion returns control to Revit without a save/completion popup. Results remain in the TXT log and CSV report. Save failures still report an error.

Dimension validation now checks enabled annotation crop bounds, including paper-space offsets multiplied by view scale, instead of rejecting annotations outside the model crop. Hidden Dimensions in MH_PLAN/MH_SEC remain an explicit review issue: enable the category in the intended template before testing. The tool does not modify shared templates. Real reference/value checks and per-view rollback remain active.

## Threading assessment

Revit 2024 API calls must run on the main Revit thread in a supported API context. This includes collectors, geometry/reference access, creating openings/dimensions/views and transaction commits. Task.Run/Parallel.For cannot safely accelerate these operations. ExternalEvent schedules work in that context; it does not make document operations concurrent.

Future parallel work could operate on detached numeric snapshots (no Document, Element, XYZ or geometry API access) for sufficiently expensive calculations. Current evidence points to API view/crop/viewport work; linked MEP scanning already has a run-scoped cache. No threading change or speedup claim is justified yet.

Reference: https://help.autodesk.com/cloudhelp/2024/ENU/Revit-API/files/Revit_API_Developers_Guide/Introduction/Getting_Started/Using_the_Autodesk_Revit_API/Revit_API_Revit_API_Developers_Guide_Introduction_Getting_Started_Using_the_Autodesk_Revit_API_Deployment_Options_html.html

Runtime acceptance: test one existing row; confirm no new views/sheets, unchanged viewport centers, resized managed openings, visible associative opening/body/base dimensions, preserved manual dimensions and a second run without duplicate tool dimensions. Build/regression checks do not replace this Revit test.

## Select scope from Revit

Openings - Pick Bases uses the current Revit selection when nonempty (recognized manhole bases only). With no preselection, the manager closes for interactive multi-picking: select bases and press Finish; Esc cancels without edits. A nonempty selection without recognized bases is rejected rather than falling back to all manholes.

Openings - Current View collects host structural foundations using Revit's view-scoped collector, then intersects with recognized manholes. It uses the active model view, not screen zoom; sheets are rejected. Revit visibility/occlusion rules can include candidates that are not visually obvious, so inspect the highlighted targets and confirmation count. Linked bases are excluded. Neither action requires prepared rows or creates views/sheets.


## Verified end connectors

Production and Clean Scan now include unambiguous pipe/duct end connectors touching a wall, stopping within its thickness, or stopping up to 150 mm before its face. Sources already crossing the wall mid-plane continue through actual-crossing detection. The endpoint must match a real End connector in transformed host coordinates; plan approach remains limited to 15 degrees. Unsupported or ambiguous connectors remain deferred.

Connector cross-sections are projected onto the wall plane, including travel through wall thickness. Rectangular connector rotation and round outer pipe diameter are respected; clearance is added afterward. Wall fit, overlap checks, source-key deduplication and manual-edit protection still apply. Linked elements are never extended in the model. Test MH-003 first, then rerun it to check no duplicate openings and clearance updates. Automated tests cover eligibility and envelope mathematics; live Revit connector/geometry validation is still required.


## Independent walls and combined openings

Each independent opening or connected group commits separately during Run Openings and the opening stage of Generate / Update All. An unsafe fit or failed transaction preserves that group for review without blocking other groups on the same wall. An edited profile or manual/void cuts still block the affected wall. Existing combined-cut members stay in one transaction even after they move apart. Dimensions are refreshed from committed openings; whole-base Repair retains its separate atomic behavior.

**Merge overlapping openings** is off by default. When enabled it combines intersecting opening rectangles on the same wall, including the existing under-5-mm separation tolerance. It takes the enclosing rectangle after per-side clearance and validates that entire cut against the wall. It also merges further openings reached by that rectangle. Different walls never combine.

Combined cuts persist every member source key. Reruns reuse or resize the same group; known tool cuts can be replaced when all their member sources are present. Missing/unloaded members, copied opening ownership mismatches and overlapping unmatched old cuts retain that wall for review. Manual openings are never merged or deleted. Turn merge on again when updating existing combined cuts.

Clean Scan recognizes a combined cut only when its physical rectangle covers the relevant members and fits the wall. Opening results report each wall and retain OPENINGS REVIEW until all walls pass.

### Revit acceptance test (not covered by the offline calculation tests)

1. Foundation 4440751, merge OFF: W2/W3 should commit; overlapping W4 stays in review.
2. Same base, merge ON: reuse W2/W3 and create one enclosing W4 cut if its full envelope fits.
3. Repeat: no duplicate cuts. Change clearance: update the existing group, then check its associative dimensions.
4. Add an edited profile or an unfit crossing to one wall: that wall and its existing cuts/dimensions remain unchanged; other walls finish.
5. Test a group with one member missing/unloaded: preserve its cut and report review. Repeat with merge OFF: preserve the combined cut for review.
6. Run Clean Scan after a successful merge: the resolved pair must no longer be reported as overlapping separate openings.

## Shared corner openings

For a detected source crossing two adjacent perpendicular wall ends of the recovered manhole, the tool verifies the common source and end proximity. It projects the straight pipe/duct connector cross-section through each wall thickness, includes the chosen clearance, and creates a separate native opening on each intersected wall. Only the verified end is permitted to extend beyond the normal 5 mm horizontal margin. Vertical limits and whole-wall-width protection remain enforced. No wall movement, profile reset or manual cut deletion is authorized by this feature.

The full projected rectangle crosses the wall end; it is not shortened to leave a concrete sliver at the corner. Revit acceptance of these native end cuts and their associative dimensions must be verified in the live model. Rejected groups stay in review while independent groups remain committed. Sources that cannot be verified/projected retain the normal fit restrictions.

Acceptance: foundation 4445337 / source 32848953 should attempt both W2 and W4 corner cuts. The independent sources 32810164, 32810225 and 32848983 must proceed even if a corner group fails. Rerun to verify reuse, adjust clearance to verify resizing, and inspect both wall ends in 3D. If same-wall envelopes overlap, enable Merge overlapping openings. No merge toggle is required merely because one source crosses two walls.

## Duct openings at a wall end

A duct opening that crosses a wall end, or leaves less than 5 mm there, is aligned with that end at its full width including clearance. The opening center shifts along the wall; the linked duct is not edited. A cut too wide for the wall, entirely outside it, or failing vertical fit still requires review. This duct rule takes precedence over shared-corner end extension; pipe corner behavior remains unchanged.

The log, opening-review CSV and run report record the signed shift along the wall axis. A nonzero shift retains site-coordination review even when the opening was committed. Clean Scan reports the same outstanding coordination until the duct position no longer needs adjustment. Stored opening centers are the actual fitted centers so reruns reuse them correctly.

Run Openings now starts after scope selection/picking without the extra Existing Manholes confirmation dialog. Automatic saving remains active and failures remain visible; dimension-only and base-repair confirmations retain their previous behavior.
