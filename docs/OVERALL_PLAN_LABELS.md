# Overall plan manhole labels

Open the intended overall plan before opening the manager. In Drawings choose **Overall plan labels - Active plan**, then **Run Drawing Task**. No grid-row selection is required. Bases in the active view receive a two-line managed TextNote and leader using the current default text type. Adjust text positions/type manually for readability. Run again after sheet changes: text updates in place; if a base moves the label follows its displacement while preserving its manual offset. These are explicit-refresh annotations, not live family tags.

Placement comes from current Viewports of the production PLAN / OUT_W1–W4 view names (`MH_<foundationId>_PROD_2D_*`), never from reserved sheet slots. Keep these internal view names; title-on-sheet may be changed. A single sheet displays its actual SheetNumber. Split placement lists all sheet numbers; fewer than five placed views is marked PARTIAL; no placements is NOT PLACED. Existing manual annotations are not touched. Duplicate tool labels are reported rather than deleted. Tool labels whose base is absent/outside the current view are marked NOT IN CURRENT VIEW / CHECK, not silently deleted.

Supports host manhole bases recognized by the manager with assigned Internal MH IDs, in an unsplit rectangular plan crop (or crop disabled). Check annotation crop, visibility and text overlap visually; automatic label collision arrangement is not implemented. Nearby labels may need manual spacing. Viewport name-based mapping cannot discover arbitrarily renamed or duplicated production views.

Use **Rename batches - Cast in site** to replace the whole word Precast in names of sheets whose number starts with MH-BATCH-. Sheet numbers and placements are unchanged, custom names without that word are retained. New batch sheets use Cast in site Manholes by default. Save the current RVT after checking output.

Validation: Release build, label-policy regressions (moved/custom/split/unplaced/partial sheets, naming) and workflow UI smoke passed. Revit label creation, leader display and collision/annotation-crop visual checks still require a live run. Existing model sheets have not been renamed by the code update alone.

Identity guard: labels require both a recognized manhole base type and the tool-owned Internal MH ID matching exactly MH- followed by ASCII digits (for example MH-01 or MH-170). Arbitrary element names/Marks do not qualify. Invalid IDs are skipped and counted/logged; an old tool label for an invalid/deleted identity is flagged INVALID MANHOLE ID / CHECK.

## Coverage audit

After label updates, a read-only coverage audit now runs automatically. **Check overall coverage - Active plan** also runs it independently. Its denominator is ALL bases currently recognized by LoadFast, not only visible bases. Missing/invalid/duplicate IDs, hidden or crop-clipped bases, missing/duplicate/hidden/cropped labels, missing label bounds, outdated text/sheet information and stale anchors cause REVIEW. Orphan tool labels are reported separately. Custom/split crops and temporary hide/isolate are marked unverified. Empty models cannot pass coverage.

The result includes counts and an `.overall-coverage.csv` in the log folder with one row per recognized base (name and ElementId), label IDs and reasons, plus orphan label rows. Full details are also logged. No model or registry changes occur during this check. A passed automated check is not a pixel/rendering guarantee: occlusion, text collisions and graphical readability still require visual review. If coverage fails after label creation committed, the message explicitly says labels committed and coverage not verified.

Build, UI smoke and label-policy tests passed; the new coverage command still requires a live Revit run on the intended overall plan.
