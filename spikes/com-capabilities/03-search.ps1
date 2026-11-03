<#
.SYNOPSIS FindPages straight after a write, then polled until the page shows up or -DelaySec passes.
.DESCRIPTION
  Creates one page in the spike section (from results/spike-state.json, written by 02-write.ps1)
  holding a unique token, then runs FindPages for that token:
    * immediately, with fIncludeUnindexedPages = false and = true
    * every -PollSec seconds until both hit or -DelaySec elapses
  Also records a FindPages call scoped to the root (all notebooks; read-only, only hit counts kept)
  and an invalid query. Logs to spikes/results/search.jsonl.
#>
param([int]$DelaySec = 300, [int]$PollSec = 15)
Import-Module (Join-Path $PSScriptRoot 'OneNoteProbe.psm1') -Force
Use-WindowsPowerShell -ScriptPath $PSCommandPath -BoundParameters $PSBoundParameters

$Log = 'search'
$Ns = Get-OneNoteNamespace
$statePath = Join-Path (Split-Path $PSScriptRoot -Parent) 'results\spike-state.json'
if (-not (Test-Path $statePath)) { Write-Host 'run 02-write.ps1 first'; exit 1 }
$state = Get-Content $statePath -Raw | ConvertFrom-Json
$nbId = Get-SpikeNotebookId
if ($nbId -ne $state.notebookId) { Write-Host 'spike notebook ID changed; re-run 02-write.ps1'; exit 1 }

$token = 'zqspike' + ([guid]::NewGuid().ToString('N').Substring(0, 10))
$r = Invoke-GuardedWrite -Log $Log -Probe 'search-setup-page' -Method 'CreateNewPage' -TargetId $state.sectionId `
    -Code 'param($on, $a) $id = ""; $on.CreateNewPage($a.section, [ref]$id, 0); $id' -A @{ section = $state.sectionId } -LogArgs @{ sectionId = $state.sectionId }
if (-not $r.ok) { exit 1 }
$pageId = $r.value
$xml = "<?xml version=`"1.0`"?><one:Page xmlns:one=`"$Ns`" ID=`"$pageId`"><one:Title><one:OE><one:T><![CDATA[Search probe]]></one:T></one:OE></one:Title><one:Outline><one:OEChildren><one:OE><one:T><![CDATA[token $token]]></one:T></one:OE></one:OEChildren></one:Outline></one:Page>"
$w = Invoke-GuardedWrite -Log $Log -Probe 'search-setup-write' -Method 'UpdatePageContent' -TargetId $pageId -Code 'param($on, $a) $on.UpdatePageContent($a.xml); "ok"' -A @{ xml = $xml } -LogArgs @{ pageId = $pageId; change = 'title + token outline' }
$written = Get-Date

$findCode = 'param($on, $a) $x = ""; $on.FindPages($a.start, $a.q, [ref]$x, $a.unindexed, $false); $x'
$hit = { param($v) $x = ConvertTo-OneNoteXml $v; @{ hits = $x.Doc.SelectNodes('//one:Page', $x.Ns).Count; targetFound = [bool]$x.Doc.SelectSingleNode("//one:Page[@ID='$pageId']", $x.Ns); secondsSinceWrite = [Math]::Round(((Get-Date) - $written).TotalSeconds, 1) } }

function Find([string]$Probe, [bool]$Unindexed, [string]$Start = $nbId) {
    Invoke-Probe -Log $Log -Probe $Probe -Method 'FindPages' -Code $findCode -A @{ start = $Start; q = $token; unindexed = $Unindexed } `
        -LogArgs @{ start = $(if ($Start) { $Start } else { '(root)' }); query = 'unique token'; fIncludeUnindexedPages = $Unindexed } -TimeoutSec 60 -DetailScript $hit
}

$wsearch = (Get-Service WSearch -ErrorAction SilentlyContinue).Status
Write-ProbeResult -Log $Log -Probe 'search-environment' -Method 'Get-Service WSearch' -Outcome 'ok' -Verdict 'info' -Ms 0 -Detail @{ wsearch = "$wsearch" }

$i0 = Find 'search-immediate-indexed-only' $false
$i1 = Find 'search-immediate-include-unindexed' $true
Find 'search-immediate-root-include-unindexed' $true '' | Out-Null

$foundIdx = $i0.ok -and $i0.value -match [regex]::Escape($pageId)
$foundUnx = $i1.ok -and $i1.value -match [regex]::Escape($pageId)
$deadline = $written.AddSeconds($DelaySec)
while ((-not $foundIdx -or -not $foundUnx) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $PollSec
    if (-not $foundIdx) { $p = Find 'search-poll-indexed-only' $false; $foundIdx = $p.ok -and $p.value -match [regex]::Escape($pageId) }
    if (-not $foundUnx) { $p = Find 'search-poll-include-unindexed' $true; $foundUnx = $p.ok -and $p.value -match [regex]::Escape($pageId) }
}
Write-ProbeResult -Log $Log -Probe 'search-summary' -Method 'FindPages' -Outcome 'ok' -Verdict $(if ($foundIdx -or $foundUnx) { 'works' } else { 'fails' }) -Ms ([long]((Get-Date) - $written).TotalMilliseconds) `
    -Detail @{ foundIndexedOnly = $foundIdx; foundIncludeUnindexed = $foundUnx; delaySec = $DelaySec; pollSec = $PollSec }

# Is Windows Search indexing any notebook? A common word, root scope, indexed pages only (counts only).
$wordStats = { param($v) $x = ConvertTo-OneNoteXml $v; @{ hits = $x.Doc.SelectNodes('//one:Page', $x.Ns).Count; spikeHits = $x.Doc.SelectNodes("//one:Notebook[@ID='$nbId']//one:Page", $x.Ns).Count } }
foreach ($u in $false, $true) {
    Invoke-Probe -Log $Log -Probe "search-common-word-root-unindexed-$u" -Method 'FindPages' -Code $findCode -A @{ start = ''; q = 'the'; unindexed = $u } `
        -LogArgs @{ start = '(root)'; query = 'common word'; fIncludeUnindexedPages = $u } -TimeoutSec 60 -DetailScript $wordStats | Out-Null
}
# Spike notebook page count, to compare with include-unindexed hit counts.
$sh = Get-SpikeHierarchy
Write-ProbeResult -Log $Log -Probe 'search-spike-page-count' -Method 'GetHierarchy' -CallArgs @{ notebookId = $nbId } -Outcome 'ok' -Verdict 'info' -Ms 0 -Detail @{ pages = $sh.Doc.SelectNodes('//one:Page', $sh.Ns).Count; pagesOutsideRecycleBin = $sh.Doc.SelectNodes('//one:Page[not(@isInRecycleBin="true")]', $sh.Ns).Count }
Invoke-Probe -Log $Log -Probe 'search-invalid-query' -Method 'FindPages' -Code $findCode -A @{ start = $nbId; q = ''; unindexed = $false } -LogArgs @{ start = $nbId; query = '(empty)' } | Out-Null
Invoke-Probe -Log $Log -Probe 'search-bad-start-id' -Method 'FindPages' -Code $findCode -A @{ start = '{00000000-0000-0000-0000-000000000000}{1}{B0}'; q = $token; unindexed = $false } -LogArgs @{ start = '(nonexistent ID)' } | Out-Null
Reset-ComWorker
exit 0
