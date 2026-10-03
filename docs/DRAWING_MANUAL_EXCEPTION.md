
### Incomplete-wall manual documentation

For exceptional bases such as MH-170 / MH-171, select one row and use **Drawings > Manual views - Incomplete walls (Selected) > Run Drawing Task**. This explicitly selected action prepares/reuses the sheet and PLAN/W1–W4 without opening or dimension generation. Normal workflows retain their validated four-wall requirement.

When four-wall recovery fails, the fallback uses the project-axis base bounding envelope for four full-depth exterior overview views. Surviving walls overlapping the base and starting near its elevation supply height only; this is not a validated wall assignment. At least one such wall is required. Verify crops, template visibility and orientation manually, especially for rotated/irregular bases. Existing views/crops are reused; a Views review remains for manual completion. No base, wall, pin, cut or manual dimension is edited. Save the current model after reviewing the output.

Validation: Release build and workflow UI smoke/drawing-policy checks passed. Read-only query on the current model found two height candidates for foundation 5079277 and three for 5079966. Creation and rendering in Revit remain to be tested.
