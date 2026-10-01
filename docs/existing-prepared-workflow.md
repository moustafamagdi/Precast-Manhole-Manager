# Stabilize existing manholes

Use **Openings → Run Openings**. **Apply to** defaults to **Pick bases in Revit**; selected row, current model view and all detected manholes are also available. Openings no longer require sheets or views. Missing PLAN/W1–W4 defers dimensions.

These operations validate current physical blockers again, update managed pipe/duct openings with the selected per-side clearance, refresh the opening note and regenerate tool-owned dimensions. Manual cuts remain protected. They do not assign IDs, reserve rows, create views/sheets/3D views, move viewports or rearrange rows. Updating dimensions can change viewport extents, so inspect spacing afterward.

`Dimensions - Selected` updates the selected manhole only. `Dimensions - All Prepared` runs dimensions independently of opening preflight, so body/base dimensions can be attempted even when cuts need manual work. Neither creates missing views. Only managed openings receive automatic opening dimensions; manual openings are not adopted.

The prepared-row runs save the current local RVT every ten items or five minutes between items, and at completion/cancellation. There is no SaveAs or synchronize. The adjacent `.existing.csv` records each attempted foundation and its outcome. Skips are in the TXT log. Completion returns control to Revit rather than reopening the manager.

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

Each wall commits independently during Run Openings and the opening stage of Generate / Update All. A wall with an edited profile, manual/void cuts, unsafe fit or failed Revit transaction stays in review; successful walls remain committed. Dimensions belonging to a failed wall are preserved. Whole-base Repair retains its separate atomic behavior.

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
