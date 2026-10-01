# Repair lower-wall opening failures

Use **Repair Low Base - Pick** to select bases, or **Repair Low Base - View** for recognized bases in the current model view. The existing prepared-row requirement still applies. Normal Openings buttons do not lower foundations automatically.

The repair requires at least one confirmed crossing/verified endpoint opening to fail at the wall bottom. Horizontal fit failures, openings above the wall top, edited profiles, manual cuts, grouped elements, attached/non-basic walls and joins to other model elements are rejected. Cases without a bottom failure are skipped, even if their existing base gap is less than 100 mm.

The lowest required opening bottom includes the operator's clearance. Target base top is that elevation minus **100 mm**. The base is translated down with unchanged thickness and footprint. The four walls extend to its new top; their upper elevations and horizontal bounds must remain unchanged. Originally pinned elements are temporarily unpinned and restored to their original pin states. There is no arbitrary maximum drop; the computed old/new elevations and drop are logged.

Existing section crops and plan bottom/depth ranges are extended downward without moving viewports or creating views. Custom/split section crops and unsupported view-range constraints require manual review. A larger view may occupy more sheet space; inspect the repaired row visually.

Geometry, view extents, openings and tool dimensions are contained in one transaction group per manhole. Any failure, including incomplete dimensions, rolls the entire repair back. A second scan checks that newly exposed crossings do not violate the 100 mm gap. CSV reports distinguish repaired, skipped and review cases; normal in-place checkpoint saves still apply.

Validation: pure regression checks cover the drop formula, negative elevations, edge margin, already satisfied gap and rejection of upper/both-end failures. Live Revit acceptance is still required: one pinned base and four pinned walls, unchanged wall tops/base thickness, 100 mm below the lowest clearance-inclusive opening, retained original pin states, correct dimensions/crops, and a second run that skips without lowering again. Also verify rollback with a deliberately unrepairable constraint.
