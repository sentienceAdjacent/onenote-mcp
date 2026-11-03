# OneNoteProbe.psm1 -- shared helper for the COM capability spike (ticket 1).
#
# What it gives every probe:
#   * a COM worker: one dedicated STA runspace that owns the OneNote.Application object.
#     Every COM call runs there, so a hung call can be timed out (default 30 s) and the
#     worker thrown away without hanging the probe script.
#   * the ID guard: every write/rename/delete/publish/navigate goes through
#     Invoke-GuardedWrite, which re-reads the spike notebook's hierarchy and refuses any
#     target ID that is not inside it -- before any COM call is made.
#   * a JSONL result logger: one row per probe (method, args, outcome, HRESULT, ms).
#     Rows carry IDs and counts only, never page text or notebook names.
#
# Must run under Windows PowerShell 5.1. Under pwsh 7 every late-bound call with a [ref]
# out-param fails with E_FAIL (0x80004005) -- see the findings doc. Probe scripts call
# Use-WindowsPowerShell first, which re-launches them under powershell.exe.

Set-StrictMode -Version 2

$script:Ns = 'http://schemas.microsoft.com/office/onenote/2013/onenote'
$script:SpikeRoot = Join-Path $env:LOCALAPPDATA 'OneNoteMcpSpike'
$script:SpikeNotebookName = 'MCP Spike'
# Second spike notebook (ticket 1 search re-run): inside the Windows Search crawl scope, outside OneDrive and the repo.
$script:IndexedSpikeRoot = Join-Path $env:USERPROFILE 'OneNoteMcpSpikeIndexed'
$script:IndexedSpikeNotebookName = 'MCP Spike Indexed'
$script:ResultsDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'results'
$script:Worker = $null   # @{ Runspace = ...; Id = n }
$script:WorkerSeq = 0

# OneNote enums (Microsoft.Office.Interop.OneNote), as ints for late binding.
$script:Scope = @{ Self = 0; Children = 1; Notebooks = 2; Sections = 3; Pages = 4 }
$script:PageInfo = @{ Basic = 0; BinaryData = 1; Selection = 2; BinaryDataSelection = 3; FileType = 4; BinaryDataFileType = 5; SelectionFileType = 6; All = 7 }
$script:CreateFileType = @{ None = 0; Notebook = 1; Folder = 2; Section = 3 }
$script:PublishFormat = @{ OneNote = 0; OneNotePackage = 1; MHTML = 2; PDF = 3; XPS = 4; Word = 5; EMF = 6; HTML = 7; OneNote2007 = 8 }
$script:NewPageStyle = @{ Default = 0; BlankPageWithTitle = 1; BlankPageNoTitle = 2 }

# OneNote HRESULTs, from learn.microsoft.com "Error codes (OneNote)", fetched 2026-11-03.
$script:HResultNames = @{
    '0x80042000' = 'hrMalformedXML'
    '0x80042001' = 'hrInvalidXML'
    '0x80042002' = 'hrCreatingSection'
    '0x80042003' = 'hrOpeningSection'
    '0x80042004' = 'hrSectionDoesNotExist'
    '0x80042005' = 'hrPageDoesNotExist'
    '0x80042006' = 'hrFileDoesNotExist'
    '0x80042007' = 'hrInsertingImage'
    '0x80042008' = 'hrInsertingInk'
    '0x80042009' = 'hrInsertingHtml'
    '0x8004200a' = 'hrNavigatingToPage'
    '0x8004200b' = 'hrSectionReadOnly'
    '0x8004200c' = 'hrPageReadOnly'
    '0x8004200d' = 'hrInsertingOutlineText'
    '0x8004200e' = 'hrPageObjectDoesNotExist'
    '0x8004200f' = 'hrBinaryObjectDoesNotExist'
    '0x80042010' = 'hrLastModifiedDateDidNotMatch'
    '0x80042011' = 'hrGroupDoesNotExist'
    '0x80042012' = 'hrPageDoesNotExistInGroup'
    '0x80042013' = 'hrNoActiveSelection'
    '0x80042014' = 'hrObjectDoesNotExist'
    '0x80042015' = 'hrNotebookDoesNotExist'
    '0x80042016' = 'hrInsertingFile'
    '0x80042017' = 'hrInvalidName'
    '0x80042018' = 'hrFolderDoesNotExist'
    '0x80042019' = 'hrInvalidQuery'
    '0x8004201a' = 'hrFileAlreadyExists'
    '0x8004201b' = 'hrSectionEncryptedAndLocked'
    '0x8004201c' = 'hrDisabledByPolicy'
    '0x8004201d' = 'hrNotYetSynchronized'
    '0x8004201e' = 'hrLegacySection'
    '0x8004201f' = 'hrMergeFailed'
    '0x80042020' = 'hrInvalidXMLSchema'
    '0x80042022' = 'hrFutureContentLoss'
    '0x80042023' = 'hrTimeOut'
    '0x80042024' = 'hrRecordingInProgress'
    '0x80042025' = 'hrUnknownLinkedNoteState'
    '0x80042026' = 'hrNoShortNameForLinkedNote'
    '0x80042027' = 'hrNoFriendlyNameForLinkedNote'
    '0x80042028' = 'hrInvalidLinkedNoteUri'
    '0x80042029' = 'hrInvalidLinkedNoteThumbnail'
    '0x8004202a' = 'hrImportLNTThumbnailFailed'
    '0x8004202b' = 'hrUnreadDisabledForNotebook'
    '0x8004202c' = 'hrInvalidSelection'
    '0x8004202d' = 'hrConvertFailed'
    '0x8004202e' = 'hrRecycleBinEditFailed'
    '0x8004202f' = 'hrIMConversationTypeInvalid'
    '0x80042030' = 'hrAppInModalUI'
    '0x80042031' = 'hrPublishFormatUnsupportedForLabels'
    # Generic COM / RPC codes seen around activation and lost connections.
    '0x80004005' = 'E_FAIL'
    '0x80040154' = 'REGDB_E_CLASSNOTREG'
    '0x80080005' = 'CO_E_SERVER_EXEC_FAILURE'
    '0x800706ba' = 'RPC_S_SERVER_UNAVAILABLE'
    '0x800706be' = 'RPC_S_CALL_FAILED'
    '0x80010108' = 'RPC_E_DISCONNECTED'
    '0x80010001' = 'RPC_E_CALL_REJECTED'
    '0x8001010a' = 'RPC_E_SERVERCALL_RETRYLATER'
    '0x80070005' = 'E_ACCESSDENIED'
    '0x80070057' = 'E_INVALIDARG'
    '0x80020005' = 'DISP_E_TYPEMISMATCH'
    '0x8002000e' = 'DISP_E_BADPARAMCOUNT'
}

function Get-OneNoteNamespace { $script:Ns }
function Get-SpikeRoot { $script:SpikeRoot }
function Get-SpikeNotebookPath { Join-Path $script:SpikeRoot $script:SpikeNotebookName }
function Get-IndexedSpikeRoot { $script:IndexedSpikeRoot }
function Get-IndexedSpikeNotebookPath { Join-Path $script:IndexedSpikeRoot $script:IndexedSpikeNotebookName }
function Get-SpikeNotebookPaths { @((Get-SpikeNotebookPath), (Get-IndexedSpikeNotebookPath)) }
function Get-OneNoteEnum { param([ValidateSet('Scope', 'PageInfo', 'CreateFileType', 'PublishFormat', 'NewPageStyle')][string]$Name) (Get-Variable -Scope Script -Name $Name).Value }

function Use-WindowsPowerShell {
    # Re-launch the calling script under Windows PowerShell 5.1 when started from pwsh 7.
    param([Parameter(Mandatory)][string]$ScriptPath, [hashtable]$BoundParameters = @{})
    if ($PSVersionTable.PSEdition -ne 'Core') { return }
    $exe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath)
    foreach ($k in $BoundParameters.Keys) {
        $v = $BoundParameters[$k]
        if ($v -is [switch] -or $v -is [bool]) { if ($v) { $argList += "-$k" } } else { $argList += "-$k"; $argList += "$v" }
    }
    Write-Host "[probe] pwsh 7 cannot late-bind OneNote [ref] out-params; re-launching under Windows PowerShell 5.1"
    & $exe @argList
    exit $LASTEXITCODE
}

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    ([Security.Principal.WindowsPrincipal]::new($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Format-HResult {
    param([int]$HResult)
    $hex = '0x{0:x8}' -f $HResult
    $name = $script:HResultNames[$hex]
    if ($name) { "$hex $name" } else { $hex }
}

# ---------------------------------------------------------------- JSONL logger

function Write-ProbeResult {
    <#
    .SYNOPSIS Append one result row to spikes/results/<Log>.jsonl.
    Args and Detail must hold IDs, counts, sizes and flags only -- never text from a page.
    #>
    param(
        [Parameter(Mandatory)][string]$Log,
        [Parameter(Mandatory)][string]$Probe,
        [Parameter(Mandatory)][string]$Method,
        [hashtable]$CallArgs = @{},
        [Parameter(Mandatory)][ValidateSet('ok', 'error', 'timeout', 'refused', 'skipped')][string]$Outcome,
        # works = capability confirmed; fails = capability broken; pass = expected rejection observed;
        # not-tested = skipped with a reason; info = measurement only.
        [ValidateSet('works', 'fails', 'pass', 'not-tested', 'info')][string]$Verdict,
        [string]$HResult,
        [long]$Ms = -1,
        [hashtable]$Detail = @{},
        [string]$Scenario
    )
    if (-not (Test-Path $script:ResultsDir)) { New-Item -ItemType Directory -Path $script:ResultsDir | Out-Null }
    if (-not $Verdict) { $Verdict = if ($Outcome -eq 'ok') { 'works' } elseif ($Outcome -eq 'skipped') { 'not-tested' } else { 'fails' } }
    $row = [ordered]@{
        ts       = (Get-Date).ToString('o')
        probe    = $Probe
        method   = $Method
        args     = $CallArgs
        outcome  = $Outcome
        verdict  = $Verdict
        hresult  = $HResult
        ms       = $Ms
        detail   = $Detail
        scenario = $Scenario
        host     = "powershell $($PSVersionTable.PSVersion)"
        elevated = [bool](Test-Elevated)
    }
    $line = $row | ConvertTo-Json -Compress -Depth 6
    $path = Join-Path $script:ResultsDir "$Log.jsonl"
    [IO.File]::AppendAllText($path, $line + "`n", [Text.UTF8Encoding]::new($false))
    $tag = if ($HResult) { " [$HResult]" } else { '' }
    Write-Host ("[{0,-8}] {1,-10} {2} {3}ms{4}" -f $Verdict, $Outcome, "$Probe/$Method", $Ms, $tag)
}

# ---------------------------------------------------------------- STA COM worker

$script:WorkerScript = @'
param($Code, $A, [bool]$Reconnect)
$r = @{ ok = $false; value = $null; hresult = $null; hresultInt = 0; message = $null; ms = 0; apartment = [Threading.Thread]::CurrentThread.GetApartmentState().ToString() }
$sw = [Diagnostics.Stopwatch]::StartNew()
try {
    if ($Reconnect -or -not $global:OneNoteApp) {
        if ($global:OneNoteApp) { try { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($global:OneNoteApp) } catch {} }
        $global:OneNoteApp = $null
        $global:OneNoteApp = New-Object -ComObject OneNote.Application
    }
    $r.value = & ([scriptblock]::Create($Code)) $global:OneNoteApp $A
    $r.ok = $true
} catch {
    $e = $_.Exception
    while ($e.InnerException -and -not ($e -is [Runtime.InteropServices.COMException])) { $e = $e.InnerException }
    $r.hresultInt = $e.HResult
    $r.hresult = '0x{0:x8}' -f $e.HResult
    $r.message = $e.GetType().Name + ': ' + $e.Message
}
$r.ms = $sw.ElapsedMilliseconds
$r
'@

function Get-ComWorker {
    if ($script:Worker) { return $script:Worker }
    $rs = [runspacefactory]::CreateRunspace()
    $rs.ApartmentState = [Threading.ApartmentState]::STA
    $rs.ThreadOptions = [Management.Automation.Runspaces.PSThreadOptions]::ReuseThread
    $rs.Open()
    $script:WorkerSeq++
    $script:Worker = @{ Runspace = $rs; Id = $script:WorkerSeq }
    $script:Worker
}

function Reset-ComWorker {
    # Drop the worker. A worker stuck in a COM call is abandoned, not disposed (Dispose would block).
    param([switch]$Abandon)
    if (-not $script:Worker) { return }
    if (-not $Abandon) { try { $script:Worker.Runspace.Dispose() } catch {} }
    $script:Worker = $null
}

function Invoke-OneNote {
    <#
    .SYNOPSIS Run a COM call on the STA worker with a timeout. Never throws.
    .PARAMETER Code  Script text run as `param($on, $a)`; $on is the OneNote.Application object.
    .PARAMETER Reconnect  Release and re-create the COM object inside the timed call (measures activation).
    .OUTPUTS hashtable: ok, value, hresult (hex + name), hresultInt, message, ms, timedOut, apartment
    #>
    param(
        [Parameter(Mandatory)][string]$Code,
        $A = @{},
        [int]$TimeoutSec = 30,
        [switch]$Reconnect
    )
    $w = Get-ComWorker
    $ps = [powershell]::Create()
    $ps.Runspace = $w.Runspace
    [void]$ps.AddScript($script:WorkerScript).AddArgument($Code).AddArgument($A).AddArgument([bool]$Reconnect)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $handle = $ps.BeginInvoke()
    if (-not $handle.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds($TimeoutSec))) {
        Reset-ComWorker -Abandon
        return @{ ok = $false; value = $null; hresult = 'timeout'; hresultInt = 0; message = "no return after $TimeoutSec s; worker abandoned"; ms = $sw.ElapsedMilliseconds; timedOut = $true; apartment = 'STA' }
    }
    try {
        $out = $ps.EndInvoke($handle)
        $r = $out[$out.Count - 1]
        if ($r -isnot [hashtable]) { $r = $r.PSObject.BaseObject }
    } catch {
        $r = @{ ok = $false; value = $null; hresult = 'worker-error'; hresultInt = 0; message = $_.Exception.Message; ms = $sw.ElapsedMilliseconds; apartment = '?' }
    } finally { $ps.Dispose() }
    $r.timedOut = $false
    if ($r.hresultInt) { $r.hresult = Format-HResult $r.hresultInt }
    $r
}

function Invoke-Probe {
    <#
    .SYNOPSIS Invoke-OneNote + Write-ProbeResult in one step. Returns the call result.
    .PARAMETER Expect  A HRESULT hex (e.g. '0x80042010') whose occurrence counts as a pass.
    .PARAMETER DetailScript  Optional { param($value) @{...counts...} } run on success to build Detail.
    #>
    param(
        [Parameter(Mandatory)][string]$Log,
        [Parameter(Mandatory)][string]$Probe,
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Code,
        $A = @{},
        [hashtable]$LogArgs = @{},
        [string]$Expect,
        [scriptblock]$DetailScript,
        [hashtable]$Detail = @{},
        [int]$TimeoutSec = 30,
        [switch]$Reconnect,
        [string]$Scenario
    )
    $r = Invoke-OneNote -Code $Code -A $A -TimeoutSec $TimeoutSec -Reconnect:$Reconnect
    $d = @{} + $Detail
    if ($r.ok -and $DetailScript) { try { $d += (& $DetailScript $r.value) } catch { $d.detailError = $_.Exception.Message } }
    if (-not $r.ok -and $r.message) { $d.error = ($r.message -replace '\s+', ' ').Substring(0, [Math]::Min(200, $r.message.Length)) }
    $outcome = if ($r.timedOut) { 'timeout' } elseif ($r.ok) { 'ok' } else { 'error' }
    $verdict = $null
    if ($Expect) {
        if (-not $r.ok -and $r.hresult -like "$Expect*") { $verdict = 'pass' } else { $verdict = 'fails'; $d.expected = $Expect }
    }
    $hr = if ($r.ok) { $null } else { $r.hresult }
    $wp = @{ Log = $Log; Probe = $Probe; Method = $Method; CallArgs = $LogArgs; Outcome = $outcome; HResult = $hr; Ms = $r.ms; Detail = $d; Scenario = $Scenario }
    if ($verdict) { $wp.Verdict = $verdict }
    Write-ProbeResult @wp
    $r
}

# ---------------------------------------------------------------- hierarchy helpers

function ConvertTo-OneNoteXml {
    param([Parameter(Mandatory)][string]$Xml)
    $doc = [xml]$Xml
    $nsm = [Xml.XmlNamespaceManager]::new($doc.NameTable)
    $nsm.AddNamespace('one', $script:Ns)
    @{ Doc = $doc; Ns = $nsm }
}

$script:GetHierarchyCode = 'param($on, $a) $x = ""; $on.GetHierarchy($a.id, $a.scope, [ref]$x); $x'
function Get-HierarchyCode { $script:GetHierarchyCode }

function Get-SpikeNotebookId {
    # Resolve a spike notebook by its on-disk path (never by name -- names are not unique).
    param([string]$NotebookPath = (Get-SpikeNotebookPath))
    $r = Invoke-OneNote -Code $script:GetHierarchyCode -A @{ id = ''; scope = $script:Scope.Notebooks }
    if (-not $r.ok) { throw "guard: cannot read notebook list ($($r.hresult))" }
    $x = ConvertTo-OneNoteXml $r.value
    $want = $NotebookPath.TrimEnd('\')
    foreach ($nb in $x.Doc.SelectNodes('//one:Notebook', $x.Ns)) {
        if ($nb.path.TrimEnd('\') -ieq $want) { return $nb.ID }
    }
    $null
}

function Get-SpikeHierarchy {
    # GetHierarchy(spikeNotebookId, hsPages) -- the guard's source of truth.
    param([string]$NotebookPath = (Get-SpikeNotebookPath))
    $nbId = Get-SpikeNotebookId -NotebookPath $NotebookPath
    if (-not $nbId) { return $null }
    $r = Invoke-OneNote -Code $script:GetHierarchyCode -A @{ id = $nbId; scope = $script:Scope.Pages }
    if (-not $r.ok) { throw "guard: cannot read spike hierarchy ($($r.hresult))" }
    $x = ConvertTo-OneNoteXml $r.value
    $x.NotebookId = $nbId
    $x
}

function Test-SpikeTarget {
    <#
    .SYNOPSIS True only when $Id is the spike notebook or an element inside its current hierarchy.
    OneNote child IDs do not embed the parent's ID, so a prefix check is not possible; the guard
    looks the ID up in a fresh GetHierarchy(spikeNotebookId, hsPages) on every call.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Id)
    if ([string]::IsNullOrWhiteSpace($Id)) { return $false }
    # Either spike notebook qualifies (MCP Spike, MCP Spike Indexed); each is resolved by path.
    foreach ($nbPath in Get-SpikeNotebookPaths) {
        $h = Get-SpikeHierarchy -NotebookPath $nbPath
        if (-not $h) { continue }
        if ($Id -eq $h.NotebookId) { return $true }
        foreach ($n in $h.Doc.SelectNodes('//*[@ID]', $h.Ns)) { if ($n.ID -eq $Id) { return $true } }
    }
    $false
}

function Test-SpikePath {
    # True only for a file-system path under %LOCALAPPDATA%\OneNoteMcpSpike or %USERPROFILE%\OneNoteMcpSpikeIndexed.
    param([Parameter(Mandatory)][string]$Path)
    try { $full = [IO.Path]::GetFullPath($Path) } catch { return $false }
    foreach ($r in $script:SpikeRoot, $script:IndexedSpikeRoot) {
        $root = [IO.Path]::GetFullPath($r).TrimEnd('\') + '\'
        if ($full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    $false
}

function Invoke-GuardedWrite {
    <#
    .SYNOPSIS The one door for every write, rename, delete, publish or navigate call.
    Checks every -TargetId with Test-SpikeTarget and every -TargetPath with Test-SpikePath.
    A refusal is logged and no COM call is made. Otherwise behaves like Invoke-Probe.
    #>
    param(
        [Parameter(Mandatory)][string]$Log,
        [Parameter(Mandatory)][string]$Probe,
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Code,
        [string[]]$TargetId = @(),
        [string[]]$TargetPath = @(),
        $A = @{},
        [hashtable]$LogArgs = @{},
        [string]$Expect,
        [scriptblock]$DetailScript,
        [int]$TimeoutSec = 30,
        [string]$Scenario,
        # The verdict to log when the guard refuses. 'pass' for the foreign-ID probe, else 'fails'.
        [ValidateSet('pass', 'fails')][string]$RefusalVerdict = 'fails'
    )
    if ($TargetId.Count -eq 0 -and $TargetPath.Count -eq 0) { throw "Invoke-GuardedWrite: no target given for $Method" }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $bad = @()
    try {
        foreach ($id in $TargetId) { if (-not (Test-SpikeTarget -Id $id)) { $bad += "id:$id" } }
    } catch { $bad += "guard-error:$($_.Exception.Message)" }
    foreach ($p in $TargetPath) { if (-not (Test-SpikePath -Path $p)) { $bad += 'path-outside-spike-root' } }
    if ($bad.Count -gt 0) {
        $la = @{} + $LogArgs; $la.refusedTargets = $bad
        Write-ProbeResult -Log $Log -Probe $Probe -Method $Method -CallArgs $la -Outcome 'refused' -Verdict $RefusalVerdict -Ms $sw.ElapsedMilliseconds -Detail @{ guard = 'refused before COM call'; guardMs = $sw.ElapsedMilliseconds } -Scenario $Scenario
        return @{ ok = $false; refused = $true; hresult = 'refused-by-guard' }
    }
    $r = Invoke-Probe -Log $Log -Probe $Probe -Method $Method -Code $Code -A $A -LogArgs $LogArgs -Expect $Expect -DetailScript $DetailScript -TimeoutSec $TimeoutSec -Scenario $Scenario -Detail @{ guardMs = $sw.ElapsedMilliseconds }
    $r.refused = $false
    $r
}

Export-ModuleMember -Function Get-OneNoteNamespace, Get-SpikeRoot, Get-SpikeNotebookPath, Get-IndexedSpikeRoot, Get-IndexedSpikeNotebookPath, Get-SpikeNotebookPaths, Get-OneNoteEnum, Use-WindowsPowerShell, Test-Elevated, Format-HResult, Write-ProbeResult, Get-ComWorker, Reset-ComWorker, Invoke-OneNote, Invoke-Probe, ConvertTo-OneNoteXml, Get-HierarchyCode, Get-SpikeNotebookId, Get-SpikeHierarchy, Test-SpikeTarget, Test-SpikePath, Invoke-GuardedWrite
