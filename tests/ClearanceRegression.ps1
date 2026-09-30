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
