<#
.SYNOPSIS Snapshot (or compare) lastModifiedTime of every pre-existing notebook and section.
.DESCRIPTION
  Read-only. Run with -Label before ahead of the write probes and -Label after once the spike
  is done, then -Compare to diff them. The spike notebook is excluded. Snapshots hold IDs and
  timestamps only.
.EXAMPLE pwsh -File spikes/com-capabilities/00-baseline.ps1 -Label before
.EXAMPLE pwsh -File spikes/com-capabilities/00-baseline.ps1 -Compare
#>
param([string]$Label = 'before', [switch]$Compare)
Import-Module (Join-Path $PSScriptRoot 'OneNoteProbe.psm1') -Force
Use-WindowsPowerShell -ScriptPath $PSCommandPath -BoundParameters $PSBoundParameters

$results = Join-Path (Split-Path $PSScriptRoot -Parent) 'results'
if (-not (Test-Path $results)) { New-Item -ItemType Directory $results | Out-Null }
$Scope = Get-OneNoteEnum Scope

if ($Compare) {
    $b = Get-Content (Join-Path $results 'baseline-before.json') -Raw | ConvertFrom-Json
    $a = Get-Content (Join-Path $results 'baseline-after.json') -Raw | ConvertFrom-Json
    $after = @{}; foreach ($i in $a.items) { $after[$i.id] = $i }
    $changed = @(); $missing = @()
    foreach ($i in $b.items) {
        if (-not $after.ContainsKey($i.id)) { $missing += $i.id; continue }
        if ($after[$i.id].lastModifiedTime -ne $i.lastModifiedTime) { $changed += @{ id = $i.id; kind = $i.kind; before = $i.lastModifiedTime; after = $after[$i.id].lastModifiedTime } }
    }
    $verdict = if ($changed.Count -eq 0 -and $missing.Count -eq 0) { 'works' } else { 'fails' }
    Write-ProbeResult -Log 'baseline' -Probe 'untouched-check' -Method 'compare' -Outcome 'ok' -Verdict $verdict -Ms 0 -Detail @{ compared = @($b.items).Count; changed = $changed; missing = $missing; beforeTs = $b.ts; afterTs = $a.ts }
    if ($verdict -ne 'works') { Write-Host "CHANGED: $($changed.Count)  MISSING: $($missing.Count)"; exit 1 }
    Write-Host "All $(@($b.items).Count) pre-existing notebooks/sections unchanged."
    exit 0
}

$r = Invoke-OneNote -Code (Get-HierarchyCode) -A @{ id = ''; scope = $Scope.Sections }
if (-not $r.ok) { Write-ProbeResult -Log 'baseline' -Probe "snapshot-$Label" -Method 'GetHierarchy' -CallArgs @{ scope = 'hsSections' } -Outcome 'error' -HResult $r.hresult -Ms $r.ms; exit 1 }
$x = ConvertTo-OneNoteXml $r.value
$spikePath = (Get-SpikeNotebookPath).TrimEnd('\')
$items = @()
foreach ($nb in $x.Doc.SelectNodes('/one:Notebooks/one:Notebook', $x.Ns)) {
    if ($nb.path.TrimEnd('\') -ieq $spikePath) { continue }
    $items += [ordered]@{ kind = 'notebook'; id = $nb.ID; lastModifiedTime = $nb.lastModifiedTime }
    foreach ($s in $nb.SelectNodes('.//one:Section', $x.Ns)) {
        $items += [ordered]@{ kind = 'section'; id = $s.ID; lastModifiedTime = $s.GetAttribute('lastModifiedTime') }
    }
}
$snap = [ordered]@{ ts = (Get-Date).ToString('o'); label = $Label; items = $items }
$snap | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $results "baseline-$Label.json")
Write-ProbeResult -Log 'baseline' -Probe "snapshot-$Label" -Method 'GetHierarchy' -CallArgs @{ scope = 'hsSections' } -Outcome 'ok' -Verdict 'info' -Ms $r.ms -Detail @{ notebooks = @($items | Where-Object { $_.kind -eq 'notebook' }).Count; sections = @($items | Where-Object { $_.kind -eq 'section' }).Count }
exit 0
