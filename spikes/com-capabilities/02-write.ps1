<#
.SYNOPSIS Write-capability probes. Every write targets the "MCP Spike" notebook only.
.DESCRIPTION
  Creates (or reopens) the spike notebook at %LOCALAPPDATA%\OneNoteMcpSpike\MCP Spike, then:
  section create (UpdateHierarchy and OpenHierarchy), section rename, CreateNewPage,
  UpdatePageContent append and edit with and without dateExpectedLastModified, the stale-date
  conflict, image insert + binary read-back, Publish to PDF and docx, NavigateTo,
  DeleteHierarchy (recycle bin, permanent, stale date) and the guard refusing a foreign ID.
  Every write call goes through Invoke-GuardedWrite. Logs to spikes/results/write.jsonl.
  Writes the IDs the search probe needs to spikes/results/spike-state.json.
#>
param()
Import-Module (Join-Path $PSScriptRoot 'OneNoteProbe.psm1') -Force
Use-WindowsPowerShell -ScriptPath $PSCommandPath -BoundParameters $PSBoundParameters

$Log = 'write'
$Ns = Get-OneNoteNamespace
$Scope = Get-OneNoteEnum Scope
$run = (Get-Date).ToString('yyyyMMdd-HHmmss')
$root = Get-SpikeRoot
$nbPath = Get-SpikeNotebookPath
if (-not (Test-Path $root)) { New-Item -ItemType Directory $root | Out-Null }

$updCode = 'param($on, $a) $on.UpdateHierarchy($a.xml); "ok"'
$pageCode = 'param($on, $a) $x = ""; $on.GetPageContent($a.id, [ref]$x, 0); $x'
$upcNoDate = 'param($on, $a) $on.UpdatePageContent($a.xml); "ok"'
$upcDate = 'param($on, $a) $on.UpdatePageContent($a.xml, $a.date); "ok"'
$newPageCode = 'param($on, $a) $id = ""; $on.CreateNewPage($a.section, [ref]$id, 0); $id'
$delDateCodeEarly = 'param($on, $a) $on.DeleteHierarchy($a.id, $a.date, $a.permanent); "ok"'

function Read-Page([string]$Id) {
    $r = Invoke-OneNote -Code $pageCode -A @{ id = $Id }
    if (-not $r.ok) { throw "read page failed: $($r.hresult)" }
    ConvertTo-OneNoteXml $r.value
}
function Get-PageDate([string]$Id) {
    # lastModifiedTime as a UTC DateTime (OneNote writes it as ISO 8601 with Z).
    $p = Read-Page $Id
    $s = $p.Doc.DocumentElement.lastModifiedTime
    @{ text = $s; utc = [datetime]::Parse($s, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() }
}
function New-TitleXml([string]$PageId, [string]$Title, [string]$Body) {
    $o = if ($Body) { "<one:Outline><one:OEChildren><one:OE><one:T><![CDATA[$Body]]></one:T></one:OE></one:OEChildren></one:Outline>" } else { '' }
    "<?xml version=`"1.0`"?><one:Page xmlns:one=`"$Ns`" ID=`"$PageId`"><one:Title><one:OE><one:T><![CDATA[$Title]]></one:T></one:OE></one:Title>$o</one:Page>"
}
function New-SpikePage([string]$Probe, [string]$SectionId, [string]$Title) {
    $r = Invoke-GuardedWrite -Log $Log -Probe $Probe -Method 'CreateNewPage' -TargetId $SectionId -Code $newPageCode -A @{ section = $SectionId } -LogArgs @{ sectionId = $SectionId; style = 'npsDefault' } -DetailScript { param($v) @{ newPageId = $v } }
    if (-not $r.ok) { return $null }
    $id = $r.value
    $t = Invoke-GuardedWrite -Log $Log -Probe "$Probe-title" -Method 'UpdatePageContent' -TargetId $id -Code $upcNoDate -A @{ xml = (New-TitleXml $id $Title) } -LogArgs @{ pageId = $id; change = 'set title'; dateExpectedLastModified = '(none)' }
    $id
}
function Find-ByName([string]$XPath) {
    $h = Get-SpikeHierarchy
    $h.Doc.SelectSingleNode($XPath, $h.Ns)
}

# ------------------------------------------------------------------ 1. spike notebook
$r = Invoke-GuardedWrite -Log $Log -Probe 'open-spike-notebook' -Method 'OpenHierarchy' -TargetPath $nbPath `
    -Code 'param($on, $a) $id = ""; $on.OpenHierarchy($a.path, "", [ref]$id, 1); $id' -A @{ path = $nbPath } `
    -LogArgs @{ path = '%LOCALAPPDATA%\OneNoteMcpSpike\MCP Spike'; createFileType = 'cftNotebook' } -DetailScript { param($v) @{ notebookId = $v } }
if (-not $r.ok) { Write-Host 'cannot open spike notebook; stopping'; exit 1 }
$nbId = $r.value
if ((Get-SpikeNotebookId) -ne $nbId) { Write-Host 'guard resolves a different spike notebook ID; stopping'; exit 1 }
$h = Get-SpikeHierarchy
$sectionsBefore = $h.Doc.SelectNodes('//one:Section', $h.Ns).Count

# ------------------------------------------------------------------ 2. section create
$nameA = "Probe $run"
$xmlA = "<?xml version=`"1.0`"?><one:Notebook xmlns:one=`"$Ns`" ID=`"$nbId`"><one:Section name=`"$nameA`" /></one:Notebook>"
$r = Invoke-GuardedWrite -Log $Log -Probe 'section-create-updatehierarchy' -Method 'UpdateHierarchy' -TargetId $nbId -Code $updCode -A @{ xml = $xmlA } -LogArgs @{ notebookId = $nbId; change = 'add Section' }
$secA = Find-ByName "//one:Section[@name='$nameA']"
$h = Get-SpikeHierarchy
Write-ProbeResult -Log $Log -Probe 'section-create-verify' -Method 'GetHierarchy' -CallArgs @{ notebookId = $nbId } -Outcome 'ok' -Verdict $(if ($secA) { 'works' } else { 'fails' }) -Ms 0 `
    -Detail @{ sectionFound = [bool]$secA; sectionId = $(if ($secA) { $secA.ID }); sectionsBefore = $sectionsBefore; sectionsAfter = $h.Doc.SelectNodes('//one:Section', $h.Ns).Count; otherSectionsKept = ($h.Doc.SelectNodes('//one:Section', $h.Ns).Count -ge $sectionsBefore) }
if (-not $secA) { Write-Host 'section A missing; stopping'; exit 1 }
$secAId = $secA.ID

$fileB = "Probe B $run.one"
$r = Invoke-GuardedWrite -Log $Log -Probe 'section-create-openhierarchy' -Method 'OpenHierarchy' -TargetId $nbId `
    -Code 'param($on, $a) $id = ""; $on.OpenHierarchy($a.file, $a.nb, [ref]$id, 3); $id' -A @{ file = $fileB; nb = $nbId } `
    -LogArgs @{ parentId = $nbId; createFileType = 'cftSection' } -DetailScript { param($v) @{ sectionId = $v } }
$secBId = if ($r.ok) { $r.value } else { $null }

# ------------------------------------------------------------------ 3. section rename
$renamed = "Probe $run renamed"
$r = Invoke-GuardedWrite -Log $Log -Probe 'section-rename-section-root' -Method 'UpdateHierarchy' -TargetId $secAId -Code $updCode `
    -A @{ xml = "<?xml version=`"1.0`"?><one:Section xmlns:one=`"$Ns`" ID=`"$secAId`" name=`"$renamed`" />" } -LogArgs @{ sectionId = $secAId; change = 'rename; Section as XML root' }
if (-not $r.ok) {
    $r = Invoke-GuardedWrite -Log $Log -Probe 'section-rename-notebook-root' -Method 'UpdateHierarchy' -TargetId $nbId, $secAId -Code $updCode `
        -A @{ xml = "<?xml version=`"1.0`"?><one:Notebook xmlns:one=`"$Ns`" ID=`"$nbId`"><one:Section ID=`"$secAId`" name=`"$renamed`" /></one:Notebook>" } -LogArgs @{ sectionId = $secAId; change = 'rename; Notebook as XML root' }
}
$s = Find-ByName "//one:Section[@ID='$secAId']"
Write-ProbeResult -Log $Log -Probe 'section-rename-verify' -Method 'GetHierarchy' -CallArgs @{ sectionId = $secAId } -Outcome 'ok' -Verdict $(if ($s -and $s.name -eq $renamed) { 'works' } else { 'fails' }) -Ms 0 -Detail @{ renamed = [bool]($s -and $s.name -eq $renamed); idStable = [bool]$s }

# ------------------------------------------------------------------ 4. page create
$pageId = New-SpikePage 'page-create' $secAId "Spike page $run"
if (-not $pageId) { Write-Host 'page create failed; stopping'; exit 1 }

# ------------------------------------------------------------------ 5. append outline
$appendXml = "<?xml version=`"1.0`"?><one:Page xmlns:one=`"$Ns`" ID=`"$pageId`"><one:Outline><one:OEChildren><one:OE><one:T><![CDATA[Line one]]></one:T></one:OE></one:OEChildren></one:Outline></one:Page>"
Invoke-GuardedWrite -Log $Log -Probe 'append-outline-nodate' -Method 'UpdatePageContent' -TargetId $pageId -Code $upcNoDate -A @{ xml = $appendXml } -LogArgs @{ pageId = $pageId; change = 'add Outline'; dateExpectedLastModified = '(none)' } | Out-Null
$d = Get-PageDate $pageId
$appendXml2 = $appendXml -replace 'Line one', 'Line two'
Invoke-GuardedWrite -Log $Log -Probe 'append-outline-withdate' -Method 'UpdatePageContent' -TargetId $pageId -Code $upcDate -A @{ xml = $appendXml2; date = $d.utc } -LogArgs @{ pageId = $pageId; change = 'add Outline'; dateExpectedLastModified = $d.text; dateKind = 'UTC' } | Out-Null
$p = Read-Page $pageId
Write-ProbeResult -Log $Log -Probe 'append-verify' -Method 'GetPageContent' -CallArgs @{ pageId = $pageId } -Outcome 'ok' -Verdict $(if ($p.Doc.SelectNodes('//one:Outline', $p.Ns).Count -ge 2) { 'works' } else { 'fails' }) -Ms 0 -Detail @{ outlines = $p.Doc.SelectNodes('//one:Outline', $p.Ns).Count }

# ------------------------------------------------------------------ 6. edit existing text, date check
function Get-FirstOE($p) {
    $o = $p.Doc.SelectSingleNode('//one:Outline', $p.Ns)
    $oe = $o.SelectSingleNode('.//one:OE', $p.Ns)
    @{ outline = $o.objectID; oe = $oe.objectID }
}
function New-EditXml($ids, [string]$text) {
    "<?xml version=`"1.0`"?><one:Page xmlns:one=`"$Ns`" ID=`"$pageId`"><one:Outline objectID=`"$($ids.outline)`"><one:OEChildren><one:OE objectID=`"$($ids.oe)`"><one:T><![CDATA[$text]]></one:T></one:OE></one:OEChildren></one:Outline></one:Page>"
}
$ids = Get-FirstOE (Read-Page $pageId)
$t0 = Get-PageDate $pageId
Start-Sleep -Seconds 2
$edit = Invoke-GuardedWrite -Log $Log -Probe 'edit-text-withdate-utc' -Method 'UpdatePageContent' -TargetId $pageId -Code $upcDate `
    -A @{ xml = (New-EditXml $ids 'Line one, edited'); date = $t0.utc } -LogArgs @{ pageId = $pageId; change = 'edit T by objectID'; dateExpectedLastModified = $t0.text; dateKind = 'UTC' }
$dateKind = 'UTC'; $staleDate = $t0.utc
if (-not $edit.ok -and $edit.hresult -like '0x80042010*') {
    $edit = Invoke-GuardedWrite -Log $Log -Probe 'edit-text-withdate-local' -Method 'UpdatePageContent' -TargetId $pageId -Code $upcDate `
        -A @{ xml = (New-EditXml $ids 'Line one, edited'); date = $t0.utc.ToLocalTime() } -LogArgs @{ pageId = $pageId; change = 'edit T by objectID'; dateExpectedLastModified = $t0.text; dateKind = 'Local' }
    $dateKind = 'Local'; $staleDate = $t0.utc.ToLocalTime()
}
$p = Read-Page $pageId
$oeNode = $p.Doc.SelectSingleNode("//one:OE[@objectID='$($ids.oe)']/one:T", $p.Ns)
Write-ProbeResult -Log $Log -Probe 'edit-verify' -Method 'GetPageContent' -CallArgs @{ pageId = $pageId } -Outcome 'ok' -Verdict $(if ($oeNode -and $oeNode.InnerText -like '*edited*') { 'works' } else { 'fails' }) -Ms 0 -Detail @{ edited = [bool]($oeNode -and $oeNode.InnerText -like '*edited*'); dateKindAccepted = $(if ($edit.ok) { $dateKind } else { 'none' }); outlinesAfterEdit = $p.Doc.SelectNodes('//one:Outline', $p.Ns).Count }

Start-Sleep -Seconds 2
Invoke-GuardedWrite -Log $Log -Probe 'edit-text-nodate' -Method 'UpdatePageContent' -TargetId $pageId -Code $upcNoDate `
    -A @{ xml = (New-EditXml $ids 'Line one, edited twice') } -LogArgs @{ pageId = $pageId; change = 'edit T by objectID'; dateExpectedLastModified = '(none)' } | Out-Null

# ------------------------------------------------------------------ 7. stale-date conflict
$cur = Get-PageDate $pageId
Invoke-GuardedWrite -Log $Log -Probe 'edit-stale-date' -Method 'UpdatePageContent' -TargetId $pageId -Code $upcDate -Expect '0x80042010' `
    -A @{ xml = (New-EditXml $ids 'SHOULD NOT APPEAR'); date = $staleDate } -LogArgs @{ pageId = $pageId; change = 'edit T'; dateExpectedLastModified = "$($t0.text) (stale)"; currentLastModified = $cur.text; dateKind = $dateKind } | Out-Null
$p = Read-Page $pageId
Write-ProbeResult -Log $Log -Probe 'edit-stale-verify' -Method 'GetPageContent' -CallArgs @{ pageId = $pageId } -Outcome 'ok' -Verdict $(if ($p.Doc.OuterXml -notmatch 'SHOULD NOT APPEAR') { 'works' } else { 'fails' }) -Ms 0 -Detail @{ staleEditApplied = ($p.Doc.OuterXml -match 'SHOULD NOT APPEAR') }

# ------------------------------------------------------------------ 7b. does lastModifiedTime track COM edits?
# Found while building 7: a page's lastModifiedTime only advanced on COM edits while the page was
# the one displayed in OneNote. This probe records that for a page displayed and not displayed.
foreach ($displayed in $false, $true) {
    $tag = if ($displayed) { 'displayed' } else { 'not-displayed' }
    $tp = New-SpikePage "lmt-trace-$tag-setup" $secAId "LMT trace $tag $run"
    if (-not $tp) { continue }
    if ($displayed) {
        Invoke-GuardedWrite -Log $Log -Probe "lmt-trace-$tag-navigate" -Method 'NavigateTo' -TargetId $tp -Code 'param($on, $a) $on.NavigateTo($a.id, "", $false); "ok"' -A @{ id = $tp } -LogArgs @{ pageId = $tp } | Out-Null
    } else {
        Invoke-GuardedWrite -Log $Log -Probe "lmt-trace-$tag-navigate-away" -Method 'NavigateTo' -TargetId $pageId -Code 'param($on, $a) $on.NavigateTo($a.id, "", $false); "ok"' -A @{ id = $pageId } -LogArgs @{ pageId = $pageId; note = 'display a different page' } | Out-Null
    }
    $seen = @((Get-PageDate $tp).text)
    foreach ($i in 1..3) {
        Start-Sleep -Seconds 2
        Invoke-GuardedWrite -Log $Log -Probe "lmt-trace-$tag-edit" -Method 'UpdatePageContent' -TargetId $tp -Code $upcNoDate -A @{ xml = (New-TitleXml $tp "LMT trace $tag $i") } -LogArgs @{ pageId = $tp; change = "title edit $i" } | Out-Null
        $seen += (Get-PageDate $tp).text
    }
    $advances = 0; for ($i = 1; $i -lt $seen.Count; $i++) { if ($seen[$i] -gt $seen[$i - 1]) { $advances++ } }
    $s0 = Get-PageDate $tp
    Start-Sleep -Seconds 2
    Invoke-GuardedWrite -Log $Log -Probe "lmt-trace-$tag-edit" -Method 'UpdatePageContent' -TargetId $tp -Code $upcNoDate -A @{ xml = (New-TitleXml $tp "LMT trace $tag pre-stale") } -LogArgs @{ pageId = $tp; change = 'title edit before stale write' } | Out-Null
    $s1 = Get-PageDate $tp
    $stale = Invoke-GuardedWrite -Log $Log -Probe "lmt-trace-$tag-stale-write" -Method 'UpdatePageContent' -TargetId $tp -Code $upcDate -Expect '0x80042010' `
        -A @{ xml = (New-TitleXml $tp "LMT trace $tag stale"); date = $s0.utc } -LogArgs @{ pageId = $tp; dateExpectedLastModified = "$($s0.text) (stale)"; currentLastModified = $s1.text }
    Write-ProbeResult -Log $Log -Probe "lmt-trace-$tag" -Method 'GetPageContent' -CallArgs @{ pageId = $tp; displayed = $displayed } -Outcome 'ok' -Verdict $(if ($advances -eq 3) { 'works' } else { 'fails' }) -Ms 0 `
        -Detail @{ edits = 3; lmtAdvances = $advances; lmtSeries = $seen; staleWriteRejected = [bool]($stale.hresult -like '0x80042010*') }
    $dd = Get-PageDate $tp
    Invoke-GuardedWrite -Log $Log -Probe "lmt-trace-$tag-cleanup" -Method 'DeleteHierarchy' -TargetId $tp -Code $delDateCodeEarly -A @{ id = $tp; date = $dd.utc; permanent = $true } -LogArgs @{ pageId = $tp; deletePermanently = $true } | Out-Null
}

# ------------------------------------------------------------------ 8. image insert + binary read-back
Add-Type -AssemblyName System.Drawing
$bmp = New-Object Drawing.Bitmap 32, 32
$g = [Drawing.Graphics]::FromImage($bmp); $g.Clear([Drawing.Color]::SteelBlue); $g.Dispose()
$ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
$b64 = [Convert]::ToBase64String($ms.ToArray())
$imgXml = "<?xml version=`"1.0`"?><one:Page xmlns:one=`"$Ns`" ID=`"$pageId`"><one:Outline><one:OEChildren><one:OE><one:Image format=`"png`"><one:Data>$b64</one:Data></one:Image></one:OE></one:OEChildren></one:Outline></one:Page>"
Invoke-GuardedWrite -Log $Log -Probe 'image-insert' -Method 'UpdatePageContent' -TargetId $pageId -Code $upcNoDate -A @{ xml = $imgXml } -LogArgs @{ pageId = $pageId; change = 'add Image (png 32x32)'; dataBytes = $ms.Length } | Out-Null
$pcCode = 'param($on, $a) $x = ""; $on.GetPageContent($a.id, [ref]$x, $a.pi); $x'
$stats = { param($v) $px = ConvertTo-OneNoteXml $v; @{ bytes = $v.Length; images = $px.Doc.SelectNodes('//one:Image', $px.Ns).Count; dataNodes = $px.Doc.SelectNodes('//one:Image/one:Data', $px.Ns).Count; callbackIds = $px.Doc.SelectNodes('//one:Image/one:CallbackID', $px.Ns).Count } }
$basic = Invoke-Probe -Log $Log -Probe 'spike-page-content-basic' -Method 'GetPageContent' -Code $pcCode -A @{ id = $pageId; pi = 0 } -LogArgs @{ pageId = $pageId; pageInfo = 'piBasic'; notebookKind = 'local-path' } -DetailScript $stats
Invoke-Probe -Log $Log -Probe 'spike-page-content-binary' -Method 'GetPageContent' -Code $pcCode -A @{ id = $pageId; pi = 1 } -LogArgs @{ pageId = $pageId; pageInfo = 'piBinaryData'; notebookKind = 'local-path' } -DetailScript $stats | Out-Null
if ($basic.ok) {
    $bx = ConvertTo-OneNoteXml $basic.value
    $cb = $bx.Doc.SelectSingleNode('//one:Image/one:CallbackID', $bx.Ns)
    if ($cb) {
        Invoke-Probe -Log $Log -Probe 'spike-binary-page-content' -Method 'GetBinaryPageContent' -Code 'param($on, $a) $b = ""; $on.GetBinaryPageContent($a.page, $a.cb, [ref]$b); $b' `
            -A @{ page = $pageId; cb = $cb.callbackID } -LogArgs @{ pageId = $pageId; callbackId = $cb.callbackID; notebookKind = 'local-path' } -DetailScript { param($v) @{ base64Chars = $v.Length; roundTripMatches = ($v -eq $b64) } } | Out-Null
    } else {
        Write-ProbeResult -Log $Log -Probe 'spike-binary-page-content' -Method 'GetBinaryPageContent' -Outcome 'skipped' -Detail @{ reason = 'no CallbackID on the image in the piBasic read' }
    }
}

# ------------------------------------------------------------------ 9. Publish
$pubDir = Join-Path $root 'publish'
if (-not (Test-Path $pubDir)) { New-Item -ItemType Directory $pubDir | Out-Null }
foreach ($f in @(@{ n = 'PDF'; v = 3; ext = 'pdf' }, @{ n = 'Word'; v = 5; ext = 'docx' }, @{ n = 'MHTML'; v = 2; ext = 'mht' })) {
    $out = Join-Path $pubDir "page-$run.$($f.ext)"
    Invoke-GuardedWrite -Log $Log -Probe "publish-$($f.ext)" -Method 'Publish' -TargetId $pageId -TargetPath $out `
        -Code 'param($on, $a) $on.Publish($a.id, $a.path, $a.fmt, ""); if (Test-Path $a.path) { (Get-Item $a.path).Length } else { -1 }' `
        -A @{ id = $pageId; path = $out; fmt = $f.v } -LogArgs @{ pageId = $pageId; format = "pf$($f.n)"; target = "%LOCALAPPDATA%\OneNoteMcpSpike\publish\page-$run.$($f.ext)" } `
        -TimeoutSec 60 -DetailScript { param($v) @{ fileBytes = $v } } | Out-Null
}
# Publish to an existing file -> expect hrFileAlreadyExists.
$existing = Join-Path $pubDir "page-$run.pdf"
Invoke-GuardedWrite -Log $Log -Probe 'publish-existing-file' -Method 'Publish' -TargetId $pageId -TargetPath $existing -Expect '0x8004201a' `
    -Code 'param($on, $a) $on.Publish($a.id, $a.path, 3, ""); "ok"' -A @{ id = $pageId; path = $existing } -LogArgs @{ pageId = $pageId; format = 'pfPDF'; target = '(file exists)' } | Out-Null

# ------------------------------------------------------------------ 10. NavigateTo
Invoke-GuardedWrite -Log $Log -Probe 'navigate-to-page' -Method 'NavigateTo' -TargetId $pageId -Code 'param($on, $a) $on.NavigateTo($a.id, "", $false); "ok"' -A @{ id = $pageId } -LogArgs @{ pageId = $pageId; newWindow = $false } | Out-Null
Invoke-GuardedWrite -Log $Log -Probe 'get-hyperlink' -Method 'GetHyperlinkToObject' -TargetId $pageId -Code 'param($on, $a) $l = ""; $on.GetHyperlinkToObject($a.id, "", [ref]$l); $l' -A @{ id = $pageId } -LogArgs @{ pageId = $pageId } -DetailScript { param($v) @{ scheme = ($v -split ':')[0]; length = $v.Length } } | Out-Null

# ------------------------------------------------------------------ 11. delete
$delCode = 'param($on, $a) $on.DeleteHierarchy($a.id); "ok"'
$delDateCode = 'param($on, $a) $on.DeleteHierarchy($a.id, $a.date, $a.permanent); "ok"'
function Get-RecycleState([string]$Id) {
    $h = Get-SpikeHierarchy
    $n = $h.Doc.SelectSingleNode("//*[@ID='$Id']", $h.Ns)
    @{
        idPresent       = [bool]$n
        isInRecycleBin  = [bool]($n -and $n.GetAttribute('isInRecycleBin') -eq 'true')
        recycleBinPages = $h.Doc.SelectNodes('//one:SectionGroup[@isRecycleBin="true"]//one:Page', $h.Ns).Count
        recycleBinSections = $h.Doc.SelectNodes('//one:SectionGroup[@isRecycleBin="true"]/one:Section[not(@isDeletedPages="true")]', $h.Ns).Count
    }
}
# 11a. soft delete -> recycle bin
$p2 = New-SpikePage 'delete-soft-setup' $secAId "Delete soft $run"
$before = Get-RecycleState $p2
Invoke-GuardedWrite -Log $Log -Probe 'delete-page-default' -Method 'DeleteHierarchy' -TargetId $p2 -Code $delCode -A @{ id = $p2 } -LogArgs @{ pageId = $p2; deletePermanently = '(default)' } | Out-Null
$after = Get-RecycleState $p2
Write-ProbeResult -Log $Log -Probe 'delete-page-recycle-check' -Method 'GetHierarchy' -CallArgs @{ pageId = $p2 } -Outcome 'ok' -Verdict 'info' -Ms 0 -Detail @{ before = $before; after = $after; landedInRecycleBin = ($after.isInRecycleBin -or $after.recycleBinPages -gt $before.recycleBinPages) }
# 11b. permanent delete, with the current date
$p3 = New-SpikePage 'delete-permanent-setup' $secAId "Delete hard $run"
$d3 = Get-PageDate $p3
$before = Get-RecycleState $p3
Invoke-GuardedWrite -Log $Log -Probe 'delete-page-permanent' -Method 'DeleteHierarchy' -TargetId $p3 -Code $delDateCode -A @{ id = $p3; date = $d3.utc; permanent = $true } -LogArgs @{ pageId = $p3; deletePermanently = $true; dateExpectedLastModified = $d3.text } | Out-Null
$after = Get-RecycleState $p3
Write-ProbeResult -Log $Log -Probe 'delete-permanent-check' -Method 'GetHierarchy' -CallArgs @{ pageId = $p3 } -Outcome 'ok' -Verdict $(if (-not $after.idPresent -and $after.recycleBinPages -eq $before.recycleBinPages) { 'works' } else { 'fails' }) -Ms 0 -Detail @{ before = $before; after = $after }
# 11c. delete with a stale date
$p4 = New-SpikePage 'delete-stale-setup' $secAId "Delete stale $run"
$d4 = Get-PageDate $p4
Start-Sleep -Seconds 2
Invoke-GuardedWrite -Log $Log -Probe 'delete-stale-setup-edit' -Method 'UpdatePageContent' -TargetId $p4 -Code $upcNoDate -A @{ xml = (New-TitleXml $p4 "Delete stale $run" 'changed after date read') } -LogArgs @{ pageId = $p4; change = 'add Outline' } | Out-Null
$cur4 = Get-PageDate $p4
Invoke-GuardedWrite -Log $Log -Probe 'delete-stale-date' -Method 'DeleteHierarchy' -TargetId $p4 -Code $delDateCode -Expect '0x80042010' -A @{ id = $p4; date = $(if ($dateKind -eq 'Local') { $d4.utc.ToLocalTime() } else { $d4.utc }); permanent = $true } -LogArgs @{ pageId = $p4; deletePermanently = $true; dateExpectedLastModified = "$($d4.text) (stale)"; currentLastModified = $cur4.text } | Out-Null
$st = Get-RecycleState $p4
Write-ProbeResult -Log $Log -Probe 'delete-stale-check' -Method 'GetHierarchy' -CallArgs @{ pageId = $p4 } -Outcome 'ok' -Verdict $(if ($st.idPresent -and -not $st.isInRecycleBin) { 'works' } else { 'fails' }) -Ms 0 -Detail @{ pageSurvived = $st.idPresent }
# 11d. delete a section
if ($secBId) {
    $before = Get-RecycleState $secBId
    Invoke-GuardedWrite -Log $Log -Probe 'delete-section-default' -Method 'DeleteHierarchy' -TargetId $secBId -Code $delCode -A @{ id = $secBId } -LogArgs @{ sectionId = $secBId; deletePermanently = '(default)' } | Out-Null
    $after = Get-RecycleState $secBId
    Write-ProbeResult -Log $Log -Probe 'delete-section-recycle-check' -Method 'GetHierarchy' -CallArgs @{ sectionId = $secBId } -Outcome 'ok' -Verdict 'info' -Ms 0 -Detail @{ before = $before; after = $after; landedInRecycleBin = ($after.isInRecycleBin -or $after.recycleBinSections -gt $before.recycleBinSections) }
}

# ------------------------------------------------------------------ 12. guard refuses foreign targets
$all = Invoke-OneNote -Code (Get-HierarchyCode) -A @{ id = ''; scope = $Scope.Pages } -TimeoutSec 120
$ax = ConvertTo-OneNoteXml $all.value
$foreignPage = $ax.Doc.SelectNodes('/one:Notebooks/one:Notebook', $ax.Ns) | Where-Object { $_.ID -ne $nbId } |
    ForEach-Object { $_.SelectSingleNode('.//one:Page', $ax.Ns) } | Where-Object { $_ } | Select-Object -First 1
$foreignNb = $ax.Doc.SelectNodes('/one:Notebooks/one:Notebook', $ax.Ns) | Where-Object { $_.ID -ne $nbId } | Select-Object -First 1
# The code block below must never run: it throws, so even a guard bug cannot modify anything.
$breach = 'param($on, $a) throw "GUARD BREACH: foreign target reached the COM call"'
foreach ($t in @(@{ probe = 'guard-foreign-page'; id = $foreignPage.ID; m = 'DeleteHierarchy' }, @{ probe = 'guard-foreign-notebook'; id = $foreignNb.ID; m = 'UpdateHierarchy' }, @{ probe = 'guard-empty-id'; id = ''; m = 'UpdatePageContent' })) {
    Invoke-GuardedWrite -Log $Log -Probe $t.probe -Method $t.m -TargetId $t.id -Code $breach -RefusalVerdict 'pass' -LogArgs @{ targetId = $t.id; note = 'expected refusal' } | Out-Null
}
Invoke-GuardedWrite -Log $Log -Probe 'guard-foreign-path' -Method 'Publish' -TargetId $pageId -TargetPath (Join-Path $env:USERPROFILE 'Documents\spike-should-not-exist.pdf') -Code $breach -RefusalVerdict 'pass' -LogArgs @{ target = '%USERPROFILE%\Documents\...'; note = 'expected refusal' } | Out-Null

# ------------------------------------------------------------------ state for the search probe
$state = [ordered]@{ run = $run; notebookId = $nbId; sectionId = $secAId; pageId = $pageId }
$state | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path (Split-Path $PSScriptRoot -Parent) 'results\spike-state.json')
Reset-ComWorker
exit 0
