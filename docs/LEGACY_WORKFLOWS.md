# Precast Manhole Manager

> Historical workflow documentation retained for diagnostic commands and earlier milestones. For supported production behavior and Pipe/Duct scope, see the root README. References below to broader MEP categories or older sheet workflows do not describe the current project runner.

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

### Phase 3 — Create/update openings (current test build)
The modeless preview now includes **Create / Update Openings**.

- Uses a Revit `ExternalEvent` so model modifications are executed in a valid Revit API context.
- Creates native Revit wall `Opening` elements for accepted penetrations.
- Stores persistent source metadata on every managed opening using Extensible Storage.
- Re-running the same manhole classifies openings as:
  - CREATED
  - UPDATED
  - UNCHANGED
  - REMOVED
- A separate timestamped TXT log is written for every Phase 3 sync run.
- Unchecked/stale managed openings on the selected manhole walls are removed.
- Current limitation: Revit native wall Opening only supports rectangular openings. Round pipe penetrations therefore use a square rectangular envelope equal to the recommended opening diameter. A true round void-family implementation will replace this after the sync engine is validated.

### Phase 4 — Manhole data carrier (current)
The preview now includes a **Manhole No.** field and **Save Manhole Data**.

- Creates one tiny Generic Model `DirectShape` data carrier at the manhole center.
- Reuses the same carrier on later runs for the same Structural Foundation.
- Writes the manhole number into the carrier `Mark` parameter.
- Stores detailed manhole metadata using Extensible Storage:
  - Foundation ID / UniqueId
  - W1 / W2 / W3 / W4 element IDs
  - Base top elevation
  - Base thickness
  - Clear W1-W4 dimension
  - Clear W2-W3 dimension
  - Outer W1-W4 dimension
  - Outer W2-W3 dimension
  - Wall height
- Links all managed openings on the four manhole walls to the manhole.
- Numbers openings per wall as `O01`, `O02`, etc.
- Persists an opening code such as `MH-001-W2-O01`.
- Writes a separate timestamped Phase 4 diagnostic log.

### Phase 5 — Manufacturer export (current)
The preview now includes **Export Manufacturer Data**.

Exports all saved manholes in the active Revit document, not only the currently selected manhole.

Output folder:
`Desktop\Precast Manhole Manager\Manufacturer Exports\`

Files created per export run:

- `Manholes_Summary_yyyyMMdd_HHmmss.csv`
  - Manhole number
  - Foundation ID
  - Clear dimensions W1-W4 / W2-W3
  - Outer dimensions W1-W4 / W2-W3
  - Wall height
  - Base thickness
  - Base top elevation
  - W1/W2/W3/W4 host wall IDs

- `Manhole_Openings_yyyyMMdd_HHmmss.csv`
  - Manhole number
  - Wall number
  - Opening number and full opening code
  - Revit opening Element ID
  - Actual opening width and height
  - Managed vs Adopted Manual
  - Horizontal offset from the stable wall start
  - Invert from base
  - Absolute invert
  - Center elevation
  - Service category
  - System name
  - Family/type
  - Source Revit link
  - Linked source Element ID / UniqueId
  - Export status

Invert is resolved from the linked MEP element size when the source link is available. If the source cannot be resolved, the exporter falls back to the persisted opening/source geometry data.

A separate timestamped TXT diagnostic log is also created in the normal `Logs` folder.

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
7. Optionally export accepted penetrations.
8. Click **Create / Update Openings** to sync accepted rows into the Revit walls.
9. Review the Phase 3 result in the window footer and its timestamped TXT log.
10. Review Desktop outputs:
   - `PrecastManholeManager_yyyyMMdd_HHmmss.txt`
   - `PrecastManholePenetrations_yyyyMMdd_HHmmss.csv`

The TXT log is intentionally verbose and should be shared when a detection result is wrong; it includes document/link data, selected IDs, wall candidates, geometry, intersections, sizes, and exceptions.


## Phase 4 test
1. Scan a manhole that already has managed Phase 3 openings.
2. In the preview, enter a meaningful manhole number such as `MH-001`.
3. Click **Save Manhole Data**.
4. Confirm the footer reports:
   - Carrier element ID
   - Number of linked openings
5. Re-scan the same foundation and confirm the same manhole number is prefilled.
6. Re-save and confirm no duplicate data carrier is created.


## Phase 5 test
1. Make sure at least one manhole has been saved with **Save Manhole Data**.
2. Make sure its openings are managed/adopted and linked to that manhole.
3. Click **Export Manufacturer Data**.
4. Confirm two CSV files are created under:
   `Desktop\Precast Manhole Manager\Manufacturer Exports\`
5. Check the opening schedule against Revit:
   - Manhole No.
   - W1/W2/W3/W4
   - Opening size
   - Offset
   - Invert from base
   - Source service
6. Share the Phase 5 TXT log if any row is missing or incorrect.


## Batch processing

Two additional commands are available under **Hatco > Precast Tools**:

### Batch Selected
- Select multiple Structural Foundations manually.
- Processes only those selected elements.
- Existing saved manhole numbers are preserved.
- New manholes are auto-numbered as `MH-001`, `MH-002`, etc.
- Each manhole is processed in its own Revit transaction.
- Geometry warnings cause that manhole to be skipped as **NEEDS REVIEW** while the batch continues.

### Batch All
- Automatically finds Structural Foundations whose element/type naming suggests a manhole:
  - `MANHOLE`
  - `_MH`
  - `MH_`
  - `PRECAST ... MH`
- Shows a confirmation dialog before modifying the model.
- Processes valid manholes in a deterministic spatial order.
- Preserves existing saved manhole numbers and continues numbering new ones.

### Batch workflow
For each valid manhole the batch:
1. Detects the four walls.
2. Scans linked MEP penetrations.
3. Classifies existing manual openings.
4. Leaves sufficient manual openings untouched.
5. Creates/updates/removes only add-in-managed openings.
6. Never auto-modifies adopted manual openings that need review.
7. Creates/updates the manhole data carrier.
8. Links managed openings to the saved manhole.
9. Writes one consolidated timestamped TXT batch log.

The final dialog reports:
- Foundations selected
- Valid / Needs Review / Failed
- Penetrations found
- Manual sufficient / too-small openings
- Openings created / updated / unchanged / removed
- Opening review count
- Data carriers saved
- Openings linked


## Manufacturer Excel workbook

Use **Hatco > Precast Tools > Export Excel** after saving or batch-processing manholes.

The command creates:
`Desktop\Precast Manhole Manager\Manufacturer Exports\Precast_Manholes_yyyyMMdd_HHmmss.xlsx`

Workbook structure:

### MANHOLES
Project-wide manhole summary:
- Manhole No.
- Foundation ID
- Clear dimensions
- Outer dimensions
- Wall height
- Base thickness
- Base top elevation
- W1/W2/W3/W4 IDs
- Opening count

### OPENINGS
Project-wide opening schedule:
- Manhole
- Wall
- Opening number/code
- Opening width/height
- Offset
- Invert from base
- Absolute invert
- Center elevation
- Managed / Adopted Manual
- Service/system/type
- Opening Element ID
- Source link / source Element ID / UniqueId
- Status

### One sheet per manhole
Each saved manhole gets its own formatted sheet containing:
- General manhole data
- Clear/outer dimensions
- Height and base thickness
- Opening count
- W1, W2, W3, W4 sections
- Opening code
- Size
- Offset
- Invert from base
- Absolute invert
- Service/system/source

The XLSX writer is dependency-free and does not require Microsoft Excel or third-party NuGet packages on the Revit workstation.
