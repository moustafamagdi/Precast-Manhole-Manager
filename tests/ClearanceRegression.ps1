param(
    [string]$AssemblyPath = "$PSScriptRoot\..\src\PrecastManholeManager\bin\Release\net48\PrecastManholeManager.dll",
    [string]$RevitDirectory = 'C:\Program Files\Autodesk\Revit 2024'
)
$ErrorActionPreference = 'Stop'
# Pure calculation checks on the actual compiled classes; no model or Revit session is opened.
[void][Reflection.Assembly]::LoadFrom((Join-Path $RevitDirectory 'RevitAPI.dll'))
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath).Path)
$record = [Activator]::CreateInstance($assembly.GetType('Hatco.PrecastManholeManager.Models.PenetrationRecord'), $true)
$data = [Activator]::CreateInstance($assembly.GetType('Hatco.PrecastManholeManager.Services.ManagedOpeningData'), $true)
$matches = $assembly.GetType('Hatco.PrecastManholeManager.Services.CleanSyncAtomicService').GetMethod('Matches', [Reflection.BindingFlags]'NonPublic,Static')
function Assert-That([bool]$condition, [string]$message) {
    if (!$condition) { throw $message }
    Write-Output "PASS: $message"
}
$record.Shape = 'Round'
$record.DiameterMm = 500
$record.HostWallId = 123
$record.LinkedUniqueId = 'source-one'
$record.LinkInstanceId = 10
$record.ClearanceMm = 50
$key = $record.SourceKey
$data.HostWallId = 123
$data.ClearanceMm = 50
$data.CutWidthMm = 600
$data.CutHeightMm = 600
Assert-That ($record.CutWidthMm -eq 600 -and $record.CutHeightMm -eq 600) '50 mm per side produces a 600 x 600 opening for a 500 mm pipe'
Assert-That ($matches.Invoke($null, @($data, $record))) 'An unchanged opening is retained'
$record.ClearanceMm = 75
Assert-That ($record.CutWidthMm -eq 650 -and $record.CutHeightMm -eq 650) 'Increasing clearance expands both dimensions'
Assert-That (!$matches.Invoke($null, @($data, $record))) 'Increasing clearance triggers an update'
Assert-That ($record.SourceKey -eq $key) 'Changing clearance preserves source identity for matching, not duplication'
$record.ClearanceMm = 25
Assert-That ($record.CutWidthMm -eq 550 -and !$matches.Invoke($null, @($data, $record))) 'Decreasing clearance also triggers an update'
$record.ClearanceMm = 50.1
Assert-That (!$matches.Invoke($null, @($data, $record))) 'Sub-millimeter clearance changes are not silently ignored'
$record.ClearanceMm = 0
Assert-That ($record.CutWidthMm -eq 500) 'Zero clearance is supported'
$record.Shape = 'Rectangular'
$record.WidthMm = 300
$record.HeightMm = 200
$record.ClearanceMm = 25
Assert-That ($record.CutWidthMm -eq 350 -and $record.CutHeightMm -eq 250) 'Rectangular sources add clearance to each side'
Write-Output 'Calculation checks passed. Geometry, dialogs, rollback, and sheet refresh still require a Revit integration test.'

# Only failed joins entirely inside the selected manhole may be resolved.
$joinPolicy = $assembly.GetType('Hatco.PrecastManholeManager.Services.OpeningFailurePreprocessor').GetMethod('CanDetachJoin', [Reflection.BindingFlags]'NonPublic,Static')
function Test-JoinScope([bool]$known, [int[]]$affected, [int[]]$allowed, [int[]]$walls) {
    return [bool]$joinPolicy.Invoke($null, [object[]]@($known, $affected, $allowed, $walls))
}
$scope = [int[]]@(100, 101, 102, 103, 200)
$walls = [int[]]@(100, 101, 102, 103)
Assert-That (Test-JoinScope $true @(100,101) $scope $walls) 'A failed join between two selected walls can be resolved'
Assert-That (Test-JoinScope $true @(100,200) $scope $walls) 'A failed join between selected wall and foundation can be resolved'
Assert-That (!(Test-JoinScope $true @(100,999) $scope $walls)) 'A join involving an external element is rejected'
Assert-That (!(Test-JoinScope $true @(100,101,999) $scope $walls)) 'Additional related elements outside the scope prevent resolution'
Assert-That (!(Test-JoinScope $true @(100) $scope $walls)) 'An incomplete failure element list is rejected'
Assert-That (!(Test-JoinScope $true @() $scope $walls)) 'An empty failure element list is rejected'
Assert-That (!(Test-JoinScope $false @(100,101) $scope $walls)) 'Unknown failure kinds are not resolved as joins'
Assert-That (!(Test-JoinScope $true @(100,101) @() @())) 'Legacy callers without an explicit scope cannot detach joins'

$tableMatcher = $assembly.GetType('Hatco.PrecastManholeManager.Services.FirstProductionSheetService').GetMethod('IsLegacyTableText', [Reflection.BindingFlags]'NonPublic,Static')
function Test-TableText([string]$text) {
    return [bool]$tableMatcher.Invoke($null, [object[]]@($text))
}
$header = 'MH-001 | OPENING SETOUT - PRELIMINARY / VERIFY WALL MEP SOURCE CLEAR OPENING (mm) BOTTOM ABOVE BASE (mm)'
Assert-That (Test-TableText $header) 'Original generated opening table is recognized'
Assert-That (Test-TableText ($header.Replace(' ', "`r`n`t"))) 'Paragraph breaks and tabs do not prevent legacy table recognition'
Assert-That (Test-TableText ($header.Replace(' ', [string][char]0x00A0).ToLowerInvariant())) 'Non-breaking spaces and case changes are supported'
Assert-That (Test-TableText ($header.Replace('OPENING SETOUT - PRELIMINARY / VERIFY', 'PARTIAL LINK COVERAGE - NOT FOR ISSUE'))) 'Old partial-link tables can be migrated'
Assert-That (!(Test-TableText 'General note: check MEP SOURCE and BOTTOM ABOVE BASE (mm)')) 'Unrelated manual notes are not treated as opening tables'
Assert-That (!(Test-TableText '')) 'Empty notes are not treated as opening tables'

$dimensionCheck = $assembly.GetType('Hatco.PrecastManholeManager.Services.OpeningDimensionService').GetMethod('SegmentsMatch', [Reflection.BindingFlags]'NonPublic,Static')
function Test-DimensionSegments([double[]]$coordinates, [Nullable[double][]]$measured) {
    return [bool]$dimensionCheck.Invoke($null, [object[]]@($coordinates, $measured))
}
Assert-That (Test-DimensionSegments @(0,1,3) @(1,2)) 'Associated chain segments match the model face spacing'
Assert-That (Test-DimensionSegments @(-100,-99,-97) @(1,2)) 'Negative project coordinates preserve segment lengths'
Assert-That (!(Test-DimensionSegments @(0,1,3) @(1,1.5))) 'Incorrect reference measurements are rejected'
Assert-That (!(Test-DimensionSegments @(0,1,3) @(3))) 'A missing dimension segment is rejected'
Assert-That (!(Test-DimensionSegments @(0,1) @($null))) 'Unresolved dimension values are rejected'
Assert-That (!(Test-DimensionSegments @(1,0) @(1))) 'Unordered reference coordinates are rejected'
Assert-That (!(Test-DimensionSegments @(0,0) @(0))) 'Coincident references cannot masquerade as a valid dimension'
Assert-That (!(Test-DimensionSegments @(0,1) @([double]::NaN))) 'Non-finite measured values are rejected'
Assert-That (!(Test-DimensionSegments @(0,[double]::PositiveInfinity) @(1))) 'Non-finite model coordinates are rejected'

$planType = $assembly.GetType('Hatco.PrecastManholeManager.Services.CleanSyncPlan')
$plan = [Activator]::CreateInstance($planType, $true)
$blockers = $assembly.GetType('Hatco.PrecastManholeManager.Services.ProductionPreflightService').GetMethod('PhysicalBlockers', [Reflection.BindingFlags]'NonPublic,Static')
$plan.UnavailableLinks = 10
$plan.ManagedOpeningIds.Add('source', 123)
Assert-That ($blockers.Invoke($null, [object[]]@($plan)).Count -eq 0) 'Recheck accepts managed openings and ignores unloaded links'
$plan.Profiles.Add(1, 'EDITED PROFILE')
Assert-That ($blockers.Invoke($null, [object[]]@($plan)).Count -eq 1) 'An edited profile keeps the manhole in review'
$plan.Profiles[1] = 'NO EDITED SKETCH'
Assert-That ($blockers.Invoke($null, [object[]]@($plan)).Count -eq 0) 'Resetting the profile removes its blocker'
$plan.ManualOpeningIds.Add(99)
Assert-That ($blockers.Invoke($null, [object[]]@($plan)).Count -eq 1) 'Manual native openings still require review'
$plan.ManualOpeningIds.Clear()
$plan.BlockReason = 'Cannot verify wall audit'
Assert-That ($blockers.Invoke($null, [object[]]@($plan)).Count -eq 1) 'An inconclusive audit prevents resolving review'

$bodyJoin = $assembly.GetType('Hatco.PrecastManholeManager.Services.CleanSyncPlanService').GetMethod('IsLocalBodyJoin', [Reflection.BindingFlags]'NonPublic,Static')
function Test-BodyJoin([int]$wall, [int]$cutter, [int[]]$scope, [bool]$joined, [bool]$cutsWall) {
    return [bool]$bodyJoin.Invoke($null, [object[]]@($wall, $cutter, $scope, 200, $joined, $cutsWall))
}
Assert-That (Test-BodyJoin 100 101 @(100,101,102,103) $true $true) 'Verified join between selected manhole walls is preserved'
Assert-That (Test-BodyJoin 100 200 @(100,101,102,103) $true $true) 'Verified selected foundation join is preserved'
Assert-That (!(Test-BodyJoin 100 999 @(100,101,102,103) $true $true)) 'External cutter remains blocked even when joined'
Assert-That (!(Test-BodyJoin 100 101 @(100,101,102,103) $false $true)) 'Local solid cut without a geometry join remains blocked'
Assert-That (!(Test-BodyJoin 100 101 @(100,101,102,103) $true $false)) 'An inconsistent cut direction remains blocked'
Assert-That (!(Test-BodyJoin 999 100 @(100,101,102,103) $true $true)) 'A wall outside the selected manhole is rejected'
Assert-That (!(Test-BodyJoin 100 100 @(100,101,102,103) $true $true)) 'Self references are rejected'
Assert-That (!(Test-BodyJoin 100 101 @(100,101,102) $true $true)) 'Incomplete footprints cannot authorize local joins'
Assert-That (!(Test-BodyJoin 100 101 @(100,101,102,102) $true $true)) 'Duplicate walls cannot authorize local joins'

$layoutType = $assembly.GetType('Hatco.PrecastManholeManager.Services.BatchSheetLayoutService')
$nextSlot = $layoutType.GetMethod('NextSlotIndex', [Reflection.BindingFlags]'NonPublic,Static')
$page = $layoutType.GetMethod('Page', [Reflection.BindingFlags]'NonPublic,Static')
Assert-That ($nextSlot.Invoke($null,[object[]]@(,[int[]]@())) -eq 0) 'First batch starts at the first row'
Assert-That ($nextSlot.Invoke($null,[object[]]@(,[int[]]@(0,2,5))) -eq 6) 'New manholes append without filling reserved or missing earlier rows'
Assert-That ($page.Invoke($null,[object[]]@(5)) -eq 0) 'Sixth manhole retains the last row of the first sheet'
Assert-That ($page.Invoke($null,[object[]]@(6)) -eq 1) 'Seventh manhole starts the second sheet'
$order = $layoutType.GetMethod('ManholeOrder',[Reflection.BindingFlags]'NonPublic,Static')
Assert-That ($order.Invoke($null,[object[]]@('MH-1000')) -gt $order.Invoke($null,[object[]]@('MH-999'))) 'Sheet order remains numeric beyond MH-999'
$overlap = $assembly.GetType('Hatco.PrecastManholeManager.Services.LinkedMepScanCache').GetMethod('Overlaps',[Reflection.BindingFlags]'NonPublic,Static')
Assert-That ($overlap.Invoke($null,[object[]]@(-100.0,100.0,-1.0,1.0))) 'Spatial cache retains curves crossing the scan envelope with distant endpoints'
Assert-That ($overlap.Invoke($null,[object[]]@(-10.0,-5.0,-5.0,0.0))) 'Spatial cache includes touching boundaries at negative coordinates'
Assert-That (!$overlap.Invoke($null,[object[]]@(0.0,1.0,2.0,3.0))) 'Spatial cache rejects distant curves'

$plan.BlockReason = ''
$plan.SolidCutReviewReasons.Add('External cutter 901 on wall 100')
$plan.UnsupportedSolidCutWallIds.Add(100)
$reasons = $blockers.Invoke($null, [object[]]@($plan))
Assert-That ($reasons.Count -eq 1 -and $reasons[0].Contains('901')) 'Shared production and recheck blockers preserve exact cutter diagnostics without a duplicate generic reason'

$resultType = $assembly.GetType('Hatco.PrecastManholeManager.Models.ProductionManholeResult')
$result = [Activator]::CreateInstance($resultType, [object[]]@($true,$true,'Report mentions DIMENSION REVIEW as historical text'))
Assert-That ($result.Committed -and $result.DimensionsComplete) 'Historical wording in a report cannot turn successful dimensions into a batch failure'
$result = [Activator]::CreateInstance($resultType, [object[]]@($true,$false,'Localized report without an English status label'))
Assert-That ($result.Committed -and !$result.DimensionsComplete) 'Incomplete dimensions remain distinguishable from rolled-back geometry regardless of report language'
