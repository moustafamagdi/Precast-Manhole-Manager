# Clean & Sync experiment — TEST COPY ONLY

Branch: `experiment/virtual-manhole-geometry`

## Goal and scope

For **one selected manhole**, preview the old cuts in its four validated walls,
then allow a deliberate, **atomic** cleanup and managed opening synchronization.
The existing production `main` branch and experimental **Batch All** write
behavior are intentionally unchanged.

New ribbon command: **Hatco → Precast Tools → Test Clean Sync**.

1. Select one of the approved `HTC_ST_PRECAST_FN_200mm_MH` / `300mm`
   foundation instances on a **disposable detached RVT test copy**.
2. Set clearance, virtual face gap and horizontal approach angle.
3. The add-in creates a unified **read-only** opening review and a cleanup
   plan. The summary is recorded in the normal Desktop TXT log and
   `UnifiedOpeningReview_*.csv`.
4. Review:
   - Edited profiles per wall (Revit `Wall.RemoveProfileSketch` resets
     **all profile edits** on a wall; this is not per-hole editing).
   - Tool-created/managed native `Opening` elements by persistent
     `SourceKey`.
   - Non-tool native `Opening` elements including formerly adopted
     hand-made openings.
   - Individual *unattached* in-place/family void-to-wall **cut
     relationships** returned by `InstanceVoidCutUtils`.
   - Unsupported hosted inserts/solid cutting arrangements. If discovered,
     the entire plan is **BLOCKED**, not partially cleaned.
5. **Preview Only / Cancel** leaves the project unchanged.
6. To test an actual edit, you must separately tick the applicable destructive
   operations, acknowledge the test RVT copy, verify required MEP link coverage
   if any links are unavailable, and type exactly **CLEAN** to unlock
   **TEST APPLY (ONE MANHOLE)**.

## Atomic application

For one manhole, inside **one Revit Transaction**:
- Reset explicitly approved edited profiles to their original wall shape.
- Remove approved unattached family-void cutting **relationships only from
  the four selected walls**. Never delete the cutting family itself.
- Delete all explicitly approved non-tool native openings on those four walls,
  including previously adopted handmade openings.
- Regenerate and assert Revit did **not** cascade-delete or change the IDs
  of old tool-created managed openings.
- For each desired actual crossing and user-approved **straight/perpendicular**
  virtual endpoint proposal, match by source `LinkInstanceId + UniqueId +
  HostWallId`. Compare stored source position and cut width/height to 1 mm:
  **UNCHANGED** if matching, otherwise **UPDATED** by replace; create only
  missing managed openings. Regenerate after each new cut.
- Keep unmatched tool-created managed openings for review (some Revit links
  may be unavailable, and disappearing from a scan is not reliable evidence
  for deletion).
- On any exception or target Revit opening/join failure, **rollback the entire
  manhole transaction** and write the reason to the log.

**Not enabled yet:** batch destructive cleanup, deletion of unmatched managed
openings, and cuts from sloped/skewed virtual MEP endpoints (exact projected
cutting envelope needs additional validation). Such candidates are counted
as deferred in the test report. For the test manhole with 3 duct candidates,
the first safe run is expected to defer the sloped W4 duct while examining the
straight W2/W3 records.

**Unsupported geometry**: Some in-place cuts/embedded host families may not
be represented by `GetCuttingVoidInstances`; unknown
`SolidSolidCutUtils` or `Wall.FindInserts` relations explicitly block the
plan. Zero unattached relations does not prove that a wall is free of every
possible external cut.

## First test

1. Copy/detach the current RVT and save separately.
2. Test **Preview Only** on the known foundation `5144998` with three ducts
   and edited profiles. Verify the reported wall/Opening IDs against Revit.
3. Close and rerun **Test Clean Sync**; explicitly enable only the applicable
   changes, check test-copy and link-coverage confirmations, then enter
   `CLEAN`.
4. If the operation commits, rerun **Test Clean Sync** on the **same**
   foundation and confirm old tool-created openings are classified
   `UNCHANGED`. If it fails, inspect the rollback reason before altering code.
5. Send both the log and unified review CSV. Do not try Batch All writes
   until the single-manhole test is stable.


## Known pinned Model-In-Place Void case — 5046777

The real-model read-only plan for foundation **5144998** detected
`Wall.FindInserts` member **5046777** on wall **5044417**.
The user confirmed it is a **pinned Model-In-Place Void**.

The updated plan classifies family instances whose owning
`Family.IsInPlace` is true separately from Revit native `Opening`
objects and from `InstanceVoidCutUtils` relations. It checks all OTHER
walls in the current host document; if the instance is returned as an
insert on an outside wall, the cleanup plan remains **BLOCKED**.

The single-manhole experimental confirmation window now includes an
additional explicit **UNPIN & DELETE in-place cutter instances** choice.
When approved, the atomic transaction validates the cutter/target wall
relationship again; it first tests deleting the instance in a
rolled-back `SubTransaction`, rejecting unexpected cascading element
deletions. It then unpins the instance (if pinned) and deletes the
**instance**, not its family type, followed by regeneration. Failure
rolls back the entire manhole transaction including the pin state.

**Important limitation:** `Wall.FindInserts` and cascading element ID
checks do not exhaustively prove that a void has no side effects on
foundations or other non-wall elements. The user must visually verify
that before explicitly opting into this destructive test on a
**disposable detached RVT copy**. Do not enable unattended Batch All
cleanup of in-place cutters based on this experiment alone.

After pull/rebuild, test **Test Clean Sync** on foundation **5144998**;
send the new TXT log if 5046777 is classified as in-place or if the
program still blocks it. If it is not recognized as `FamilyInstance`
with an in-place owning family, leave it blocked and inspect the new
reported runtime class instead of forcing deletion.
