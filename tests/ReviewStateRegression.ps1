param(
    [string]$AssemblyPath = "$PSScriptRoot\..\src\PrecastManholeManager\bin\Release\net48\PrecastManholeManager.dll",
    [string]$RevitDirectory = 'C:\Program Files\Autodesk\Revit 2024'
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $RevitDirectory 'RevitAPI.dll'))
$a = [Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath).Path)
$state = $a.GetType('Hatco.PrecastManholeManager.Services.ReviewState')
$registry = $a.GetType('Hatco.PrecastManholeManager.Services.ManholeReviewRegistry')
$issueType = $a.GetType('Hatco.PrecastManholeManager.Services.ManholeReviewIssue')
$domainType = $a.GetType('Hatco.PrecastManholeManager.Services.ReviewDomain')
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$save = $registry.GetMethod('SaveAt', $flags)
$load = $registry.GetMethod('LoadAt', $flags)
$list = [Activator]::CreateInstance($save.GetParameters()[1].ParameterType)
function Assert([bool]$ok, [string]$message) { if (!$ok) { throw $message }; "PASS: $message" }
function Domain($name) { [Enum]::Parse($domainType, $name) }
function Issue($domain, $uid = 'mh-one') {
    $r = [Activator]::CreateInstance($issueType, $true)
    $r.FoundationUniqueId = $uid; $r.FoundationId = 123
    $r.Domain = Domain $domain; $r.Status = 'OPEN'; $r.Reason = 'Problem'
    $r.Severity = 'REVIEW'; $r.WallIds = '1,2'; $r.Evidence = 'geometry-A'
    return $r
}
function Resolve($domain) { $state.GetMethod('Resolve', $flags).Invoke($null, [object[]]@($list.PSObject.BaseObject, 'mh-one', (Domain $domain))) }
function Update($r, $reason = 'Problem', $severity = 'REVIEW', $evidence = 'geometry-A') {
    $state.GetMethod('Update', $flags).Invoke($null, [object[]]@($r, $reason, $severity, '1,2', $evidence, $true))
}
function Accept($r) { $state.GetMethod('Accept', $flags).Invoke($null, [object[]]@($r, 'tester', 'Checked on site')) }
$opening = Issue 'Openings'; $dim = Issue 'Dimensions'; $layout = Issue 'Layout'; $legacy = Issue 'Legacy'; $other = Issue 'Openings' 'mh-two'
foreach ($r in @($opening, $dim, $layout, $legacy, $other)) { $list.Add($r) }
Resolve 'Openings'
Assert ($opening.Status -eq 'RESOLVED' -and $dim.Status -eq 'OPEN' -and $layout.Status -eq 'OPEN' -and $legacy.Status -eq 'OPEN' -and $other.Status -eq 'OPEN') 'Opening success preserves dimensions, layout, legacy and other manholes'
Resolve 'Dimensions'
Assert ($dim.Status -eq 'RESOLVED' -and $layout.Status -eq 'OPEN') 'Dimension success does not resolve layout'
Update $opening
Accept $opening
Update $opening
Assert ($opening.Status -eq 'IGNORED' -and $opening.AcceptanceNote -eq 'Checked on site') 'Same issue and evidence retain manual acceptance'
Update $opening 'Problem' 'REVIEW' 'geometry-B'
Assert ($opening.Status -eq 'OPEN' -and !$opening.AcceptanceNote) 'Changed geometry reopens the same textual issue'
Accept $opening
Update $opening 'New problem' 'REVIEW' 'geometry-B'
Assert ($opening.Status -eq 'OPEN') 'New failure reopens an accepted domain'
Accept $opening
Update $opening 'New problem' 'BLOCKED' 'geometry-B'
Assert ($opening.Status -eq 'OPEN') 'Severity escalation cannot remain ignored'
$rejected = $false
try { $state.GetMethod('Accept', $flags).Invoke($null, [object[]]@($opening, 'tester', ' ')) } catch { $rejected = $true }
Assert $rejected 'Manual acceptance requires a reason'
$list.Clear(); $list.Add($opening)
Accept $opening
Assert ($state.GetMethod('Readiness', $flags).Invoke($null, [object[]](,$list.PSObject.BaseObject)) -eq 'MANUALLY ACCEPTED / NOT VERIFIED') 'Ignored does not mean repaired or delivery-ready'
$list.Add($layout)
Assert ($state.GetMethod('Status', $flags).Invoke($null, [object[]](,$list.PSObject.BaseObject)) -eq 'OPEN') 'An open domain takes precedence over an ignored domain'
$list.Clear()
Assert ($state.GetMethod('Readiness', $flags).Invoke($null, [object[]](,$list.PSObject.BaseObject)) -eq 'DELIVERY NOT VERIFIED') 'No issues is not evidence of delivery readiness'
function Enc([string]$s) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s)) }
$path = Join-Path ([IO.Path]::GetTempPath()) ('mh-review-v2-' + [Guid]::NewGuid().ToString('N') + '.tsv')
try {
    $old = "UniqueId`tId`tSeverity`tStatus`tReason`tWalls`tUpdatedUtc`tViewId`tViewName`tVersion`r`n" +
        (@((Enc 'mh-one'), '123', (Enc 'REVIEW'), (Enc 'OPEN'), (Enc 'Mixed dimension and opening failure'), (Enc '1,2'), (Enc '2026-10-03'), '0', (Enc ''), '1') -join "`t") + "`r`n"
    [IO.File]::WriteAllText($path, $old)
    $migrated = $load.Invoke($null, [object[]]@([string]$path))
    Assert ($migrated.Count -eq 1 -and $migrated[0].Domain.ToString() -eq 'Legacy' -and $migrated[0].Status -eq 'OPEN') 'Mixed legacy records stay open without guessed classification'
    $migrated.Add($opening); $migrated.Add($dim)
    $save.Invoke($null, [object[]]@([string]$path, $migrated.PSObject.BaseObject))
    Assert ([IO.File]::ReadAllText($path + '.v1.bak') -eq $old) 'Original register backed up before first v2 write'
    $loaded = $load.Invoke($null, [object[]]@([string]$path))
    Assert ($loaded.Count -eq 3) 'Multiple domains for one manhole survive serialization'
    $ignored = @($loaded | Where-Object {$_.Domain.ToString() -eq 'Openings'})[0]
    Assert ($ignored.Status -eq 'IGNORED' -and $ignored.AcceptedBy -eq 'tester' -and $ignored.AcceptanceNote -eq 'Checked on site') 'Acceptance audit survives reload'
    [IO.File]::AppendAllText($path, "broken record`r`n")
    $before = [IO.File]::ReadAllText($path)
    $rejected = $false
    try { $save.Invoke($null, [object[]]@([string]$path, $loaded.PSObject.BaseObject)) } catch { $rejected = $true }
    Assert ($rejected -and [IO.File]::ReadAllText($path) -eq $before) 'Malformed source cannot be silently overwritten or lose records'
} finally {
    foreach ($file in @($path, $path + '.v1.bak', $path + '.tmp')) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file } }
}
'Review state tests passed. Revit transactions and live drawing validation require an integration run.'
