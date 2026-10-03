$ErrorActionPreference = 'Stop'
$source = Get-Content "$PSScriptRoot/../src/PrecastManholeManager/Services/OverallPlanLabelPolicy.cs" -Raw
Add-Type ($source.Replace('internal static class','public static class').Replace('internal static','public static'))
function Assert-Equal($actual, $expected, $label) {
    if ($actual -ne $expected) { throw "$label : expected [$expected], got [$actual]" }
    Write-Output "PASS: $label"
}
$policy = [Hatco.PrecastManholeManager.Services.OverallPlanLabelPolicy]
Assert-Equal ($policy::FoundationId('MH_5079277_PROD_2D_PLAN')) 5079277 'Plan maps to foundation'
Assert-Equal ($policy::FoundationId('MH_5079277_PROD_2D_OUT_W4')) 5079277 'Exterior view maps to foundation'
Assert-Equal ($policy::FoundationId('MH_5079277_DRAFT_2D_PLAN')) -1 'Draft is not a production placement'
Assert-Equal ($policy::FoundationId('MH_5079277_PROD_2D_PLAN Copy 1')) -1 'Unowned copied name cannot certify placement'
Assert-Equal ($policy::Label('MH-170', [string[]]@('MH-BATCH-029'),5)) "MH-170`nSheet: MH-BATCH-029" 'Actual new sheet appears after manual move'
Assert-Equal ($policy::Label('MH-170', [string[]]@('MH-BATCH-030','MH-BATCH-029','MH-BATCH-030'),5)) "MH-170`nSPLIT - Sheets: MH-BATCH-029, MH-BATCH-030" 'Split placement is explicit and deduplicated'
Assert-Equal ($policy::Label('MH-171', [string[]]@(),0)) "MH-171`nNOT PLACED" 'No reserved-sheet fallback for unplaced views'
Assert-Equal ($policy::Label('MH-171', [string[]]@('CUSTOM-01'),1)) "MH-171`nSheet: CUSTOM-01 (PARTIAL 1/5)" 'Custom sheet number retained and missing placements flagged'
Assert-Equal ($policy::SheetName('Precast Manholes - 029')) 'Cast in site Manholes - 029' 'Batch title replacement'
Assert-Equal ($policy::SheetName('Cast in site Manholes - 029')) 'Cast in site Manholes - 029' 'Rename is idempotent'
Assert-Equal ($policy::SheetName('Special Manholes - 029')) 'Special Manholes - 029' 'Unrelated custom title preserved'
