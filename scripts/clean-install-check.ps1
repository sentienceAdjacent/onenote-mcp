#Requires -Version 7
<#
.SYNOPSIS
  Story 1.13's clean-machine check: shows that `OneNoteMcp uninstall --purge` removed only OneNoteMcp's files.

.DESCRIPTION
  Run it twice around the purge:

    pwsh -File scripts/clean-install-check.ps1 -Phase Before
    & "<path>\OneNoteMcp.exe" uninstall --purge
    pwsh -File scripts/clean-install-check.ps1 -Phase After

  Each phase snapshots:
    - every file and folder under %APPDATA%\OneNoteMcp and %LOCALAPPDATA%\OneNoteMcp (recursively);
    - the top-level listing of %APPDATA% and %LOCALAPPDATA% (their parents);
    - every file under the plugin's install folder, %USERPROFILE%\.claude\plugins\cache\sentienceadjacent\onenote,
      which purge must leave alone.

  After compares the two snapshots and prints counts and PASS or FAIL only: never a path, a file name or any file
  content, so its output can be committed. The snapshots themselves (paths and sizes, no content) stay in -OutDir
  for a local look; never commit them.

  PASS means: something was removed from the OneNoteMcp folders, nothing is left in them except files purge
  doesn't own (counted as foreign), nothing else at the top of %APPDATA% or %LOCALAPPDATA% disappeared, and the
  plugin's install folder is unchanged.

  Exit code: 0 PASS, 1 FAIL, 2 bad usage (After without Before).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Before', 'After')]
    [string]$Phase,

    [string]$OutDir = (Join-Path ([IO.Path]::GetTempPath()) 'onenote-mcp-clean-check'),

    # Rehearsal only: stand-ins for %APPDATA%, %LOCALAPPDATA% and the plugin's install folder, so the script can be
    # tried on a temp tree with ONENOTE_MCP_SETTINGS_PATH and ONENOTE_MCP_LOG_DIR pointing into it.
    [string]$AppDataRoot = [Environment]::GetFolderPath('ApplicationData'),
    [string]$LocalAppDataRoot = [Environment]::GetFolderPath('LocalApplicationData'),
    [string]$PluginRoot = (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.claude\plugins\cache\sentienceadjacent\onenote')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$appData = $AppDataRoot
$localAppData = $LocalAppDataRoot
$ownRoots = @((Join-Path $appData 'OneNoteMcp'), (Join-Path $localAppData 'OneNoteMcp'))
$parents = @($appData, $localAppData)

# The exact places purge targets (UninstallCommand's patterns, default locations, top level only). A file anywhere
# else under the OneNoteMcp folders is foreign, even when its name looks like one of ours.
$purgeTargets = @(
    @{ Folder = $ownRoots[0]; Pattern = '^(settings\.json|\.settings\..+\.tmp)$' },
    @{ Folder = (Join-Path $ownRoots[1] 'logs'); Pattern = '^onenote-mcp-.*\.log$' },
    @{ Folder = (Join-Path $ownRoots[1] 'journal'); Pattern = '^\d{8}T\d{9}-\d+-[0-9a-fA-F-]{32,36}\.json\.gz$' }
)

function Test-PurgeTarget([string]$path) {
    $folder = Split-Path -Parent $path
    $name = Split-Path -Leaf $path
    [bool]($purgeTargets | Where-Object { $folder -ieq $_.Folder -and $name -match $_.Pattern })
}

# Mark-of-the-Web on the plugin's installed exe (story-release-pipeline-plan.md moved this check here). Reported only.
function Get-MarkOfTheWeb {
    $exe = Get-ChildItem -LiteralPath $PluginRoot -Filter 'OneNoteMcp.exe' -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) { return 'no plugin exe found' }
    if (Get-Item -LiteralPath $exe.FullName -Stream 'Zone.Identifier' -ErrorAction SilentlyContinue) { 'yes' } else { 'no' }
}

function Get-Entries([string]$folder, [switch]$Recurse) {
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { return @() }
    @(Get-ChildItem -LiteralPath $folder -Force -Recurse:$Recurse -ErrorAction SilentlyContinue |
        ForEach-Object {
            [pscustomobject]@{
                Path   = $_.FullName
                Folder = $_.PSIsContainer
                Length = if ($_.PSIsContainer) { 0 } else { $_.Length }
            }
        })
}

function New-Snapshot {
    [pscustomobject]@{
        TakenAt = (Get-Date).ToUniversalTime().ToString('o')
        Own     = @($ownRoots | ForEach-Object {
                if (Test-Path -LiteralPath $_ -PathType Container) { [pscustomobject]@{ Path = $_; Folder = $true; Length = 0 } }
                Get-Entries $_ -Recurse
            })
        Parents = @($parents | ForEach-Object { Get-Entries $_ })
        Plugin  = @(Get-Entries $pluginRoot -Recurse | Where-Object { -not $_.Folder })
    }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$beforeFile = Join-Path $OutDir 'before.json'
$afterFile = Join-Path $OutDir 'after.json'

if ($Phase -eq 'Before') {
    $snapshot = New-Snapshot
    $snapshot | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $beforeFile -Encoding utf8NoBOM
    Remove-Item -LiteralPath $afterFile -ErrorAction SilentlyContinue
    $ownFiles = @($snapshot.Own | Where-Object { -not $_.Folder })
    Write-Output "Before: $($ownFiles.Count) files in the OneNoteMcp folders, $($snapshot.Plugin.Count) files in the plugin's install folder."
    Write-Output "Mark-of-the-Web (Zone.Identifier) on the plugin's OneNoteMcp.exe: $(Get-MarkOfTheWeb)"
    Write-Output "Now run: OneNoteMcp.exe uninstall --purge, then this script with -Phase After."
    exit 0
}

if (-not (Test-Path -LiteralPath $beforeFile)) {
    Write-Output "No 'Before' snapshot in -OutDir. Run with -Phase Before first."
    exit 2
}

$before = Get-Content -Raw -LiteralPath $beforeFile | ConvertFrom-Json
$after = New-Snapshot
$after | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $afterFile -Encoding utf8NoBOM

function New-PathSet($paths) {
    $set = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in @($paths)) { if ($path) { [void]$set.Add($path) } }
    , $set
}

$afterOwn = New-PathSet ($after.Own | ForEach-Object { $_.Path })
$removedOwn = @($before.Own | Where-Object { -not $afterOwn.Contains($_.Path) })
$removedOwnFiles = @($removedOwn | Where-Object { -not $_.Folder })
$removedOwnFolders = @($removedOwn | Where-Object { $_.Folder })
$removedNotOwnName = @($removedOwnFiles | Where-Object { -not (Test-PurgeTarget $_.Path) })

$leftFiles = @($after.Own | Where-Object { -not $_.Folder })
$leftOwnName = @($leftFiles | Where-Object { Test-PurgeTarget $_.Path })
$leftForeign = $leftFiles.Count - $leftOwnName.Count

$afterParents = New-PathSet ($after.Parents | ForEach-Object { $_.Path })
$ownRootSet = New-PathSet $ownRoots
$removedElsewhere = @($before.Parents | Where-Object { -not $afterParents.Contains($_.Path) -and -not $ownRootSet.Contains($_.Path) })

$pluginKey = { param($e) "$($e.Path)|$($e.Length)" }
$pluginBefore = @(@($before.Plugin | ForEach-Object { & $pluginKey $_ }) | Sort-Object)
$pluginAfter = @(@($after.Plugin | ForEach-Object { & $pluginKey $_ }) | Sort-Object)
$pluginSame = (@($pluginBefore) -join "`n") -ceq (@($pluginAfter) -join "`n")

$checks = [ordered]@{
    'Files removed from the OneNoteMcp folders'                  = @($removedOwnFiles.Count, ($removedOwnFiles.Count -gt 0))
    'Folders removed from them'                                  = @($removedOwnFolders.Count, $true)
    'Removed files purge does not own (must be 0)'               = @($removedNotOwnName.Count, ($removedNotOwnName.Count -eq 0))
    'OneNoteMcp files left behind (must be 0)'                   = @($leftOwnName.Count, ($leftOwnName.Count -eq 0))
    'Foreign files left in the OneNoteMcp folders (kept)'        = @($leftForeign, $true)
    'Other top-level entries gone from AppData (must be 0)'      = @($removedElsewhere.Count, ($removedElsewhere.Count -eq 0))
    "Plugin install files, before / after (must be unchanged)"   = @("$($pluginBefore.Count) / $($pluginAfter.Count)", $pluginSame)
}

$pass = $true
foreach ($check in $checks.GetEnumerator()) {
    $value, $ok = $check.Value
    $pass = $pass -and $ok
    Write-Output ('{0,-4} {1}: {2}' -f ($(if ($ok) { 'ok' } else { 'FAIL' })), $check.Key, $value)
}

Write-Output "Mark-of-the-Web (Zone.Identifier) on the plugin's OneNoteMcp.exe: $(Get-MarkOfTheWeb)"
Write-Output $(if ($pass) { 'PASS' } else { 'FAIL (the snapshots in -OutDir show which paths; keep them local)' })
exit $(if ($pass) { 0 } else { 1 })
