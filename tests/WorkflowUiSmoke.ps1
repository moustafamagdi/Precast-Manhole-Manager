param(
 [string]$OutputDirectory = (Join-Path $env:TEMP 'ManholeWorkflowUi'),
 [string]$RevitDirectory = 'C:\Program Files\Autodesk\Revit 2024'
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
[void][Reflection.Assembly]::LoadFrom((Join-Path $RevitDirectory 'RevitAPI.dll'))
$a=[Reflection.Assembly]::LoadFrom((Resolve-Path "$PSScriptRoot/../src/PrecastManholeManager/bin/Release/net48/PrecastManholeManager.dll").Path)
$t=$a.GetType('Hatco.PrecastManholeManager.UI.SimpleProjectWindow')
$w=$t.GetConstructors()[0].Invoke([object[]]@($null,[double]50))
$root=$w.Content
$tabs=$root.Children[0].Children | Where-Object { $_ -is [Windows.Controls.TabControl] }
if($tabs.Items.Count -ne 4){throw 'Expected four workflow tabs'}
$drawingButtons=@($tabs.Items[2].Content.Content.Children | Where-Object {$_ -is [Windows.Controls.WrapPanel]} | ForEach-Object {$_.Children} | Where-Object {$_ -is [Windows.Controls.Button]})
if(!($drawingButtons | Where-Object {$_.Content -eq 'Full Automation - All'})){throw 'Full automation button is missing'}
$drawingChoices=@($tabs.Items[2].Content.Content.Children | Where-Object {$_ -is [Windows.Controls.StackPanel]} | ForEach-Object {$_.Children} | Where-Object {$_ -is [Windows.Controls.ComboBox]})
$drawingTask=$drawingChoices | Where-Object {$_.Items.Contains('Check drawings - Selected')}
if(!$drawingTask -or !$drawingTask.Items.Contains('Check drawings - All')){throw 'Read-only drawing checks are missing'}
$drawingTask.SelectedIndex=6
$drawingTask.SelectedIndex=7
$drawingTask.SelectedIndex=0
$reviewButtons=@($tabs.Items[0].Content.Content.Children | Where-Object {$_ -is [Windows.Controls.WrapPanel]} | ForEach-Object {$_.Children} | Where-Object {$_ -is [Windows.Controls.Button]})
if(!($reviewButtons | Where-Object {$_.Content -eq 'Ignore / Restore Review'})){throw 'Review acknowledgment button is missing'}
$openingPanel=$tabs.Items[1].Content.Content
$scopes=@($openingPanel.Children | Where-Object {$_ -is [Windows.Controls.StackPanel]} | ForEach-Object {$_.Children} | Where-Object {$_ -is [Windows.Controls.ComboBox]})
if($scopes[0].SelectedIndex -ne 1){throw 'Opening scope must default to picking'}
$reset=$t.GetField('_resetProfiles',[Reflection.BindingFlags]'NonPublic,Instance').GetValue($w)
$reset.IsChecked=$true
$scopes[1].SelectedIndex=1
if(!$w.MissingOpeningsOnly){throw 'Missing mode was not applied'}
$reset=$t.GetField('_resetProfiles',[Reflection.BindingFlags]'NonPublic,Instance').GetValue($w)
if($reset.IsEnabled -or $reset.IsChecked){throw 'Missing mode must disable profile reset'}
$scopes[1].SelectedIndex=0
if($w.MissingOpeningsOnly -or !$reset.IsEnabled){throw 'Update mode was not restored'}
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
for($i=0;$i -lt 4;$i++) {
 $tabs.SelectedIndex=$i
 $root.Measure([Windows.Size]::new(1048,690)); $root.Arrange([Windows.Rect]::new(0,0,1048,690)); $root.UpdateLayout()
 $bmp=[Windows.Media.Imaging.RenderTargetBitmap]::new(1048,690,96,96,[Windows.Media.PixelFormats]::Pbgra32)
 $bmp.Render($root)
 $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
 $stream=[IO.File]::Create((Join-Path $OutputDirectory "tab-$i.png")); $encoder.Save($stream); $stream.Dispose()
}
'PASS: Four workflow tabs, picking default, missing-only mode and profile-reset guard; all tabs rendered.'

$registry=$a.GetType('Hatco.PrecastManholeManager.Services.ManholeReviewRegistry')
$issueType=$a.GetType('Hatco.PrecastManholeManager.Services.ManholeReviewIssue')
$values=[Activator]::CreateInstance($registry.GetMethod('SaveAt',[Reflection.BindingFlags]'NonPublic,Static').GetParameters()[1].ParameterType)
$domainType=$a.GetType('Hatco.PrecastManholeManager.Services.ReviewDomain')
foreach($domain in @('Openings','Dimensions','Legacy')) {
 $issue=[Activator]::CreateInstance($issueType,$true)
 $issue.FoundationId=123; $issue.FoundationUniqueId='sample'; $issue.Status='OPEN'
 $issue.Domain=[Enum]::Parse($domainType,$domain); $issue.Reason='Example issue for independent review'
 $values.Add($issue)
}
$reviewType=$a.GetType('Hatco.PrecastManholeManager.UI.ManholeReviewManagerWindow')
$review=$reviewType.GetConstructors()[0].Invoke([object[]]@($values.PSObject.BaseObject,'Test register (not a real project)',$true))
$reviewGrid=$review.Content.Children | Where-Object {$_ -is [Windows.Controls.DataGrid]}
if(!($reviewGrid.Columns | Where-Object {$_.Header -eq 'Domain'})){throw 'Review domains must be visible'}
$footerButtons=@($review.Content.Children | Where-Object {$_ -is [Windows.Controls.StackPanel]} | ForEach-Object {$_.Children} | Where-Object {$_ -is [Windows.Controls.Button]})
if($footerButtons | Where-Object {$_.Content -eq 'Mark Selected Resolved'}){throw 'Acceptance must not pretend to resolve an issue'}
if(!($footerButtons | Where-Object {$_.Content -eq 'Accept Selected (Ignore)'})){throw 'Explicit acceptance action is missing'}
$review.Content.Measure([Windows.Size]::new(1160,610)); $review.Content.Arrange([Windows.Rect]::new(0,0,1160,610)); $review.Content.UpdateLayout()
$bmp=[Windows.Media.Imaging.RenderTargetBitmap]::new(1160,610,96,96,[Windows.Media.PixelFormats]::Pbgra32)
$bmp.Render($review.Content)
$encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
$stream=[IO.File]::Create((Join-Path $OutputDirectory 'review-domains.png')); $encoder.Save($stream); $stream.Dispose()
'PASS: Domain-specific review window renders with acceptance instead of manual resolution.'
