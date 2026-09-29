# Precast Manhole Manager

Revit add-in for detecting precast manholes, identifying linked MEP penetrations, calculating opening data, and later generating/updating openings and precast schedules.

## Target
- Autodesk Revit 2024
- .NET Framework 4.8
- Visual Studio 2022
- Ribbon tab: **Hatco**
- Panel: **Precast Tools**

## Roadmap

### Phase 1 — Diagnostic scanner (current)
Read-only. No model modifications.

1. Pick one Structural Foundation representing a manhole base.
2. Detect the four surrounding Walls.
3. Number the walls deterministically W1-W4.
   - W1/W4 are opposite.
   - W2/W3 are opposite.
   - Initial automatic numbering uses wall direction/position and is logged for verification.
4. Scan loaded Revit links for:
   - Pipes
   - Ducts
   - Cable Trays
   - Conduits
5. Transform linked geometry into host coordinates.
6. Detect centerline intersections with each manhole wall solid.
7. Calculate:
   - Linked element/source link
   - Host wall + wall number
   - Intersection XYZ
   - MEP size
   - Absolute invert/bottom elevation at wall
   - Invert above foundation top
   - Horizontal offset along the wall
8. Write:
   - Timestamped TXT diagnostic log on Desktop
   - Timestamped CSV penetration report on Desktop

### Phase 2 — Opening preview (current)
Modeless WPF window shown after the diagnostic scan.

- Review all detected penetrations.
- Accept/reject individual penetrations.
- Default clearance: 50 mm per side.
- Apply a different clearance to all detected penetrations.
- Preview recommended round/rectangular opening size.
- Export accepted penetrations to CSV.
- No Revit model elements are modified in Phase 2.

### Phase 3 — Create/update openings
Void families for round/rectangular penetrations. Persistent source IDs and statuses:
NEW / UNCHANGED / MOVED / RESIZED / REMOVED.

### Phase 4 — Manhole data carrier
Create one lightweight Generic Model data carrier per manhole and persist manhole identity/dimensions.

### Phase 5 — Scheduling/export
Manhole schedule + opening schedule + manufacturer-oriented CSV/Excel export.

### Phase 6 — Precast elevations
Generate wall elevation views/sheets W1-W4 with dimensions, opening IDs, offsets, and invert data.

## Build
Open `PrecastManholeManager.sln` in Visual Studio 2022 and build **Release | Any CPU**.

The project references Revit 2024 API DLLs from:
`C:\Program Files\Autodesk\Revit 2024\`

The post-build step copies the DLL and add-in manifest to:
`%APPDATA%\Autodesk\Revit\Addins\2024\PrecastManholeManager\`

## Phase 1 usage
1. Open the structural Revit model.
2. Ensure relevant MEP Revit links are loaded.
3. Hatco > Precast Tools > **Scan Manhole**.
4. Select the Structural Foundation/base of one manhole.
5. Review the modeless Opening Preview window.
6. Adjust clearance and accept/reject penetrations as required.
7. Export accepted penetrations if required.
8. Review Desktop outputs:
   - `PrecastManholeManager_yyyyMMdd_HHmmss.txt`
   - `PrecastManholePenetrations_yyyyMMdd_HHmmss.csv`

The TXT log is intentionally verbose and should be shared when a detection result is wrong; it includes document/link data, selected IDs, wall candidates, geometry, intersections, sizes, and exceptions.
