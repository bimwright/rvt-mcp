#Requires -Version 5.1
<#
.SYNOPSIS
  Pack RvtMcp.Server as a .NET global tool with a SHA-256 sidecar, optionally push to nuget.org.

.DESCRIPTION
  Does not read or store the API key in the repo. Pass it via -ApiKey or $env:NUGET_API_KEY.

.EXAMPLE
  pwsh scripts/publish-nupkg.ps1
  $env:NUGET_API_KEY = '<key>'; pwsh scripts/publish-nupkg.ps1 -Push
#>
[CmdletBinding()]
param(
    [string]$ApiKey = $env:NUGET_API_KEY,
    [switch]$Push,
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$RepoRoot = (Resolve-Path $RepoRoot).Path
$outDir = Join-Path $RepoRoot 'artifacts'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$csproj = Join-Path $RepoRoot 'src\server\RvtMcp.Server.csproj'
[xml]$project = Get-Content -LiteralPath $csproj -Raw
$versionNode = $project.SelectSingleNode('/Project/PropertyGroup/Version')
if (-not $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) { throw 'Server csproj has no package Version.' }
$version = $versionNode.InnerText.Trim()
Write-Host "Packing $csproj"
& dotnet pack $csproj -c Release --output $outDir
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed: $LASTEXITCODE" }

# Leftover artifacts may have newer timestamps; never hash or push a different version.
$packagePath = Join-Path $outDir ("RvtMcp.Server.{0}.nupkg" -f $version)
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) { throw "Expected package not found: $packagePath" }
$nupkg = Get-Item -LiteralPath $packagePath
$hash = (Get-FileHash -LiteralPath $nupkg.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$($nupkg.FullName).sha256" -Value ("{0}  {1}" -f $hash, $nupkg.Name) -Encoding ascii -NoNewline
Write-Host "Packed: $($nupkg.FullName)"
Write-Host "SHA-256: $hash  ($($nupkg.Name).sha256)"

if (-not $Push) {
    Write-Host "Dry pack only. To upload: set NUGET_API_KEY then re-run with -Push"
    return
}

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    throw 'NUGET_API_KEY is empty. Create a key at https://www.nuget.org/account/apikeys — do not commit it.'
}

Write-Host "Pushing to nuget.org..."
& dotnet nuget push $nupkg.FullName --api-key $ApiKey --source 'https://api.nuget.org/v3/index.json' --skip-duplicate
if ($LASTEXITCODE -ne 0) { throw "dotnet nuget push failed: $LASTEXITCODE" }
Write-Host 'Push accepted. Package page: https://www.nuget.org/packages/RvtMcp.Server'
