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

$attribute = $assembly.GetType('Hatco.PrecastManholeManager.Infrastructure.PerformanceMeasurement').GetMethod('CanAttribute', [Reflection.BindingFlags]'NonPublic,Static')
Assert-That ($attribute.Invoke($null,[object[]]@('Viewport.Create',$true))) 'Diagnostic regeneration is enabled for a viewport call inside a transaction'
Assert-That (!$attribute.Invoke($null,[object[]]@('Viewport.Create',$false))) 'Diagnostic regeneration cannot run outside a transaction'
Assert-That (!$attribute.Invoke($null,[object[]]@('Transaction.Commit.Documentation',$true))) 'Commit timing never triggers post-commit regeneration'
Assert-That (!$attribute.Invoke($null,[object[]]@('Document.Save.Checkpoint',$true))) 'Save timing does not inject regeneration'
Assert-That (!$attribute.Invoke($null,[object[]]@('Regenerate.BeforeRow',$true))) 'Explicit regeneration is not recursively instrumented'

$annotationAxis = $assembly.GetType('Hatco.PrecastManholeManager.Services.OpeningDimensionService').GetMethod('WithinAnnotationAxis', [Reflection.BindingFlags]'NonPublic,Static')
Assert-That ($annotationAxis.Invoke($null,[object[]]@(11.0,0.0,10.0,0.1,0.1,25))) 'Annotation outside model crop remains valid within scaled annotation margin'
Assert-That (!$annotationAxis.Invoke($null,[object[]]@(13.0,0.0,10.0,0.1,0.1,25))) 'Annotation outside annotation crop remains rejected'
Assert-That ($annotationAxis.Invoke($null,[object[]]@(-2.5,0.0,10.0,0.1,0.2,25))) 'Annotation lower boundary uses paper units times view scale'
Assert-That (!$annotationAxis.Invoke($null,[object[]]@(-3.0,0.0,10.0,0.1,0.2,25))) 'Asymmetric annotation margins do not swap lower and upper boundaries'

$candidateType = $assembly.GetType('Hatco.PrecastManholeManager.Services.VirtualMepCandidate')
$candidate = [Activator]::CreateInstance($candidateType, $true)
$eligible = $candidateType.GetProperty('EligibleForProduction', [Reflection.BindingFlags]'NonPublic,Instance')
$candidate.ConnectorVerified = $true
$candidate.Status = 'REVIEW'
$candidate.ProjectedWidthMm = 705
$candidate.ProjectedHeightMm = 700
$candidate.GapToFaceMm = 0
Assert-That ($eligible.GetValue($candidate)) 'Verified end connector touching the wall is eligible'
$candidate.Status = 'INSIDE_WALL_REVIEW'
$candidate.DepthInsideWallMm = 25
Assert-That ($eligible.GetValue($candidate)) 'Verified connector 25 mm inside wall no longer requires a mid-plane crossing'
$candidate.GapToFaceMm = 150
Assert-That ($eligible.GetValue($candidate)) 'Endpoint at the agreed 150 mm face gap is eligible'
$candidate.GapToFaceMm = 150.01
Assert-That (!$eligible.GetValue($candidate)) 'Endpoint beyond the agreed gap remains deferred'
$candidate.GapToFaceMm = 0
$candidate.Status = 'AMBIGUOUS REVIEW'
Assert-That (!$eligible.GetValue($candidate)) 'An endpoint near several eligible walls cannot authorize a cut'
$candidate.Status = 'REVIEW'
$candidate.ConnectorVerified = $false
Assert-That (!$eligible.GetValue($candidate)) 'An unverified curve endpoint cannot authorize a connector-based cut'
$candidate.ConnectorVerified = $true
$candidate.DeviationDeg = 26
Assert-That ($eligible.GetValue($candidate)) 'The observed 26-degree duct approach is eligible'
$candidate.DeviationDeg = 45
Assert-That ($eligible.GetValue($candidate)) 'Exactly 45 degrees is included'
$candidate.DeviationDeg = 45.01
Assert-That (!$eligible.GetValue($candidate)) 'An approach beyond 45 degrees remains deferred'
$candidate.DeviationDeg = 0
$candidate.ProjectedWidthMm = [double]::NaN
Assert-That (!$eligible.GetValue($candidate)) 'Invalid projected envelope cannot authorize a cut'

$extent = $assembly.GetType('Hatco.PrecastManholeManager.Services.VirtualMepExtensionScanner').GetMethod('ProjectedExtent', [Reflection.BindingFlags]'NonPublic,Static')
Assert-That ($extent.Invoke($null,[object[]]@($true,250.0,0.0,0.0)) -eq 500) 'Normal round connector retains its diameter'
Assert-That ($extent.Invoke($null,[object[]]@($true,150.0,200.0,20.0)) -eq 520) 'Round envelope includes oblique projection and wall thickness travel'
Assert-That ($extent.Invoke($null,[object[]]@($false,-150.0,200.0,-20.0)) -eq 720) 'Rotated rectangular envelope includes both connector axes and signed wall travel'
$record.ProjectedWidthMm = 705
$record.ProjectedHeightMm = 700
$record.ClearanceMm = 50
Assert-That ($record.CutWidthMm -eq 805 -and $record.CutHeightMm -eq 800) 'Verified duct endpoint envelope receives clearance on both sides'
$record.ClearanceMm = 75
Assert-That ($record.CutWidthMm -eq 855 -and $record.CutHeightMm -eq 850 -and $record.SourceKey -eq $key) 'Endpoint clearance updates preserve the source key and projected size'

$repairType = $assembly.GetType('Hatco.PrecastManholeManager.Services.ManholeBaseRepairService')
$dropMethod = $repairType.GetMethod('RequiredDrop', [Reflection.BindingFlags]'NonPublic,Static')
$lowerMethod = $repairType.GetMethod('IsLowerFailure', [Reflection.BindingFlags]'NonPublic,Static')
Assert-That ($dropMethod.Invoke($null,[object[]]@(1000.0,950.0)) -eq 150) 'Base drops enough for an opening 50 mm below it plus a 100 mm clear gap'
Assert-That ($dropMethod.Invoke($null,[object[]]@(1000.0,1002.0)) -eq 98) 'Opening within the wall edge margin receives the full 100 mm base separation'
Assert-That ($dropMethod.Invoke($null,[object[]]@(1000.0,1100.0)) -eq 0) 'Already satisfied 100 mm gap never lowers the base again'
Assert-That ($dropMethod.Invoke($null,[object[]]@(-1000.0,-1050.0)) -eq 150) 'Repair works with negative project elevations'
Assert-That ($lowerMethod.Invoke($null,[object[]]@(950.0,1550.0,1000.0,2000.0))) 'Lower-wall-only fit failure can be repaired'
Assert-That (!$lowerMethod.Invoke($null,[object[]]@(1100.0,2100.0,1000.0,2000.0))) 'Upper-wall failure cannot authorize base lowering'
Assert-That (!$lowerMethod.Invoke($null,[object[]]@(950.0,2100.0,1000.0,2000.0))) 'Opening spanning below and above the wall requires manual review'
Assert-That (!$lowerMethod.Invoke($null,[object[]]@(1100.0,1550.0,1000.0,2000.0))) 'A fitting opening alone cannot trigger a repair'

$baseOffset = $repairType.GetMethod('BaseOffset', [Reflection.BindingFlags]'NonPublic,Static')
Assert-That ([Math]::Abs($baseOffset.Invoke($null,[object[]]@(583846.824,585600.0)) + 1753.176) -lt 0.00001) 'Base offset uses the target and level in the same project coordinate frame'
Assert-That ($baseOffset.Invoke($null,[object[]]@(-150.0,200.0)) -eq -350) 'Base offset handles levels and targets on opposite sides of project zero'

# Copied foundations retain legacy names; allocation must preserve originals
# and reserve every existing number before assigning duplicates/new bases.
$planType = $assembly.GetType('Hatco.PrecastManholeManager.Services.ManholeNumberingPlan')
$rowType = $assembly.GetType('Hatco.PrecastManholeManager.Services.ManholeNumberingRow')
$numberPlan = [Activator]::CreateInstance($planType, $true)
foreach ($pair in @(@(30,'MH-148'), @(10,'MH-148'), @(40,''), @(20,'MH-001'), @(50,'mh-148'))) {
    $row = [Activator]::CreateInstance($rowType, $true)
    $row.FoundationId = $pair[0]
    $row.PreviousName = $pair[1]
    $numberPlan.Rows.Add($row)
}
$allocator = $assembly.GetType('Hatco.PrecastManholeManager.Services.ManholeNumberingService').GetMethod('AllocateNames', [Reflection.BindingFlags]'NonPublic,Static')
[void]$allocator.Invoke($null, @($numberPlan))
$original = $numberPlan.Rows | Where-Object FoundationId -eq 10
$copy = $numberPlan.Rows | Where-Object FoundationId -eq 30
$newBase = $numberPlan.Rows | Where-Object FoundationId -eq 40
Assert-That ($original.ProposedName -eq 'MH-148' -and !$original.NewNumber) 'Earliest legacy foundation retains its internal identity regardless of input order'
Assert-That ($copy.ProposedName -eq 'MH-002' -and $copy.NewNumber) 'Copied name gets a fresh number without taking an existing number'
Assert-That ($newBase.ProposedName -eq 'MH-003') 'Unnumbered bases and copies share one collision-free allocation'
Assert-That (@($numberPlan.Rows.ProposedName | Sort-Object -Unique).Count -eq 5) 'Case-insensitive duplicates are resolved to unique names'
$firstAllocation = $numberPlan.Rows.ProposedName -join '|'
[void]$allocator.Invoke($null, @($numberPlan))
Assert-That (($numberPlan.Rows.ProposedName -join '|') -eq $firstAllocation) 'Repeating the same numbering preview is deterministic'
foreach ($row in $numberPlan.Rows) { $row.PreviousName = $row.ProposedName }
[void]$allocator.Invoke($null, @($numberPlan))
Assert-That ($numberPlan.NewlyNumbered -eq 0) 'Already repaired identities stay unchanged on subsequent runs'

# Compound cuts exercise the compiled grouping/union implementation (no Revit session).
$compound = $assembly.GetType('Hatco.PrecastManholeManager.Services.CompoundOpeningService')
$combine = $compound.GetMethod('Combine', [Reflection.BindingFlags]'NonPublic,Static')
$contains = $compound.GetMethod('Contains', [Reflection.BindingFlags]'NonPublic,Static')
$penetrationType = $assembly.GetType('Hatco.PrecastManholeManager.Models.PenetrationRecord')
function New-Cut([string]$key, [double]$x, [double]$z, [double]$w=200, [double]$h=200, [int]$wall=100) {
    $r = [Activator]::CreateInstance($penetrationType, $true)
    $r.SourceKeyOverride=$key; $r.HostWallId=$wall; $r.WallNumber=1
    $r.Xmm=$x; $r.Zmm=$z; $r.Shape='Rectangular'; $r.ClearanceMm=50
    $r.WidthMm=$w-100; $r.HeightMm=$h-100
    return $r
}
function Combine-Cuts($cuts, [bool]$enabled=$true, [double]$dx=1, [double]$dy=0) {
    $typed = [Array]::CreateInstance($penetrationType, $cuts.Count)
    for ($i=0; $i -lt $cuts.Count; $i++) { $typed.SetValue($cuts[$i],$i) }
    return ,$combine.Invoke($null, [object[]]@($typed,$dx,$dy,$enabled))
}
$aCut=New-Cut 'A' 0 0
$bCut=New-Cut 'B' 150 50
$merged=Combine-Cuts @($aCut,$bCut)
Assert-That ($merged.Count -eq 1 -and $merged[0].CutWidthMm -eq 350 -and $merged[0].CutHeightMm -eq 250) 'Overlapping cuts create one enclosing rectangle without double clearance'
Assert-That ($merged[0].Xmm -eq 75 -and $merged[0].Zmm -eq 25) 'Combined cut is centered on union bounds'
Assert-That ($merged[0].MemberSourceKeys.Count -eq 2 -and $merged[0].SourceKey.StartsWith('GROUP|100|')) 'Combined identity retains both source keys'
$keyBefore=$merged[0].SourceKey
$reversed=Combine-Cuts @($bCut,$aCut)
Assert-That ($reversed[0].SourceKey -eq $keyBefore) 'Combined identity does not depend on scan order'
$aCut.ClearanceMm=75; $bCut.ClearanceMm=75
$resized=Combine-Cuts @($aCut,$bCut)
Assert-That ($resized[0].SourceKey -eq $keyBefore -and $resized[0].CutWidthMm -eq 400 -and $resized[0].CutHeightMm -eq 300) 'Clearance update resizes the group while keeping its identity'
$aCut.ClearanceMm=50; $bCut.ClearanceMm=50
$third=New-Cut 'C' 300 0
$chain=Combine-Cuts @($aCut,$bCut,$third)
Assert-That ($chain.Count -eq 1 -and $chain[0].MemberSourceKeys.Count -eq 3 -and $chain[0].CutWidthMm -eq 500) 'Transitive overlaps merge all three sources'
$separate=Combine-Cuts @($aCut,(New-Cut 'D' 500 0))
Assert-That ($separate.Count -eq 2) 'Separated cuts remain separate'
$otherWall=Combine-Cuts @($aCut,(New-Cut 'E' 0 0 200 200 101))
Assert-That ($otherWall.Count -eq 2) 'Coincident cuts on different walls never merge'
$rejected=$false
try { [void](Combine-Cuts @($aCut,$bCut) $false) } catch { $rejected=$true }
Assert-That $rejected 'Disabling merge rejects an overlapping wall instead of cutting overlapping holes'
$touching=Combine-Cuts @($aCut,(New-Cut 'F' 200 0))
Assert-That ($touching.Count -eq 1) 'Touching cuts combine under the existing 5 mm separation rule'
$ra=New-Cut 'RA' 0 0; $ra.Ymm=0
$rb=New-Cut 'RB' 0 50; $rb.Ymm=150
$rotated=Combine-Cuts @($ra,$rb) $true 0 1
Assert-That ($rotated[0].CutWidthMm -eq 350 -and $rotated[0].Ymm -eq 75 -and $rotated[0].Xmm -eq 0) 'Union follows a wall aligned along Y'
Assert-That ($contains.Invoke($null,[object[]]@($merged[0],$aCut,1.0,0.0))) 'Combined bounds contain the first member for clean-scan validation'
Assert-That (!$contains.Invoke($null,[object[]]@($aCut,$merged[0],1.0,0.0))) 'A smaller stale opening cannot cover a combined envelope'
$invalid=New-Cut 'Bad' 0 0; $invalid.Xmm=[double]::NaN
$rejected=$false
try { [void](Combine-Cuts @($invalid)) } catch { $rejected=$true }
Assert-That $rejected 'Invalid coordinates are rejected before opening creation'
$authorize = $compound.GetMethod('AuthorizeReplacement', [Reflection.BindingFlags]'NonPublic,Static')
Assert-That ($authorize.Invoke($null,[object[]]@([string[]]@('A','B'),[string[]]@('A','B'),$true))) 'A known combined opening can be replaced only with full source coverage'
Assert-That (!$authorize.Invoke($null,[object[]]@([string[]]@('A','B'),[string[]]@('C'),$true))) 'Unmatched managed groups are preserved'
$rejected=$false
try { [void]$authorize.Invoke($null,[object[]]@([string[]]@('A','B'),[string[]]@('A'),$true)) } catch { $rejected=$true }
Assert-That $rejected 'An unloaded or missing member prevents replacing its combined opening'
$rejected=$false
try { [void]$authorize.Invoke($null,[object[]]@([string[]]@('A','B'),[string[]]@('A','B'),$false)) } catch { $rejected=$true }
Assert-That $rejected 'Turning merge off preserves existing combined cuts for review'
# The union can hit a third rectangle outside both initial members.
$l1=New-Cut 'L1' 0 0
$l2=New-Cut 'L2' 150 150
$l3=New-Cut 'L3' -75 275 120 120
$lMerge=Combine-Cuts @($l1,$l2,$l3)
Assert-That ($lMerge.Count -eq 1 -and $lMerge[0].MemberSourceKeys.Count -eq 3) 'Grouping repeats after union expands into another opening'

# Partition preserves independent candidates and old combined-cut dependencies.
$partition=$compound.GetMethod('Partition',[Reflection.BindingFlags]'NonPublic,Static')
function Partition-Cuts($cuts, [string[][]]$old=@()) {
    $typed=[Array]::CreateInstance($penetrationType,$cuts.Count)
    for($i=0;$i -lt $cuts.Count;$i++){ $typed.SetValue($cuts[$i],$i) }
    return ,$partition.Invoke($null,[object[]]@($typed,1.0,0.0,$old))
}
$parts=Partition-Cuts @((New-Cut 'BadEdge' -1.2 0 214.3 214.3),(New-Cut 'Good1' 450 0),(New-Cut 'Good2' 800 0))
Assert-That ($parts.Count -eq 3) 'An edge failure is isolated from two independent openings on the same wall'
$parts=Partition-Cuts @((New-Cut 'A' 0 0),(New-Cut 'B' 150 0),(New-Cut 'C' 800 0))
Assert-That ($parts.Count -eq 2 -and $parts[0].Count -eq 2) 'An overlapping pair is one transaction component; a distant cut is independent'
$parts=Partition-Cuts @((New-Cut 'A' 0 0),(New-Cut 'B' 800 0)) ([string[][]]@(,[string[]]@('A','B')))
Assert-That ($parts.Count -eq 1) 'Members of an existing combined opening stay in one rollback group even after moving apart'
$parts=Partition-Cuts @($l1,$l2,$l3)
Assert-That ($parts.Count -eq 1) 'Union expansion keeps dependent cuts in the same isolated group'
$fit=$assembly.GetType('Hatco.PrecastManholeManager.Services.OpeningFitValidationService').GetMethod('HorizontalFits',[Reflection.BindingFlags]'NonPublic,Static')
function Test-Horizontal([double]$center,[double]$width,[double]$length,[bool]$left,[bool]$right){ return [bool]$fit.Invoke($null,[object[]]@($center,$width,$length,$left,$right)) }
Assert-That (!(Test-Horizontal -1.2 214.3 1100 $false $false)) 'Ordinary openings cannot bypass the wall-end margin'
Assert-That (Test-Horizontal -1.2 214.3 1100 $true $false) 'Verified shared corner can cross the start of W2'
Assert-That (Test-Horizontal 1098.8 214.3 1100 $false $true) 'Verified shared corner can cross the end of W4'
Assert-That (!(Test-Horizontal -1.2 214.3 1100 $false $true)) 'Corner permission for the opposite end cannot authorize a cut'
Assert-That (!(Test-Horizontal -500 214.3 1100 $true $false)) 'A cut wholly outside the wall is rejected even with corner permission'
Assert-That (!(Test-Horizontal 550 1200 1100 $true $true)) 'Corner qualification cannot remove the complete wall width'
Assert-That (Test-Horizontal 550 200 1100 $false $false) 'Normal in-wall openings remain valid'
$near=$assembly.GetType('Hatco.PrecastManholeManager.Services.CornerOpeningService').GetMethod('NearEnd',[Reflection.BindingFlags]'NonPublic,Static')
Assert-That ($near.Invoke($null,[object[]]@(-1.2,214.3,1100.0,0))) 'Corner candidate must intersect its physical end'
Assert-That (!$near.Invoke($null,[object[]]@(550.0,214.3,1100.0,0))) 'Crossings midway along adjacent walls do not qualify as one corner'
$aCut.CornerStartAllowed=$true
$cornerMerge=Combine-Cuts @($aCut,$bCut)
Assert-That ($cornerMerge[0].CornerStartAllowed -and !$cornerMerge[0].CornerEndAllowed) 'Merging preserves only the qualified wall-end permission'

$edge=$assembly.GetType('Hatco.PrecastManholeManager.Services.DuctEdgeOpeningService').GetMethod('AdjustedCenter',[Reflection.BindingFlags]'NonPublic,Static')
function Edge-Center([double]$c,[double]$w,[double]$l){return [double]$edge.Invoke($null,[object[]]@($c,$w,$l))}
Assert-That ((Edge-Center -1.2 500 1100) -eq 250) 'Duct at wall start shifts inward while preserving full 500 mm opening width'
Assert-That ((Edge-Center 1098.8 500 1100) -eq 850) 'Duct at wall end shifts in the opposite direction without reducing clearance'
Assert-That ((Edge-Center 550 500 1100) -eq 550) 'Centered duct openings are not moved'
Assert-That ([double]::IsNaN((Edge-Center 550 1200 1100))) 'Oversized duct opening cannot be fixed by shifting'
Assert-That ([double]::IsNaN((Edge-Center -500 500 1100))) 'A duct opening entirely outside the wall is not relocated arbitrarily'
Assert-That ((Edge-Center 252 500 1100) -eq 250) 'A sub-5-mm wall-end sliver is replaced by an edge-aligned opening'
$fitted=New-Cut 'Fitted' 0 0 500 500
$fitted.FittedCenterXmm=250
$fittedData=[Activator]::CreateInstance($assembly.GetType('Hatco.PrecastManholeManager.Services.ManagedOpeningData'),$true)
$fittedData.HostWallId=100; $fittedData.ClearanceMm=50; $fittedData.CutWidthMm=500; $fittedData.CutHeightMm=500; $fittedData.Xmm=0
Assert-That (!$matches.Invoke($null,@($fittedData,$fitted))) 'Existing opening at the original duct center must update to its fitted position'
$fittedData.Xmm=250
Assert-That ($matches.Invoke($null,@($fittedData,$fitted))) 'Rerun reuses an opening already at the fitted position'

# Shared corner envelopes are clipped to physical wall extents, not rejected
# merely because their complete projected width exceeds the wall length.
$cornerType=$assembly.GetType('Hatco.PrecastManholeManager.Services.CornerOpeningService')
$clip=$cornerType.GetMethod('Clip',[Reflection.BindingFlags]'NonPublic,Static')
$w1=$clip.Invoke($null,[object[]]@(1196.8,1293.4,1200.0,$false,$true))
Assert-That ($null -ne $w1 -and [Math]::Abs($w1[1]-649.9) -lt 0.001 -and [Math]::Abs($w1[0]+$w1[1]/2-1200) -lt 0.001) 'Observed W1 corner retains the 649.9 mm portion inside the 1200 mm wall'
$w2=$clip.Invoke($null,[object[]]@(-2.6,1038.0,1200.0,$true,$false))
Assert-That ($null -ne $w2 -and [Math]::Abs($w2[1]-516.4) -lt 0.001 -and [Math]::Abs($w2[0]-$w2[1]/2) -lt 0.001) 'Observed W2 corner starts at the wall edge with its full in-wall portion'
Assert-That ($near.Invoke($null,[object[]]@(1196.8,1293.4,1200.0,1))) 'Projected width larger than wall length can qualify when only one wall end is crossed'
Assert-That ($null -eq $clip.Invoke($null,[object[]]@(600.0,1300.0,1200.0,$true,$true))) 'A corner envelope covering the complete wall remains blocked'
Assert-That ($null -eq $clip.Invoke($null,[object[]]@(1196.8,1293.4,1200.0,$true,$false))) 'Permission on the opposite wall end cannot authorize clipping'
Assert-That ($null -eq $clip.Invoke($null,[object[]]@(-800.0,700.0,1200.0,$true,$false))) 'A pipe wholly outside the wall cannot create a clipped cut'
$repeat=$clip.Invoke($null,[object[]]@([double]$w1[0],[double]$w1[1],1200.0,$false,$true))
Assert-That ([Math]::Abs($repeat[0]-$w1[0]) -lt 0.001 -and [Math]::Abs($repeat[1]-$w1[1]) -lt 0.001) 'Corner clipping is stable on repeated evaluation'
$slab=$cornerType.GetMethod('TraversesSlab',[Reflection.BindingFlags]'NonPublic,Static')
Assert-That ($slab.Invoke($null,[object[]]@(500.0,1000.0,0.65,200.0))) 'Finite oblique pipe spanning the wall slab can supply an adjacent corner candidate'
Assert-That (!$slab.Invoke($null,[object[]]@(1100.0,1000.0,0.65,200.0))) 'Infinite-axis intersection beyond the pipe end is rejected'
Assert-That (!$slab.Invoke($null,[object[]]@(80.0,1000.0,0.65,200.0))) 'Partial source slab coverage is left for review instead of extrapolating the pipe'
Assert-That (!$slab.Invoke($null,[object[]]@(500.0,1000.0,0.0,200.0))) 'Parallel pipe cannot supply a projected adjacent wall cut'
Assert-That (!$slab.Invoke($null,[object[]]@([double]::NaN,1000.0,0.65,200.0))) 'Invalid source station cannot authorize a corner cut'

# Physical opening coverage resolves overlap even for older individual cuts.
$coversPair=$compound.GetMethod('CoversPair',[Reflection.BindingFlags]'NonPublic,Static')
function Test-PairCoverage($cuts,$a,$b) {
    $typed=[Array]::CreateInstance($penetrationType,$cuts.Count)
    for($i=0;$i -lt $cuts.Count;$i++){ $typed.SetValue($cuts[$i],$i) }
    return $coversPair.Invoke($null,[object[]]@($typed,$a,$b,1.0,0.0))
}
$pa=New-Cut 'pair-a' 0 0
$pb=New-Cut 'pair-b' 150 0
Assert-That (Test-PairCoverage @((New-Cut 'old-a' 0 0),(New-Cut 'old-b' 150 0)) $pa $pb) 'Already executed individual cuts resolve their overlap review'
Assert-That (Test-PairCoverage @((New-Cut 'old-combined' 75 0 350 200)) $pa $pb) 'A physical combined cut resolves overlap without relying on member metadata'
Assert-That (!(Test-PairCoverage @((New-Cut 'old-a' 0 0)) $pa $pb)) 'A missing second cut still requires overlap review'
Assert-That (!(Test-PairCoverage @((New-Cut 'small' 75 0 300 200)) $pa $pb)) 'An undersized combined cut does not resolve overlap'
Assert-That (!(Test-PairCoverage @((New-Cut 'wrong-wall' 75 0 350 200 101)) $pa $pb)) 'Coverage on another wall cannot resolve overlap'

$coverage=$assembly.GetType('Hatco.PrecastManholeManager.Services.OpeningCoverageService').GetMethod('Covers',[Reflection.BindingFlags]'NonPublic,Static')
function Test-OpeningCoverage($cuts,$required) {
    $typed=[Array]::CreateInstance($penetrationType,$cuts.Count)
    for($i=0;$i -lt $cuts.Count;$i++){ $typed.SetValue($cuts[$i],$i) }
    return $coverage.Invoke($null,[object[]]@($typed,$required,1.0,0.0))
}
$required=New-Cut 'required' 100 50 300 300
Assert-That (!(Test-OpeningCoverage @() $required)) 'A service without a physical opening is missing'
Assert-That (Test-OpeningCoverage @((New-Cut 'existing' 100 50 300 300)) $required) 'An actual opening at the required size and position is sufficient'
Assert-That (!(Test-OpeningCoverage @((New-Cut 'old-location' -300 50 300 300)) $required)) 'A displaced old opening does not clear Missing Opening'
Assert-That (!(Test-OpeningCoverage @((New-Cut 'old-clearance' 100 50 280 300)) $required)) 'Insufficient physical clearance remains Missing Opening'
Assert-That (Test-OpeningCoverage @((New-Cut 'combined' 175 50 500 300)) $required) 'A larger combined cut covers its member without source-key matching'
Assert-That (!(Test-OpeningCoverage @((New-Cut 'wrong-wall' 100 50 300 300 101)) $required)) 'An opening on another wall cannot clear Missing Opening'
$required.FittedCenterXmm=150; $required.CutWidthOverrideMm=200
Assert-That (Test-OpeningCoverage @((New-Cut 'clipped-corner' 150 50 200 300)) $required) 'Corner coverage uses the clipped width and fitted center'

$presentation=$assembly.GetType('Hatco.PrecastManholeManager.Services.ManholeViewPresentationService')
$ownsMarker=$presentation.GetMethod('IsOwnSection',[Reflection.BindingFlags]'Public,Static')
function Test-OwnMarker($plan,$marker) { return $ownsMarker.Invoke($null,[object[]]@($plan,$marker)) }
1..4 | ForEach-Object { Assert-That (Test-OwnMarker 'MH_5503857_PROD_2D_PLAN' "MH_5503857_PROD_2D_OUT_W$_") "Own W$_ marker is retained" }
Assert-That (!(Test-OwnMarker 'MH_5503857_PROD_2D_PLAN' 'MH_5503711_PROD_2D_OUT_W1')) 'Neighboring manhole marker is excluded'
Assert-That (!(Test-OwnMarker 'MH_5503857_PROD_2D_PLAN' 'MH_5503857_DRAFT_2D_OUT_W1')) 'Draft marker is not mistaken for production marker'
Assert-That (!(Test-OwnMarker 'MH_5503857_PROD_2D_PLAN' 'MH_5503857_PROD_2D_OUT_W10')) 'W10 prefix does not match W1'
Assert-That (!(Test-OwnMarker 'Manual PLAN' 'Manual_OUT_W1')) 'Manual plans are not managed by name similarity'

$registry=$assembly.GetType('Hatco.PrecastManholeManager.Services.ManholeReviewRegistry')
$sameReason=$registry.GetMethod('SameReviewReason',[Reflection.BindingFlags]'NonPublic,Static')
Assert-That ($sameReason.Invoke($null,@('W1: issue; W2: issue',' W2: issue ; W1: issue '))) 'Review reason ordering and whitespace do not reopen an ignored warning'
Assert-That (!$sameReason.Invoke($null,@('W1: issue','W1: issue; MISSING OPENING W2'))) 'A newly added failure reopens ignored review'
Assert-That (!$sameReason.Invoke($null,@('MISSING OPENING W1 Source=123','MISSING OPENING W1 Source=456'))) 'A different service is not covered by the previous ignore'
Assert-That (!$sameReason.Invoke($null,@('opening 200 x 200','opening 300 x 200'))) 'A changed opening size is not covered by the previous ignore'


$issueType=$assembly.GetType('Hatco.PrecastManholeManager.Services.ManholeReviewIssue')
$issue=[Activator]::CreateInstance($issueType,$true)
$issue.FoundationUniqueId='test-foundation'; $issue.FoundationId=123
$issue.Status='IGNORED'; $issue.Reason='MISSING OPENING W1'; $issue.Severity='RECHECK'
$listType=$registry.GetMethod('SaveAt',[Reflection.BindingFlags]'NonPublic,Static').GetParameters()[1].ParameterType
$values=[Activator]::CreateInstance($listType); $values.Add($issue)
$tempRegister=Join-Path ([IO.Path]::GetTempPath()) ('mh-review-test-'+[Guid]::NewGuid().ToString('N')+'.tsv')
try {
    $registry.GetMethod('SaveAt',[Reflection.BindingFlags]'NonPublic,Static').Invoke($null,[object[]]@([string]$tempRegister,$values.PSObject.BaseObject))
    $loaded=$registry.GetMethod('LoadAt',[Reflection.BindingFlags]'NonPublic,Static').Invoke($null,[object[]]@([string]$tempRegister))
    Assert-That ($loaded.Count -eq 1 -and $loaded[0].Status -eq 'IGNORED' -and $loaded[0].Reason -eq $issue.Reason) 'Ignored review and its reason survive register reload'
} finally { if(Test-Path -LiteralPath $tempRegister) { Remove-Item -LiteralPath $tempRegister } }

$noteWidth=$assembly.GetType('Hatco.PrecastManholeManager.Services.BatchSheetLayoutService').GetMethod('ValidNoteWidth',[Reflection.BindingFlags]'NonPublic,Static')
Assert-That ($noteWidth.Invoke($null,[object[]]@([double]4,[double]0.1,[double]2)) -eq 2) 'Wide sheet note is capped to the Revit text type maximum'
Assert-That ($noteWidth.Invoke($null,[object[]]@([double]0.01,[double]0.1,[double]2)) -eq 0.1) 'Narrow note respects the Revit minimum'
Assert-That ($noteWidth.Invoke($null,[object[]]@([double]1,[double]0.1,[double]2)) -eq 1) 'Valid note width is preserved'
$invalidWidthRejected=$false
try { $noteWidth.Invoke($null,[object[]]@([double]::NaN,[double]0.1,[double]2)) } catch { $invalidWidthRejected=$true }
Assert-That $invalidWidthRejected 'Non-finite note bounds fail explicitly rather than reaching Revit'
