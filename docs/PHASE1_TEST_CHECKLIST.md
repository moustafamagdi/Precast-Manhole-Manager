# Phase 1 Test Checklist

## Goal
Validate geometry detection before the add-in modifies the Revit model.

## Build
1. Open `PrecastManholeManager.sln` in Visual Studio 2022.
2. Confirm Revit 2024 is installed under the default Autodesk path.
3. Build `Release | Any CPU`.
4. Confirm these files exist:
   - `%APPDATA%\Autodesk\Revit\Addins\2024\PrecastManholeManager\PrecastManholeManager.dll`
   - `%APPDATA%\Autodesk\Revit\Addins\2024\PrecastManholeManager\PrecastManholeManager.addin`

## Revit test
1. Open the structural host model.
2. Load the MEP links that contain the pipes/ducts crossing the manhole walls.
3. Open any convenient 3D/plan view.
4. Run:
   **Hatco > Precast Tools > Scan Manhole**
5. Pick one Structural Foundation belonging to a simple manhole.
6. Confirm the four wall IDs in the result.
7. Open the generated CSV and compare detected penetrations with the model.

## What to validate
For every test manhole check:
- The selected base/foundation is correct.
- Exactly four surrounding walls are detected.
- W1 is opposite W4.
- W2 is opposite W3.
- Linked pipes/ducts that physically cross a wall are detected.
- MEP elements close to the manhole but not crossing walls are not detected.
- `InvertAboveBase_mm` matches the vertical distance from top of base slab/foundation to the bottom/invert of the service.
- `OffsetFromWallStart_mm` is stable between repeated runs.

## Recommended first sample
Use one rectangular manhole with:
- straight walls,
- one horizontal pipe,
- preferably one sloped pipe,
- optionally one rectangular duct.

This gives enough cases to validate the geometry without introducing too many variables.

## Debug package to share after a bad result
Send:
1. The timestamped TXT log.
2. The matching CSV.
3. A screenshot/3D view showing the manhole.
4. If possible, mark which wall you expect as W1.
5. State which Revit Element ID is wrong/missing.

The TXT log already captures:
- Revit/document context,
- foundation ID and bounding box,
- every wall candidate ID,
- wall length/midpoint/direction/distance,
- assigned wall numbers,
- every loaded Revit link and transform,
- linked MEP candidate count,
- detected penetration IDs,
- penetration XYZ,
- size,
- invert,
- invert above base,
- wall offset,
- exceptions with stack traces.

## Known Phase 1 limitations
- Current automatic W1 orientation is based on Project XY/north, not a user-selected datum wall.
- Wall detection currently prioritizes the four closest straight wall axes around the foundation.
- Curved walls are intentionally ignored.
- No Revit elements are created, cut, numbered, or modified yet.
- Cable tray/conduit invert may use centerline if a usable vertical size is not available.
- Opening clearance and skew/angled penetration sizing are Phase 2/3 items.
