# Experimental branch: virtual manhole geometry

Branch: `experiment/virtual-manhole-geometry`

## Stage A — cropped foundation recovery (implemented for testing)

Run **Hatco > Precast Tools > Test Virtual Foundation** and select one Structural Foundation.

Supported foundation **type** names (exact, case-insensitive):
- `HTC_ST_PRECAST_FN_200mm_MH`
- `HTC_ST_PRECAST_FN_300mm_MH`

Supported wall **type** names (exact, case-insensitive):
- `HTC_ST_PRECAST_WL_200_MH`
- `HTC_ST_PRECAST_WL_150_MH`

This is **read-only**. It does not remove cuts, unjoin walls, create new geometry, write Extensible Storage, alter openings, or process a batch.

The test reconstructs candidate four-wall rectangular footprints independently of the cropped foundation's bounding-box center. It validates two parallel pairs, perpendicularity, intersection of finite wall segments, wall elevations and foundation proximity. It refuses ambiguous competing footprints rather than choosing silently.

Output: popup summary and detailed timestamped TXT log in `Desktop\Precast Manhole Manager\Logs`.

### Suggested first tests

1. Select an intact manhole foundation; compare detected walls and clear dimensions to Revit.
2. Select a foundation visibly cut by a neighboring base; compare virtual center to the expected full footprint.
3. Select two adjacent manholes in turn; ensure their four wall IDs do not mix.
4. Select an unsupported Structural Foundation and verify it is rejected safely.

Keep the existing **Batch Selected / Batch All** off during this stage. The production detector has *not* been switched to the virtual footprint engine yet.

Share the TXT logs for one intact, one cropped and (if available) a neighboring-manhole case. Include the four correct Wall IDs if the test rejects them or selects a wrong combination.

## Stage B — virtual MEP extension (next, after Stage A validation)

Scan linked pipes and ducts that stop shortly before a manhole wall. Extend their *transformed centerline analytically only*, never change the linked models. Preserve original element identity, slope and section size. Record wall candidate, gap to the actual wall face and extended intersection coordinates. Surface uncertain endpoints as review-only; do not create any openings in this experimental stage.

## Stage C — controlled integration (after Stage B validation)

Only then make validated virtual geometry and explicitly accepted virtual intersections available to the preview and opening sync. Do not automatically adopt, resize or delete existing manual openings.


## Stage B — delivered experimental read-only scanner

**Hatco > Precast Tools > Test Virtual MEP + Audit**

Select one manhole foundation. The command first uses the tested virtual
four-wall geometry and then:
- searches **loaded linked pipes and ducts** with straight centerline curves;
- analytically extends endpoints outward from their last modeled segment;
- only proposes intersections up to **150 mm from the wall FACE** and within
  **15 degrees from the wall normal**;
- rejects curves already crossing the location plane (handled by the actual
  penetration scanner);
- rejects projected hits outside a straight wall's length or vertical bounds;
- reports ambiguous candidates instead of silently choosing a wall;
- writes `VirtualMepCandidates_*.csv` and a detailed TXT run log in
  `Desktop\Precast Manhole Manager\Logs`.

No modeled pipe/duct is extended. No wall opening is created for a
virtual candidate. Every virtual candidate is review-only.

### Opening reset audit (same command)

For each validated manhole wall, the command inventories existing **native
Revit Opening** instances, distinguishing managed and manual, inspects wall
profile SketchId where available, and attempts to count unattached void cut
relationships through the Revit API.

This is an **audit, not reset**. A manually edited wall profile may contain
geometry that is not an opening. An in-place void family may cut multiple
elements; deleting it without tracing every cut is unsafe. Unknown profile
or void statuses are reported explicitly, not assumed clean. The command
does not delete, reset, unjoin or otherwise mutate the model.

### First tests

1. Test a loaded linked pipe that **actually crosses** one manhole wall. It
   must not be returned as a virtual-only candidate.
2. Test a pipe stopping **20–100 mm before a wall face**. Check the reported
   wall, projected hit, gap to face, extension reach to wall axis and slope.
3. Test a nearby pipe moving away or parallel to the wall; it must not be
   proposed.
4. Test a manhole with an edited wall profile; confirm audit classification.
5. Test a manhole with an in-place void cut; confirm audit classification
   (or UNKNOWN when the API cannot resolve the relationship).

## Stage C — experimental Batch All settings

**Batch All** now shows a configuration window **before** running:
- Clearance per side: default 50 mm, editable 0–500 mm.
- Edge policy: **Review / preserve**, or **Trim excess clearance only if the
  original pipe/duct envelope remains entirely inside the final opening**.
- **Preview Only** checked by default. This is strongly recommended until
  the project and log data have been checked.
- **Audit existing openings** enabled by default.

On this branch, Batch All uses the virtual four-wall footprint recovery
rather than the old nearest-four wall detector. Candidate foundations are
restricted to the two explicit types supplied by the user.

No automatic profile reset or in-place void deletion exists in this stage.
When APPLY is chosen, a second confirmation is required. Walls with edited
or unknown profiles or detected/unknown void cuts are skipped. Manual native
openings are preserved. Stale managed openings are preserved and marked
for review rather than automatically deleted. Failed opening replacements
are rolled back using a per-opening SubTransaction.

**IMPORTANT**: Stage B virtual MEP candidates are currently *diagnostic
only*. They are not inserted into the Batch All creation list. We will only
enable explicit adoption/creation after Stage B test logs prove the
projection, source identity and wall assignment are correct.

A proposed clearance-trimmed opening is not equivalent to a fully
dimensioned construction detail; verify wall edge setbacks and structural
approval before any APPLY run.

### Guarded Batch Selected and updated projection report

On this branch, Batch Selected now uses the same settings as Batch All: Preview Only is checked by default and APPLY requires explicit confirmation. Audited edited or unknown wall profiles and void cuts are marked for review even in preview, without counting cut proposals for those walls. The legacy behavior on main is unchanged.

Virtual endpoint reports also include source size, system and slope (%). GapToFace is measured against the wall solid mid-plane where Revit geometry is available; ReachToAxis includes the remaining reach to the wall axis.


## Stage B — read-only virtual MEP + opening reset audit (experimental code ready for Revit test)

Command: **Hatco > Precast Tools > Test Virtual MEP + Audit**.
Select a recognized manhole Structural Foundation. This command:
- Reconstructs the complete four-wall footprint using the accepted virtual foundation detector.
- Scans linked straight Pipes and Ducts without modifying them.
- Evaluates eligible endpoints ending up to **150 mm before wall face**, with default **15°** maximum approach deviation from wall normal. Preserves their original 3D slope in projected coordinates.
- Checks projected hit against finite wall axis and wall vertical extents; skips actual plane crossings.
- Emits **VirtualMepCandidates_<timestamp>.csv** and timestamped TXT log in the usual Desktop Logs folder.
- Classifies *every* virtual candidate as **REVIEW**, including ambiguous candidate walls. Does not create openings.

The same command inventories:
- Revit native managed / unmanaged `Opening` objects hosted by the four walls.
- Wall SketchId/profile status when detectable; UNKNOWN is explicitly reported.
- Unattached void-cut relationships accessible through the Revit API; UNKNOWN is explicitly reported.

**No reset or uncut** is performed. Profile edits could include intended geometry, and deleting an in-place cutting family could affect many unrelated objects. This is deliberately inventory-only until user review and case-specific safety checks exist.

## Experimental Batch All preview
The experimental **Batch All** command now shows a settings window. It only selects the two approved foundation type names. Options:
- Clearance per side (0–500 mm), default 50 mm.
- Boundary policy: flag as REVIEW (default), or propose a clearance-only clipped rectangle **provided the actual MEP section remains fully enclosed**.
- Optional existing opening/profile/void audit, default ON.
- Optional expensive virtual endpoint scan of every candidate foundation, default OFF.

**Preview Only is mandatory in this experimental build.** No new openings or data carriers are created from these settings. Results include proposed cuts, proposed clearance reductions, opening reviews, and optional virtual endpoint candidates. Output and logs are preliminary; successful geometry tests do not constitute fabrication approval.

Do not use experimental data for site cutting until validation of face locations, wall cuts, opening positions and legacy edited profiles is completed. The legacy Batch Selected command is not part of this experimental preview route.

### Please test in this order
1. **Test Virtual MEP + Audit** on a cropped-base manhole whose pipe stops at a wall face.
2. Repeat on a manhole with a wall sketch/profile opening and a wall cut by an in-place void family.
3. Inspect the per-run candidate CSV and diagnostic log. Confirm linked Element IDs, wall IDs, gap, projection, and opening inventory.
4. Run **Batch All** with *Preview Only*, standard 50 mm clearance and *Review* edge policy; compare proposals with your model.
5. Repeat preview with *Trim Clearance Only* on a manhole known to have an oversized clearance.

No automatic profile reset, void deletion or batch write should be attempted on this branch at this stage.
