<#
.SYNOPSIS Read-capability probes. Read-only against every notebook.
.DESCRIPTION
  * COM activation time and the apartment the worker runs in
  * GetHierarchy at every scope from the root, plus the XML namespace returned
  * GetHierarchy with an explicit schema argument (xs2010 / xs2013)
  * pwsh 7 late binding versus Windows PowerShell 5.1
  * timing on the largest real notebook: GetHierarchy(nb, hsPages) and GetPageContent on a
    sample of its pages with piBasic and piBinaryData
  Logs to spikes/results/read.jsonl. Only IDs, sizes, counts and timings are logged.
.PARAMETER SamplePages  How many pages of the largest notebook to read (default 25).
#>
param([int]$SamplePages = 25)
Import-Module (Join-Path $PSScriptRoot 'OneNoteProbe.psm1') -Force
Use-WindowsPowerShell -ScriptPath $PSCommandPath -BoundParameters $PSBoundParameters

$Log = 'read'
$Scope = Get-OneNoteEnum Scope
$PageInfo = Get-OneNoteEnum PageInfo
$hcode = Get-HierarchyCode

function Get-XmlStats($xml) {
    $x = ConvertTo-OneNoteXml $xml
    $root = $x.Doc.DocumentElement
    @{
        bytes          = $xml.Length
        rootElement    = $root.LocalName
        namespace      = $root.NamespaceURI
        notebooks      = $x.Doc.SelectNodes('//one:Notebook', $x.Ns).Count
        sectionGroups  = $x.Doc.SelectNodes('//one:SectionGroup', $x.Ns).Count
        sections       = $x.Doc.SelectNodes('//one:Section', $x.Ns).Count
        pages          = $x.Doc.SelectNodes('//one:Page', $x.Ns).Count
        lockedSections = $x.Doc.SelectNodes('//one:Section[@locked="true"]', $x.Ns).Count
        encryptedSections = $x.Doc.SelectNodes('//one:Section[@encrypted="true"]', $x.Ns).Count
        recycleBins    = $x.Doc.SelectNodes('//one:SectionGroup[@isRecycleBin="true"]', $x.Ns).Count
    }
}

# 1. Activation (re-create the COM object inside a timed call; OneNote already running).
Invoke-Probe -Log $Log -Probe 'activation' -Method 'New-Object OneNote.Application' -Reconnect `
    -Code 'param($on, $a) [Threading.Thread]::CurrentThread.GetApartmentState().ToString()' `
    -DetailScript { param($v) @{ apartment = $v; oneNoteRunning = [bool](Get-Process ONENOTE -ErrorAction SilentlyContinue) } } | Out-Null

# 2. GetHierarchy at every scope from the root.
foreach ($s in 'Self', 'Children', 'Notebooks', 'Sections', 'Pages') {
    Invoke-Probe -Log $Log -Probe "hierarchy-root-$s" -Method 'GetHierarchy' -Code $hcode `
        -A @{ id = ''; scope = $Scope[$s] } -LogArgs @{ start = '(root)'; scope = "hs$s" } `
        -TimeoutSec 120 -DetailScript { param($v) Get-XmlStats $v } | Out-Null
}

# 3. Explicit schema argument.
foreach ($xs in @(@{ n = 'xs2010'; v = 1 }, @{ n = 'xs2013'; v = 2 })) {
    Invoke-Probe -Log $Log -Probe "hierarchy-schema-$($xs.n)" -Method 'GetHierarchy' `
        -Code 'param($on, $a) $x = ""; $on.GetHierarchy("", 2, [ref]$x, $a.xs); $x' -A @{ xs = $xs.v } `
        -LogArgs @{ start = '(root)'; scope = 'hsNotebooks'; xsSchema = $xs.n } `
        -DetailScript { param($v) $d = Get-XmlStats $v; @{ namespace = $d.namespace; bytes = $d.bytes } } | Out-Null
}

# 4. pwsh 7 late binding (run out of process so this script stays on 5.1).
$pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
if ($pwsh) {
    $probe = '$on = New-Object -ComObject OneNote.Application; $x = ""; try { $on.GetHierarchy("", 2, [ref]$x); "ok:" + $x.Length } catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; "err:" + ("0x{0:x8}" -f $e.HResult) }'
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($probe))
    $out = (& $pwsh.Source -NoProfile -EncodedCommand $enc) | Select-Object -Last 1
    $ver = (& $pwsh.Source -NoProfile -Command '$PSVersionTable.PSVersion.ToString()')
    if ($out -like 'ok:*') {
        Write-ProbeResult -Log $Log -Probe 'host-pwsh7-latebinding' -Method 'GetHierarchy' -CallArgs @{ host = "pwsh $ver"; scope = 'hsNotebooks' } -Outcome 'ok' -Ms $sw.ElapsedMilliseconds -Detail @{ bytes = [int]($out -replace 'ok:', '') }
    } else {
        $hr = Format-HResult ([Convert]::ToInt32(($out -replace 'err:0x', ''), 16))
        Write-ProbeResult -Log $Log -Probe 'host-pwsh7-latebinding' -Method 'GetHierarchy' -CallArgs @{ host = "pwsh $ver"; scope = 'hsNotebooks' } -Outcome 'error' -HResult $hr -Ms $sw.ElapsedMilliseconds -Detail @{ note = 'IDispatch late binding with a [ref] BSTR out-param; Windows PowerShell 5.1 succeeds on the same call' }
    }
}

# 5. Largest real notebook (by page count), read-only.
$all = Invoke-OneNote -Code $hcode -A @{ id = ''; scope = $Scope.Pages } -TimeoutSec 120
if (-not $all.ok) { Write-Host "cannot read full hierarchy: $($all.hresult)"; exit 1 }
$x = ConvertTo-OneNoteXml $all.value
$spikePath = (Get-SpikeNotebookPath).TrimEnd('\')
$largest = $x.Doc.SelectNodes('/one:Notebooks/one:Notebook', $x.Ns) |
    Where-Object { $_.path.TrimEnd('\') -ine $spikePath } |
    ForEach-Object { [pscustomobject]@{ Node = $_; Pages = $_.SelectNodes('.//one:Page[not(@isInRecycleBin="true")]', $x.Ns).Count } } |
    Sort-Object Pages -Descending | Select-Object -First 1
if (-not $largest) { Write-Host 'no real notebook found'; exit 1 }
$nbId = $largest.Node.ID
$nbKind = if ($largest.Node.path -like 'http*') { 'cloud-url' } else { 'local-path' }

Invoke-Probe -Log $Log -Probe 'largest-notebook-hierarchy' -Method 'GetHierarchy' -Code $hcode `
    -A @{ id = $nbId; scope = $Scope.Pages } -LogArgs @{ start = $nbId; scope = 'hsPages' } -TimeoutSec 120 `
    -DetailScript { param($v) Get-XmlStats $v } | Out-Null

# Sample pages, skipping locked sections and the recycle bin.
$pages = @($largest.Node.SelectNodes('.//one:Section[not(@locked="true")]/one:Page[not(@isInRecycleBin="true")]', $x.Ns))
$sample = if ($pages.Count -le $SamplePages) { $pages } else {
    $step = [Math]::Floor($pages.Count / $SamplePages); 0..($SamplePages - 1) | ForEach-Object { $pages[$_ * $step] }
}
$pcCode = 'param($on, $a) $x = ""; $on.GetPageContent($a.id, [ref]$x, $a.pi); $x'
$pageStats = {
    param($v)
    $px = ConvertTo-OneNoteXml $v
    @{
        bytes       = $v.Length
        namespace   = $px.Doc.DocumentElement.NamespaceURI
        outlines    = $px.Doc.SelectNodes('//one:Outline', $px.Ns).Count
        images      = $px.Doc.SelectNodes('//one:Image', $px.Ns).Count
        files       = $px.Doc.SelectNodes('//one:InsertedFile', $px.Ns).Count
        ink         = $px.Doc.SelectNodes('//one:InkDrawing|//one:InkParagraph|//one:InkWord', $px.Ns).Count
        dataNodes   = $px.Doc.SelectNodes('//one:Data', $px.Ns).Count
        callbackIds = $px.Doc.SelectNodes('//one:CallbackID', $px.Ns).Count
    }
}
$totals = @{ basicMs = 0; binaryMs = 0; basicBytes = 0; binaryBytes = 0; errors = 0; withImages = 0 }
foreach ($p in $sample) {
    $b = Invoke-Probe -Log $Log -Probe 'page-content-basic' -Method 'GetPageContent' -Code $pcCode -A @{ id = $p.ID; pi = $PageInfo.Basic } `
        -LogArgs @{ pageId = $p.ID; pageInfo = 'piBasic' } -TimeoutSec 60 -DetailScript $pageStats
    $d = Invoke-Probe -Log $Log -Probe 'page-content-binary' -Method 'GetPageContent' -Code $pcCode -A @{ id = $p.ID; pi = $PageInfo.BinaryData } `
        -LogArgs @{ pageId = $p.ID; pageInfo = 'piBinaryData' } -TimeoutSec 60 -DetailScript $pageStats
    if ($b.ok) { $totals.basicMs += $b.ms; $totals.basicBytes += $b.value.Length } else { $totals.errors++ }
    if ($d.ok) { $totals.binaryMs += $d.ms; $totals.binaryBytes += $d.value.Length } else { $totals.errors++ }
    if ($b.ok -and $b.value -match '<one:Image') { $totals.withImages++ }
}
# Binary data for one image by callback ID (GetBinaryPageContent), when a sampled page has one.
foreach ($p in $sample) {
    $r = Invoke-OneNote -Code $pcCode -A @{ id = $p.ID; pi = $PageInfo.Basic } -TimeoutSec 60
    if (-not $r.ok) { continue }
    $px = ConvertTo-OneNoteXml $r.value
    $cb = $px.Doc.SelectSingleNode('//one:Image/one:CallbackID', $px.Ns)
    if (-not $cb) { continue }
    Invoke-Probe -Log $Log -Probe 'binary-page-content' -Method 'GetBinaryPageContent' `
        -Code 'param($on, $a) $b = ""; $on.GetBinaryPageContent($a.page, $a.cb, [ref]$b); $b' -A @{ page = $p.ID; cb = $cb.callbackID } `
        -LogArgs @{ pageId = $p.ID; callbackId = $cb.callbackID } -Detail @{ notebookKind = $nbKind } -DetailScript { param($v) @{ base64Chars = $v.Length } } | Out-Null
    break
}
Write-ProbeResult -Log $Log -Probe 'largest-notebook-summary' -Method 'GetPageContent' -CallArgs @{ notebookId = $nbId } -Outcome 'ok' -Verdict 'info' -Ms ($totals.basicMs + $totals.binaryMs) -Detail @{
    notebookKind = $nbKind; notebookPages = $largest.Pages; readablePages = $pages.Count; sampled = @($sample).Count; errors = $totals.errors
    avgBasicMs = if (@($sample).Count) { [Math]::Round($totals.basicMs / @($sample).Count, 1) } else { 0 }
    avgBinaryMs = if (@($sample).Count) { [Math]::Round($totals.binaryMs / @($sample).Count, 1) } else { 0 }
    basicBytes = $totals.basicBytes; binaryBytes = $totals.binaryBytes; pagesWithImages = $totals.withImages
}
Reset-ComWorker
exit 0
