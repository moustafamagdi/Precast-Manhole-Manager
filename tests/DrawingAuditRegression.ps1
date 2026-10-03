param(
    [string]$AssemblyPath = "$PSScriptRoot\..\src\PrecastManholeManager\bin\Release\net48\PrecastManholeManager.dll",
    [string]$RevitDirectory = 'C:\Program Files\Autodesk\Revit 2024'
)
$ErrorActionPreference='Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $RevitDirectory 'RevitAPI.dll'))
$a=[Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath).Path)
$t=$a.GetType('Hatco.PrecastManholeManager.Services.DrawingGeometry')
$f=[Reflection.BindingFlags]'NonPublic,Static'
function Assert([bool]$condition,[string]$message) { if(!$condition){throw $message}; "PASS: $message" }
function Contains($outer,$inner,[double]$tol=0) { $t.GetMethod('Contains',$f).Invoke($null,[object[]]@([double[]]$outer,[double[]]$inner,$tol)) }
function Overlaps($first,$second,[double]$tol=0) { $t.GetMethod('Overlaps',$f).Invoke($null,[object[]]@([double[]]$first,[double[]]$second,$tol)) }
function Points($box,$points,[bool]$depth=$true) { $t.GetMethod('ContainsPoints',$f).Invoke($null,[object[]]@([double[]]$box,[double[][]]$points,[double]0.001,$depth)) }
Assert (Contains @(0,0,10,10) @(1,1,9,9)) 'View and title entirely within the sheet pass'
Assert (!(Contains @(0,0,10,10) @(1,-1,9,9))) 'Last-row viewport below sheet is detected'
Assert (Contains @(-20,-30,-10,-10) @(-19,-29,-11,-11)) 'Translated sheets with negative coordinates work'
Assert (!(Contains @(0,0,10,10) @(1,1,[double]::NaN,9))) 'Invalid bounds cannot pass'
Assert (!(Contains @(0,0,10,10) @(1,1,1,9))) 'Empty viewport cannot pass as visible content'
Assert (Contains @(0,0,10,10) @(0,-0.0005,9,9) 0.001) 'Numerical edge tolerance is respected'
Assert (!(Contains @(0,0,10,10) @(1,1,9,9) ([double]::PositiveInfinity))) 'Infinite tolerance cannot certify layout'
Assert (Overlaps @(0,0,2,2) @(1,1,3,3)) 'Overlapping neighboring manhole views are detected'
Assert (!(Overlaps @(0,0,2,2) @(2,0,4,2))) 'Touching borders are not reported as overlapping area'
Assert (!(Overlaps @(0,0,2,2) @(0,3,2,5))) 'Separate rows do not overlap'
$union=$t.GetMethod('Union',$f).Invoke($null,[object[]]@([double[]]@(1,1,4,4),[double[]]@(1,-1,5,0)))
Assert (!(Contains @(0,0,6,6) $union)) 'Title outside the sheet is detected even when the view box fits'
$sheet=[double[]]@(0,0,3.9,2.76)
$rows=@(0..5 | ForEach-Object { ,$t.GetMethod('Row',$f).Invoke($null,[object[]]@($sheet,[int]$_)) })
Assert ($rows.Count -eq 6) 'All six reserved rows are checked'
for($i=0;$i -lt 5;$i++) { Assert ([Math]::Abs($rows[$i][1]-$rows[$i+1][3]) -lt 1e-9) "Row $i meets the next row without overlap" }
$rejected=$false
try { $t.GetMethod('Row',$f).Invoke($null,[object[]]@($sheet,[int]6)) } catch { $rejected=$true }
Assert $rejected 'Seventh row on the same sheet is rejected'
$crop=@(-2,-2,-1,2,2,1)
Assert (Points $crop @(@(-1,-1,-.5),@(1,1,.5))) 'Body within crop and depth passes'
Assert (!(Points $crop @(@(5,-1,-.5),@(7,1,.5)))) 'Moved body outside the old view is detected'
Assert (!(Points $crop @(@(-1,-1,3),@(1,1,4)))) 'Incorrect section depth is detected'
Assert (Points $crop @(@(-1,-1,3),@(1,1,4)) $false) 'Plan extent test does not mistake elevation for crop clipping'
Assert (!(Points $crop @())) 'Missing geometric samples cannot pass'
Assert (!(Points $crop @(@([double]::NaN,0,0)))) 'Non-finite transformed coordinates cannot pass'
# Local points after projecting a 45-degree rotated rectangle into its view frame.
$c=[Math]::Sqrt(.5)
$local=@()
foreach($x in @(-1.0,1.0)) { foreach($y in @(-.5,.5)) {
 $wx=$c*$x-$c*$y+100; $wy=$c*$x+$c*$y-50
 $lx=$c*($wx-100)+$c*($wy+50)
 $ly=-$c*($wx-100)+$c*($wy+50)
 $local+=,([double[]]@($lx,$ly,0))
} }
Assert (Points @(-1.1,-.6,-1,1.1,.6,1) $local) 'Rotated body is checked in local view coordinates'
'Drawing policy tests passed. Revit reference resolution, visibility and rendering require the integration acceptance matrix.'
