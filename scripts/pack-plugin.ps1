#Requires -Version 7
<#
.SYNOPSIS
  Builds the Claude Code plugin zip: dist/onenote-<ver>-win-x64.zip.

.DESCRIPTION
  Publishes OneNoteMcp.exe (self-contained, single-file, win-x64), stages it with plugin/,
  writes <Version> from Directory.Build.props into the staged plugin.json, and zips the
  staged plugin root. Writes the zip's SHA-256 beside it as dist/onenote-<ver>-win-x64.zip.sha256
  (sha256sum format: "<hex>  <file name>"), and prints the zip path and the hash. The release
  workflow (.github/workflows/release.yml) runs it and uploads exactly these bytes.

  <Version> is semver, optionally with a prerelease suffix (0.4.0-rc.1). No build metadata.

  Validate the staged plugin with: claude plugin validate --strict dist/stage/onenote
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/OneNoteMcp.Server/OneNoteMcp.Server.csproj'
$dist = Join-Path $root 'dist'
$publishDir = Join-Path $dist 'publish'
$stageDir = Join-Path $dist 'stage/onenote'

function Invoke-Native([string]$what, [scriptblock]$call) {
    $output = & $call
    if ($LASTEXITCODE -ne 0) {
        $output | Out-Host
        throw "$what failed with exit code $LASTEXITCODE."
    }
    return $output
}

$version = (Invoke-Native 'Reading <Version>' { dotnet msbuild $project -nologo -getProperty:Version }) | Select-Object -Last 1
$version = "$version".Trim()
# Keep in sync with VERSION_PATTERN in .github/workflows/release.yml (PluginPackagingTests checks it).
$versionPattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?$'
if ($version -cnotmatch $versionPattern) { throw "Unexpected <Version> '$version': expected semver, optionally with a prerelease suffix." }

$zipName = "onenote-$version-win-x64.zip"
$zip = Join-Path $dist $zipName
$shaFile = "$zip.sha256"

# Start clean: stale publish output, staging or zip from an earlier run never leaks in.
foreach ($path in @($publishDir, (Join-Path $dist 'stage'), $zip, $shaFile)) {
    if (Test-Path $path) { Remove-Item -Recurse -Force $path }
}

Invoke-Native 'dotnet publish' {
    dotnet publish $project -c $Configuration -r win-x64 -o $publishDir -nologo
} | Out-Host

$exe = Join-Path $publishDir 'OneNoteMcp.exe'
if (-not (Test-Path $exe)) { throw "Publish did not produce $exe." }

# Stage: plugin/ as-is, then the exe at the plugin root (no bin/ folder, AD-15).
New-Item -ItemType Directory -Force $stageDir | Out-Null
Get-ChildItem -Force (Join-Path $root 'plugin') | Copy-Item -Recurse -Force -Destination $stageDir
Copy-Item -Force $exe $stageDir

# The version lives only in the staged manifest (research note, Release flow 2).
$manifestPath = Join-Path $stageDir '.claude-plugin/plugin.json'
$manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json -AsHashtable
$ordered = [ordered]@{ name = $manifest['name']; version = $version }
foreach ($key in $manifest.Keys) { if ($key -notin 'name', 'version') { $ordered[$key] = $manifest[$key] } }
($ordered | ConvertTo-Json -Depth 10) + "`n" | Set-Content -NoNewline -Encoding utf8NoBOM $manifestPath

# Zip the plugin root's contents, so the zip root is the plugin root.
Compress-Archive -Path (Get-ChildItem -Force $stageDir | ForEach-Object FullName) -DestinationPath $zip -CompressionLevel Optimal

$hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
"$hash  $zipName`n" | Set-Content -NoNewline -Encoding utf8NoBOM $shaFile
Write-Output "zip:    $zip"
Write-Output "sha256 file: $shaFile"
Write-Output "sha256: $hash"
