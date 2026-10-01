# Stabilize existing manholes

Use `Openings - Existing Selected` first, then `Openings - All Prepared` after checking the result. Both require the existing production PLAN and W1–W4 on their reserved six-row sheet. Missing/incomplete rows are skipped, not generated. Legacy individual sheets are not included in this mode.

These operations validate current physical blockers again, update managed pipe/duct openings with the selected per-side clearance, refresh the opening note and regenerate tool-owned dimensions. Manual cuts remain protected. They do not assign IDs, reserve rows, create views/sheets/3D views, move viewports or rearrange rows. Updating dimensions can change viewport extents, so inspect spacing afterward.

`Dimensions - Selected` updates the selected manhole only. `Dimensions - All Prepared` runs dimensions independently of opening preflight, so body/base dimensions can be attempted even when cuts need manual work. Neither creates missing views. Only managed openings receive automatic opening dimensions; manual openings are not adopted.

The prepared-row runs save the current local RVT every ten items or five minutes between items, and at completion/cancellation. There is no SaveAs or synchronize. The adjacent `.existing.csv` records each attempted foundation and its outcome. Skips are in the TXT log. Completion returns control to Revit rather than reopening the manager.

Dimension validation now checks enabled annotation crop bounds, including paper-space offsets multiplied by view scale, instead of rejecting annotations outside the model crop. Hidden Dimensions in MH_PLAN/MH_SEC remain an explicit review issue: enable the category in the intended template before testing. The tool does not modify shared templates. Real reference/value checks and per-view rollback remain active.

## Threading assessment

Revit 2024 API calls must run on the main Revit thread in a supported API context. This includes collectors, geometry/reference access, creating openings/dimensions/views and transaction commits. Task.Run/Parallel.For cannot safely accelerate these operations. ExternalEvent schedules work in that context; it does not make document operations concurrent.

Future parallel work could operate on detached numeric snapshots (no Document, Element, XYZ or geometry API access) for sufficiently expensive calculations. Current evidence points to API view/crop/viewport work; linked MEP scanning already has a run-scoped cache. No threading change or speedup claim is justified yet.

Reference: https://help.autodesk.com/cloudhelp/2024/ENU/Revit-API/files/Revit_API_Developers_Guide/Introduction/Getting_Started/Using_the_Autodesk_Revit_API/Revit_API_Revit_API_Developers_Guide_Introduction_Getting_Started_Using_the_Autodesk_Revit_API_Deployment_Options_html.html

Runtime acceptance: test one existing row; confirm no new views/sheets, unchanged viewport centers, resized managed openings, visible associative opening/body/base dimensions, preserved manual dimensions and a second run without duplicate tool dimensions. Build/regression checks do not replace this Revit test.
