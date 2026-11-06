<#
.SYNOPSIS
  Spike 1.15 cap probe: what Claude Code does with an MCP tool result past its output cap (MAX_MCP_OUTPUT_TOKENS).

.DESCRIPTION
  For each size, starts `claude -p` with one MCP server: the harness in --fake-server mode, whose one tool (`filler`)
  returns exactly that many characters of numbered 100-character filler lines ending in END-OF-FILLER. No notebook is
  involved. From the stream-json transcript it records, per size, what reached the model in the tool_result:
  whole (end marker present, length as sent), savedToFile (no filler at all, only Claude Code's notice that the result
  was saved to a file), persistedPreview (Claude Code's <persisted-output> block: the file path plus the first 2KB),
  truncated (some other cut), or error (tool_result is_error), plus the length of
  any text Claude Code added that isn't filler, and that text itself (it comes from Claude Code, never a notebook).

  -Filler words|xml picks plain words or XML-like tags, which tokenize at different characters per token, so the two
  thresholds tell a token cap from a character cap. -Variant structured returns the filler the way the real server
  does (the envelope as structuredContent and its JSON as the text block), to show whether structuredContent counts.
  Size 13 (the bare end marker) is a useful baseline for input-token deltas.

  Writes <Out>/cap-probe.jsonl (one row per run) and keeps each transcript as <Out>/cap-<variant>-<filler>-<size>.jsonl.
  Unsets MAX_MCP_OUTPUT_TOKENS for the child so the default cap applies, unless -KeepCapEnv is given.

.EXAMPLE
  pwsh -File spikes/stress-harness/probe-output-cap.ps1 -Out $env:TEMP\cap-probe
#>
param(
    [Parameter(Mandatory)] [string] $Out,
    [int[]] $Sizes = @(20000, 40000, 80000, 100000, 120000, 200000),
    [string] $Model = 'haiku',
    [ValidateSet('filler', 'structured')] [string] $Variant = 'filler',
    [ValidateSet('words', 'xml')] [string] $Filler = 'words',
    [string] $Harness,
    [switch] $KeepCapEnv
)

$ErrorActionPreference = 'Stop'
if (-not $Harness) {
    $Harness = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\StressHarness.exe'
}
if (-not (Test-Path $Harness)) {
    throw "Harness not built. Run: dotnet build spikes/stress-harness -c Release"
}
$Harness = (Resolve-Path $Harness).Path
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$work = Join-Path $Out 'cwd'
New-Item -ItemType Directory -Force $work | Out-Null

$savedCap = $env:MAX_MCP_OUTPUT_TOKENS
if (-not $KeepCapEnv) { Remove-Item Env:MAX_MCP_OUTPUT_TOKENS -ErrorAction SilentlyContinue }

$rows = @()
try {
    foreach ($size in $Sizes) {
        $tag = "$Variant-$Filler-$size"
        $config = Join-Path $Out "mcp-$tag.json"
        @{ mcpServers = @{ cap = @{ command = $Harness; args = @('--fake-server', "$size", '--fake-variant', $Variant, '--fake-filler', $Filler) } } } |
            ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 $config
        $transcript = Join-Path $Out "cap-$tag.jsonl"
        $prompt = 'Call the MCP tool mcp__cap__filler exactly once (load it with ToolSearch first if it is deferred). ' +
                  'Do not call any other tool afterwards. Then reply with one line: the number of the last L-numbered line you received, ' +
                  'and whether you saw END-OF-FILLER (yes/no).'
        Push-Location $work
        try {
            & claude -p $prompt --mcp-config $config --strict-mcp-config `
                --allowedTools 'mcp__cap__filler' 'ToolSearch' --model $Model `
                --output-format stream-json --verbose --no-session-persistence --disable-slash-commands `
                2> (Join-Path $Out "cap-$tag.err") | Set-Content -Encoding utf8 $transcript
        } finally { Pop-Location }

        $events = Get-Content $transcript | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json -Depth 50 }
        $toolUse = $events | Where-Object type -eq 'assistant' |
            ForEach-Object { $_.message.content } | Where-Object { $_.type -eq 'tool_use' -and $_.name -eq 'mcp__cap__filler' } |
            Select-Object -First 1
        $row = [ordered]@{ variant = $Variant; filler = $Filler; size = $size; called = [bool]$toolUse }
        if ($toolUse) {
            $result = $events | Where-Object type -eq 'user' | ForEach-Object { $_.message.content } |
                Where-Object { $_.type -eq 'tool_result' -and $_.tool_use_id -eq $toolUse.id } | Select-Object -First 1
            $text = if ($result.content -is [string]) { $result.content } else { ($result.content | Where-Object type -eq 'text' | ForEach-Object text) -join "`n" }
            # Line numbers are found anywhere, since the structured variant's text is one JSON line with newlines escaped.
            $numbered = @([regex]::Matches($text, 'L(\d{6}) ') | ForEach-Object { [int]$_.Groups[1].Value })
            $isNotice = $numbered.Count -eq 0 -and $text -match 'exceeds maximum allowed tokens'
            $added = if ($isNotice) { @($text) } elseif ($Variant -eq 'filler') {
                @($text -split "`n" | Where-Object { $_ -and $_ -notmatch '^L\d{6} ' -and $_ -ne 'END-OF-FILLER' }) } else { @() }
            $row.looksLikeEnvelope = $text.TrimStart().StartsWith('{')
            $row.isError = [bool]$result.is_error
            $row.receivedChars = $text.Length
            $row.endMarker = $text.Contains('END-OF-FILLER')
            $row.lastLine = if ($numbered.Count) { ($numbered | Measure-Object -Maximum).Maximum } else { 0 }
            $row.expectedLastLine = [math]::Ceiling(($size - 'END-OF-FILLER'.Length) / 100)
            # Claude Code 2.1.x doesn't cut an oversized result: it saves it to a file and the model gets only a notice.
            $row.outcome = if ($row.isError) { 'error' }
                elseif ($row.endMarker -and $numbered.Count -ge $row.expectedLastLine) { 'whole' }
                elseif ($isNotice) { 'savedToFile' }
                elseif ($text.Contains('<persisted-output>')) { 'persistedPreview' }
                else { 'truncated' }
            $row.addedChars = [int](($added | Measure-Object -Property Length -Sum).Sum)
            $row.addedText = ($added -join "`n")
        }
        $final = $events | Where-Object type -eq 'result' | Select-Object -Last 1
        $row.modelReply = $final.result
        $row.inputTokens = $final.usage.input_tokens + $final.usage.cache_creation_input_tokens + $final.usage.cache_read_input_tokens
        $rows += [pscustomobject]$row
        ($row | ConvertTo-Json -Compress) | Add-Content -Encoding utf8 (Join-Path $Out 'cap-probe.jsonl')
        Write-Host ("$Variant/$Filler size {0}: {1} (received {2} chars, end marker {3})" -f $size, $row.outcome, $row.receivedChars, $row.endMarker)
    }
} finally {
    if ($null -ne $savedCap) { $env:MAX_MCP_OUTPUT_TOKENS = $savedCap }
}

$rows | Select-Object variant, filler, size, called, outcome, receivedChars, lastLine, expectedLastLine, endMarker, addedChars | Format-Table -AutoSize
