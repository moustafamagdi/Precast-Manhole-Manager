# Interface navigation

The manager groups actions by task. Openings, Dimensions and Base Repair use an Apply to selector followed by one Run button. Scope defaults to selected row or explicit picking, never all manholes. Existing confirmation, clearance validation and execution services remain unchanged.

| Tab | Actions |
| --- | --- |
| Openings | Selected row, pick bases, current model view, all prepared |
| Dimensions | Selected row or all prepared |
| Base Repair | Pick bases or current model view |
| Views & Sheets | Views selected, generate selected, generate/update all |
| Scan & Review | Scan, clean scan, recheck, inspect, review 3D |
| Setup & Advanced | Internal IDs, Excel export, test sheet and performance diagnostics |

The table supports searching MH names as well as foundation IDs, reasons and types. The heading is Manhole Manager. Performance diagnostics apply only to Generate / Update All. Existing documentation referring to Openings - Pick Bases or Repair Low Base - View corresponds to selecting that scope in the relevant task tab.

Validation: compiled against Revit 2024; WPF layout instantiated with sample data and rendered offscreen at normal and minimum window sizes. No production model was edited during the UI check.

## Review 3Ds for recorded issues

Scan & Review > 3D - All Review Cases creates/updates a review 3D for each OPEN issue belonging to an existing foundation UniqueId in the current model's registry. It reuses saved review views, uses MH_3D with a 350 mm margin and names views by manhole, issue category and foundation ID. The CSV includes the detailed reason and view ID/name. Each view has its own transaction; failures do not stop other items. Stop/checkpoint saving uses the current RVT, without SaveAs or synchronize.

This is a visualization of recorded issues, not a new diagnostic scan. Use Clean Scan first to refresh geometry issues. Failed opening/dimension/base-repair runs now register their outcomes, including the selected-dimension action. Historical CSV-only failures from older builds are not automatically imported because those CSVs lack model identity; rerun the relevant check to record them safely. Creating a view does not resolve its issue.
