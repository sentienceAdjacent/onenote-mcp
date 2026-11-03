<#
.SYNOPSIS Interactive failure-mode runner (hitl). Phill sets up each state; the runner does one read and one write.
.DESCRIPTION
  Scenarios (run all in order, or pick one with -Scenario):
    onenote-closed   Phill closes OneNote. Records a call on the now-stale COM object, then whether
                     creating the object starts OneNote, and how long that takes.
    elevated         Run this script from an elevated shell while OneNote runs non-elevated.
    modal-dialog     Phill opens a modal dialog in OneNote (e.g. File > Options) and leaves it open.
    sync             Phill starts a sync (Shift+F9) so a cloud notebook is syncing. The read targets
                     the largest real notebook (read-only); the write targets the spike page.
    locked-section   Phill adds a password-protected section named "Locked" to the MCP Spike notebook
                     and locks it. Read = its hierarchy/page; write = CreateNewPage in it.
    unlocked-section Phill unlocks that section. Same read and write.
  Every call has a 30 s timeout. The runner never closes or kills OneNote; it asks Phill to.
  Writes only touch the spike notebook (Invoke-GuardedWrite). Logs to spikes/results/failures.jsonl.
.EXAMPLE pwsh -File spikes/com-capabilities/04-failure-modes.ps1
.EXAMPLE pwsh -File spikes/com-capabilities/04-failure-modes.ps1 -Scenario elevated   # from an elevated shell
#>
param(
    [ValidateSet('all', 'onenote-closed', 'elevated', 'modal-dialog', 'sync', 'locked-section', 'unlocked-section')]
    [string]$Scenario = 'all'
)
Import-Module (Join-Path $PSScriptRoot 'OneNoteProbe.psm1') -Force
Use-WindowsPowerShell -ScriptPath $PSCommandPath -BoundParameters $PSBoundParameters

$Log = 'failures'
$Ns = Get-OneNoteNamespace
$Scope = Get-OneNoteEnum Scope
$hcode = Get-HierarchyCode
$state = Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'results\spike-state.json') -Raw | ConvertFrom-Json

function Ask([string]$Setup) {
    Write-Host ''
    Write-Host "SET UP: $Setup" -ForegroundColor Yellow
    $a = Read-Host 'Press Enter when ready, or type "skip <reason>"'
    if ($a -like 'skip*') { return ($a -replace '^skip\s*', '') }
    $null
}
function Skip([string]$Name, [string]$Reason, [string]$WriteMethod = 'UpdatePageContent') {
    Write-ProbeResult -Log $Log -Probe "$Name-read" -Method 'GetHierarchy' -Outcome 'skipped' -Scenario $Name -Detail @{ reason = $(if ($Reason) { $Reason } else { 'skipped by Phill' }) }
    Write-ProbeResult -Log $Log -Probe "$Name-write" -Method $WriteMethod -Outcome 'skipped' -Scenario $Name -Detail @{ reason = $(if ($Reason) { $Reason } else { 'skipped by Phill' }) }
}
function Read-Once([string]$Name, [string]$Start = '', [int]$S = $Scope.Notebooks) {
    Invoke-Probe -Log $Log -Probe "$Name-read" -Method 'GetHierarchy' -Code $hcode -A @{ id = $Start; scope = $S } -LogArgs @{ start = $(if ($Start) { $Start } else { '(root)' }); scope = $S } -Scenario $Name -TimeoutSec 30 `
        -DetailScript { param($v) $x = ConvertTo-OneNoteXml $v; @{ bytes = $v.Length; notebooks = $x.Doc.SelectNodes('//one:Notebook', $x.Ns).Count; pages = $x.Doc.SelectNodes('//one:Page', $x.Ns).Count } }
}
function Write-Once([string]$Name) {
    $xml = "<?xml version=`"1.0`"?><one:Page xmlns:one=`"$Ns`" ID=`"$($state.pageId)`"><one:Outline><one:OEChildren><one:OE><one:T><![CDATA[failure-mode probe: $Name]]></one:T></one:OE></one:OEChildren></one:Outline></one:Page>"
    Invoke-GuardedWrite -Log $Log -Probe "$Name-write" -Method 'UpdatePageContent' -TargetId $state.pageId -Code 'param($on, $a) $on.UpdatePageContent($a.xml); "ok"' -A @{ xml = $xml } -LogArgs @{ pageId = $state.pageId; change = 'append Outline' } -Scenario $Name -TimeoutSec 30
}
function Run([string]$Name) { $Scenario -eq 'all' -or $Scenario -eq $Name }

# Warm up: make sure the worker holds a live COM object.
Read-Once 'warmup' | Out-Null

if (Run 'onenote-closed') {
    $r = Ask 'Close OneNote completely (File > Exit, or close every OneNote window). Check Task Manager: ONENOTE.EXE gone.'
    if ($null -ne $r) { Skip 'onenote-closed' $r } else {
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-Process ONENOTE -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
        $gone = -not (Get-Process ONENOTE -ErrorAction SilentlyContinue)
        Write-ProbeResult -Log $Log -Probe 'onenote-closed-process' -Method 'Get-Process ONENOTE' -Outcome 'ok' -Verdict 'info' -Ms 0 -Scenario 'onenote-closed' -Detail @{ processGone = $gone; note = 'OneNote may keep running in the background after its window closes' }
        Read-Once 'onenote-closed-stale-object' | Out-Null
        Invoke-Probe -Log $Log -Probe 'onenote-closed-activation' -Method 'New-Object OneNote.Application' -Reconnect -Code 'param($on, $a) [bool](Get-Process ONENOTE -ErrorAction SilentlyContinue)' -Scenario 'onenote-closed' -TimeoutSec 60 `
            -DetailScript { param($v) @{ oneNoteRunningAfter = $v } } | Out-Null
        Read-Once 'onenote-closed' | Out-Null
        Write-Once 'onenote-closed' | Out-Null
    }
}

if (Run 'elevated') {
    if (-not (Test-Elevated)) {
        Write-Host 'Not elevated: re-run from an elevated shell with -Scenario elevated (OneNote left running normally).' -ForegroundColor Yellow
        if ($Scenario -eq 'elevated') { Skip 'elevated' 'runner was not elevated' }
    } else {
        $r = Ask 'Confirm OneNote is running NON-elevated (started normally from the Start menu).'
        if ($null -ne $r) { Skip 'elevated' $r } else {
            Invoke-Probe -Log $Log -Probe 'elevated-activation' -Method 'New-Object OneNote.Application' -Reconnect -Code 'param($on, $a) "ok"' -Scenario 'elevated' -TimeoutSec 60 | Out-Null
            Read-Once 'elevated' | Out-Null
            Write-Once 'elevated' | Out-Null
        }
    }
}

if (Run 'modal-dialog') {
    $r = Ask 'In OneNote, open a modal dialog (File > Options) and leave it open.'
    if ($null -ne $r) { Skip 'modal-dialog' $r } else {
        Read-Once 'modal-dialog' | Out-Null
        Write-Once 'modal-dialog' | Out-Null
        Write-Host 'You can close the dialog now.'
    }
}

if (Run 'sync') {
    $r = Ask 'In OneNote press Shift+F9 (sync all notebooks) and run this while it is syncing.'
    if ($null -ne $r) { Skip 'sync' $r } else {
        Read-Once 'sync' '' $Scope.Pages | Out-Null
        Write-Once 'sync' | Out-Null
    }
}

foreach ($lk in 'locked-section', 'unlocked-section') {
    if (-not (Run $lk)) { continue }
    $setup = if ($lk -eq 'locked-section') { 'In the "MCP Spike" notebook, add a section named "Locked", password-protect it (Review > Password), add one page, then lock it (Review > Lock All).' } else { 'Unlock the "Locked" section in MCP Spike (enter its password).' }
    $r = Ask $setup
    if ($null -ne $r) { Skip $lk $r 'CreateNewPage'; continue }
    $h = Get-SpikeHierarchy
    $sec = $h.Doc.SelectSingleNode("//one:Section[@name='Locked']", $h.Ns)
    if (-not $sec) { Skip $lk 'no section named Locked in the spike notebook' 'CreateNewPage'; continue }
    Write-ProbeResult -Log $Log -Probe "$lk-state" -Method 'GetHierarchy' -Outcome 'ok' -Verdict 'info' -Ms 0 -Scenario $lk -Detail @{ sectionId = $sec.ID; encrypted = $sec.GetAttribute('encrypted'); locked = $sec.GetAttribute('locked'); pagesListed = $sec.SelectNodes('one:Page', $h.Ns).Count }
    Read-Once $lk $sec.ID $Scope.Pages | Out-Null
    $pg = $sec.SelectSingleNode('one:Page', $h.Ns)
    if ($pg) {
        Invoke-Probe -Log $Log -Probe "$lk-page-content" -Method 'GetPageContent' -Code 'param($on, $a) $x = ""; $on.GetPageContent($a.id, [ref]$x, 0); $x' -A @{ id = $pg.ID } -LogArgs @{ pageId = $pg.ID } -Scenario $lk -TimeoutSec 30 -DetailScript { param($v) @{ bytes = $v.Length } } | Out-Null
    }
    Invoke-GuardedWrite -Log $Log -Probe "$lk-write" -Method 'CreateNewPage' -TargetId $sec.ID -Code 'param($on, $a) $id = ""; $on.CreateNewPage($a.s, [ref]$id, 0); $id' -A @{ s = $sec.ID } -LogArgs @{ sectionId = $sec.ID } -Scenario $lk -TimeoutSec 30 | Out-Null
}

Reset-ComWorker -Abandon
Write-Host 'Done. Results: spikes/results/failures.jsonl'
exit 0
