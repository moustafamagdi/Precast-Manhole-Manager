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
