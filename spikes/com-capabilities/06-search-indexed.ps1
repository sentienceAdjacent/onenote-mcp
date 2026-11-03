<#
.SYNOPSIS Search re-run in a second spike notebook inside the Windows Search crawl scope.
.DESCRIPTION
  Separates index latency from "location not indexed": the first spike notebook lives under
  %LOCALAPPDATA% (excluded by the crawl rules). This one is %USERPROFILE%\OneNoteMcpSpikeIndexed\
  MCP Spike Indexed (covered by the file:///C:\Users\ include rule, not under OneDrive, not in the repo).
  Steps, every write through Invoke-GuardedWrite:
    1. OpenHierarchy cftNotebook (create) and cftSection, logging the foreground app around each
       (creating a new notebook was unmeasured for focus).
    2. One page with a fresh unique token.
    3. FindPages on that notebook, indexed-only and include-unindexed: immediately, then every
       -PollSec until both hit or -DelaySec passes.
    4. Page and recycle-bin counts.
  No NavigateTo. Logs to spikes/results/search-indexed.jsonl.
#>
param([int]$DelaySec = 300, [int]$PollSec = 15)
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

$Log = 'search-indexed'
$Ns = Get-OneNoteNamespace
$root = Get-IndexedSpikeRoot
$nbPath = Get-IndexedSpikeNotebookPath
if (-not (Test-Path $root)) { New-Item -ItemType Directory $root | Out-Null }

function Watch-Focus([string]$Probe, [scriptblock]$Call) {
    Start-Sleep -Milliseconds 500
    $before = Get-Foreground
    $r = & $Call
    Start-Sleep -Milliseconds 1500
    $after = Get-Foreground
    Write-ProbeResult -Log $Log -Probe "$Probe-focus" -Method 'GetForegroundWindow' -Outcome 'ok' -Verdict 'info' -Ms 0 -Detail @{ before = $before.process; after = $after.process; foregroundChanged = ($before.hwnd -ne $after.hwnd); oneNoteTookForeground = (($after.process -like 'ONENOTE*') -and ($before.process -notlike 'ONENOTE*')); callOk = [bool]$r.ok }
    $r
}

# 1. Notebook + section
$nb = Watch-Focus 'open-indexed-notebook' {
    Invoke-GuardedWrite -Log $Log -Probe 'open-indexed-notebook' -Method 'OpenHierarchy' -TargetPath $nbPath `
        -Code 'param($on, $a) $id = ""; $on.OpenHierarchy($a.path, "", [ref]$id, 1); $id' -A @{ path = $nbPath } `
        -LogArgs @{ path = '%USERPROFILE%\OneNoteMcpSpikeIndexed\MCP Spike Indexed'; createFileType = 'cftNotebook' } -DetailScript { param($v) @{ notebookId = $v } }
}
if (-not $nb.ok) { exit 1 }
$nbId = $nb.value
if ((Get-SpikeNotebookId -NotebookPath $nbPath) -ne $nbId) { Write-Host 'guard resolves a different ID; stopping'; exit 1 }
$sec = Watch-Focus 'create-indexed-section' {
    Invoke-GuardedWrite -Log $Log -Probe 'create-indexed-section' -Method 'OpenHierarchy' -TargetId $nbId `
        -Code 'param($on, $a) $id = ""; $on.OpenHierarchy($a.file, $a.nb, [ref]$id, 3); $id' -A @{ file = 'Search.one'; nb = $nbId } `
        -LogArgs @{ parentId = $nbId; createFileType = 'cftSection' } -DetailScript { param($v) @{ sectionId = $v } }
}
if (-not $sec.ok) { exit 1 }
$secId = $sec.value

# 2. Token page
$token = 'zqspike' + ([guid]::NewGuid().ToString('N').Substring(0, 10))
$r = Invoke-GuardedWrite -Log $Log -Probe 'search-setup-page' -Method 'CreateNewPage' -TargetId $secId `
    -Code 'param($on, $a) $id = ""; $on.CreateNewPage($a.section, [ref]$id, 0); $id' -A @{ section = $secId } -LogArgs @{ sectionId = $secId }
if (-not $r.ok) { exit 1 }
$pageId = $r.value
$xml = "<?xml version=`"1.0`"?><one:Page xmlns:one=`"$Ns`" ID=`"$pageId`"><one:Title><one:OE><one:T><![CDATA[Search probe]]></one:T></one:OE></one:Title><one:Outline><one:OEChildren><one:OE><one:T><![CDATA[token $token]]></one:T></one:OE></one:OEChildren></one:Outline></one:Page>"
Invoke-GuardedWrite -Log $Log -Probe 'search-setup-write' -Method 'UpdatePageContent' -TargetId $pageId -Code 'param($on, $a) $on.UpdatePageContent($a.xml); "ok"' -A @{ xml = $xml } -LogArgs @{ pageId = $pageId; change = 'title + token outline' } | Out-Null
$written = Get-Date

# 3. Search
$findCode = 'param($on, $a) $x = ""; $on.FindPages($a.start, $a.q, [ref]$x, $a.unindexed, $false); $x'
$hit = { param($v) $x = ConvertTo-OneNoteXml $v; @{ hits = $x.Doc.SelectNodes('//one:Page', $x.Ns).Count; recycleBinHits = $x.Doc.SelectNodes('//one:Page[@isInRecycleBin="true"]', $x.Ns).Count; targetFound = [bool]$x.Doc.SelectSingleNode("//one:Page[@ID='$pageId']", $x.Ns); secondsSinceWrite = [Math]::Round(((Get-Date) - $written).TotalSeconds, 1) } }
function Find([string]$Probe, [bool]$Unindexed) {
    Invoke-Probe -Log $Log -Probe $Probe -Method 'FindPages' -Code $findCode -A @{ start = $nbId; q = $token; unindexed = $Unindexed } `
        -LogArgs @{ start = $nbId; query = 'unique token'; fIncludeUnindexedPages = $Unindexed } -TimeoutSec 60 -DetailScript $hit
}
Write-ProbeResult -Log $Log -Probe 'search-environment' -Method 'Get-Service WSearch' -Outcome 'ok' -Verdict 'info' -Ms 0 -Detail @{ wsearch = "$((Get-Service WSearch -ErrorAction SilentlyContinue).Status)"; notebookPathInCrawlScope = 'file:///C:\Users\ include rule; no exclude rule matches (registry check 2026-11-03)' }
$i0 = Find 'search-immediate-indexed-only' $false
$i1 = Find 'search-immediate-include-unindexed' $true
$foundIdx = $i0.ok -and $i0.value -match [regex]::Escape($pageId)
$foundUnx = $i1.ok -and $i1.value -match [regex]::Escape($pageId)
$firstIdx = if ($foundIdx) { 0 } else { $null }
$deadline = $written.AddSeconds($DelaySec)
while ((-not $foundIdx -or -not $foundUnx) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $PollSec
    if (-not $foundIdx) { $p = Find 'search-poll-indexed-only' $false; $foundIdx = $p.ok -and $p.value -match [regex]::Escape($pageId); if ($foundIdx) { $firstIdx = [Math]::Round(((Get-Date) - $written).TotalSeconds, 1) } }
    if (-not $foundUnx) { $p = Find 'search-poll-include-unindexed' $true; $foundUnx = $p.ok -and $p.value -match [regex]::Escape($pageId) }
}
# verdict semantics here: works = the indexed-only search found the page within the window.
Write-ProbeResult -Log $Log -Probe 'search-summary' -Method 'FindPages' -Outcome 'ok' -Verdict $(if ($foundIdx) { 'works' } else { 'fails' }) -Ms ([long]((Get-Date) - $written).TotalMilliseconds) `
    -Detail @{ foundIndexedOnly = $foundIdx; secondsToIndexedHit = $firstIdx; foundIncludeUnindexed = $foundUnx; delaySec = $DelaySec; pollSec = $PollSec; verdictMeans = 'indexed-only found the page' }

# 4. Counts
$h = Get-SpikeHierarchy -NotebookPath $nbPath
Write-ProbeResult -Log $Log -Probe 'search-notebook-page-count' -Method 'GetHierarchy' -CallArgs @{ notebookId = $nbId } -Outcome 'ok' -Verdict 'info' -Ms 0 -Detail @{
    pages = $h.Doc.SelectNodes('//one:Page', $h.Ns).Count
    pagesOutsideRecycleBin = $h.Doc.SelectNodes('//one:Page[not(@isInRecycleBin="true")]', $h.Ns).Count
    recycleBinPages = $h.Doc.SelectNodes('//one:SectionGroup[@isRecycleBin="true"]//one:Page', $h.Ns).Count
}
Reset-ComWorker
exit 0
