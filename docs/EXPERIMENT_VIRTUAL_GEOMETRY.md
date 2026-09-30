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
