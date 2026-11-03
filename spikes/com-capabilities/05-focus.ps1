<#
.SYNOPSIS DEDICATED WINDOW/FOCUS PROBE -- this script WILL bring OneNote to the foreground.
.DESCRIPTION
  Warn the user before running: do not type anywhere while it runs (about 30 s).
  Targets the MCP Spike notebook only (Invoke-GuardedWrite).
  1. For each call, records the foreground window's process before and after the call:
     GetHierarchy, CreateNewPage, UpdatePageContent, OpenHierarchy (existing spike notebook),
     GetHyperlinkToObject, and last NavigateTo.
  2. Re-runs the "page displayed" lastModifiedTime trace, re-reading the page and taking a fresh
     lastModifiedTime before every step, so stray human typing cannot be mistaken for the result.
  Logs to spikes/results/focus.jsonl.
#>
param()
Import-Module (Join-Path $PSScriptRoot 'OneNoteProbe.psm1') -Force
Use-WindowsPowerShell -ScriptPath $PSCommandPath -BoundParameters $PSBoundParameters

Add-Type -Namespace Spike -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
'@
function Get-Foreground {
    $h = [Spike.Win]::GetForegroundWindow(); $fpid = [uint32]0
    [void][Spike.Win]::GetWindowThreadProcessId($h, [ref]$fpid)
    $p = Get-Process -Id $fpid -ErrorAction SilentlyContinue
    @{ process = $(if ($p) { $p.ProcessName } else { '?' }); hwnd = $h.ToInt64() }
}

$Log = 'focus'
$Ns = Get-OneNoteNamespace
$state = Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'results\spike-state.json') -Raw | ConvertFrom-Json
$nbPath = Get-SpikeNotebookPath
$pageCode = 'param($on, $a) $x = ""; $on.GetPageContent($a.id, [ref]$x, 0); $x'
function Get-PageDate([string]$Id) {
    $r = Invoke-OneNote -Code $pageCode -A @{ id = $Id }
    $s = ([xml]$r.value).DocumentElement.lastModifiedTime
    @{ text = $s; utc = [datetime]::Parse($s, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() }
}
function TitleXml([string]$Id, [string]$T) { "<?xml version=`"1.0`"?><one:Page xmlns:one=`"$Ns`" ID=`"$Id`"><one:Title><one:OE><one:T><![CDATA[$T]]></one:T></one:OE></one:Title></one:Page>" }

function Watch-Focus([string]$Probe, [scriptblock]$Call) {
    Start-Sleep -Milliseconds 500
    $before = Get-Foreground
    $r = & $Call
    Start-Sleep -Milliseconds 1500   # let any window activation settle
    $after = Get-Foreground
    $stole = ($after.process -like 'ONENOTE*') -and ($before.process -notlike 'ONENOTE*')
    Write-ProbeResult -Log $Log -Probe "$Probe-focus" -Method 'GetForegroundWindow' -Outcome 'ok' -Verdict 'info' -Ms 0 -Detail @{ before = $before.process; after = $after.process; foregroundChanged = ($before.hwnd -ne $after.hwnd); oneNoteTookForeground = $stole; callOk = [bool]$r.ok }
    $r
}

Write-Host 'Do not type: this probe brings OneNote to the foreground.' -ForegroundColor Yellow
$sec = $state.sectionId

# 1. Which calls take the foreground? NavigateTo last.
Watch-Focus 'GetHierarchy' { Invoke-Probe -Log $Log -Probe 'GetHierarchy' -Method 'GetHierarchy' -Code (Get-HierarchyCode) -A @{ id = ''; scope = 2 } } | Out-Null
$np = Watch-Focus 'CreateNewPage' { Invoke-GuardedWrite -Log $Log -Probe 'CreateNewPage' -Method 'CreateNewPage' -TargetId $sec -Code 'param($on, $a) $id = ""; $on.CreateNewPage($a.s, [ref]$id, 0); $id' -A @{ s = $sec } -LogArgs @{ sectionId = $sec } }
$tp = $np.value
Watch-Focus 'UpdatePageContent' { Invoke-GuardedWrite -Log $Log -Probe 'UpdatePageContent' -Method 'UpdatePageContent' -TargetId $tp -Code 'param($on, $a) $on.UpdatePageContent($a.xml); "ok"' -A @{ xml = (TitleXml $tp 'Focus probe') } -LogArgs @{ pageId = $tp } } | Out-Null
Watch-Focus 'OpenHierarchy' { Invoke-GuardedWrite -Log $Log -Probe 'OpenHierarchy' -Method 'OpenHierarchy' -TargetPath $nbPath -Code 'param($on, $a) $id = ""; $on.OpenHierarchy($a.p, "", [ref]$id, 1); $id' -A @{ p = $nbPath } -LogArgs @{ path = '%LOCALAPPDATA%\OneNoteMcpSpike\MCP Spike'; note = 'existing notebook' } } | Out-Null
Watch-Focus 'GetHyperlinkToObject' { Invoke-GuardedWrite -Log $Log -Probe 'GetHyperlinkToObject' -Method 'GetHyperlinkToObject' -TargetId $tp -Code 'param($on, $a) $l = ""; $on.GetHyperlinkToObject($a.id, "", [ref]$l); $l' -A @{ id = $tp } -LogArgs @{ pageId = $tp } } | Out-Null
Watch-Focus 'NavigateTo' { Invoke-GuardedWrite -Log $Log -Probe 'NavigateTo' -Method 'NavigateTo' -TargetId $tp -Code 'param($on, $a) $on.NavigateTo($a.id, "", $false); "ok"' -A @{ id = $tp } -LogArgs @{ pageId = $tp } } | Out-Null

# 2. Displayed-page lastModifiedTime trace, fresh read before every step.
$series = @(); $advances = 0
foreach ($i in 1..3) {
    $pre = Get-PageDate $tp
    Start-Sleep -Seconds 2
    Invoke-GuardedWrite -Log $Log -Probe 'lmt-displayed-edit' -Method 'UpdatePageContent' -TargetId $tp -Code 'param($on, $a) $on.UpdatePageContent($a.xml, $a.date); "ok"' `
        -A @{ xml = (TitleXml $tp "Focus probe $i"); date = $pre.utc } -LogArgs @{ pageId = $tp; dateExpectedLastModified = $pre.text; note = 'fresh read just before' } | Out-Null
    $post = Get-PageDate $tp
    $series += "$($pre.text)->$($post.text)"
    if ($post.text -gt $pre.text) { $advances++ }
}
$s0 = Get-PageDate $tp
Start-Sleep -Seconds 2
Invoke-GuardedWrite -Log $Log -Probe 'lmt-displayed-pre-stale-edit' -Method 'UpdatePageContent' -TargetId $tp -Code 'param($on, $a) $on.UpdatePageContent($a.xml); "ok"' -A @{ xml = (TitleXml $tp 'Focus probe pre-stale') } -LogArgs @{ pageId = $tp } | Out-Null
$s1 = Get-PageDate $tp
$stale = Invoke-GuardedWrite -Log $Log -Probe 'lmt-displayed-stale-write' -Method 'UpdatePageContent' -TargetId $tp -Expect '0x80042010' -Code 'param($on, $a) $on.UpdatePageContent($a.xml, $a.date); "ok"' `
    -A @{ xml = (TitleXml $tp 'Focus probe STALE'); date = $s0.utc } -LogArgs @{ pageId = $tp; dateExpectedLastModified = "$($s0.text) (stale)"; currentLastModified = $s1.text }
Write-ProbeResult -Log $Log -Probe 'lmt-displayed-trace' -Method 'GetPageContent' -CallArgs @{ pageId = $tp } -Outcome 'ok' -Verdict $(if ($advances -eq 3) { 'works' } else { 'fails' }) -Ms 0 -Detail @{ edits = 3; lmtAdvances = $advances; series = $series; staleWriteRejected = [bool]($stale.hresult -like '0x80042010*') }
Reset-ComWorker
Write-Host 'Done. You can use the keyboard again.'
exit 0
