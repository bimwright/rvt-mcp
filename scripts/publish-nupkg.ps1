#Requires -Version 5.1
<#
.SYNOPSIS
  Pack RvtMcp.Server as a .NET global tool and optionally push to nuget.org.

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
Write-Host "Packing $csproj"
& dotnet pack $csproj -c Release --output $outDir
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed: $LASTEXITCODE" }

$nupkg = Get-ChildItem $outDir -Filter 'RvtMcp.Server.*.nupkg' |
    Where-Object { $_.Name -notmatch '\.symbols\.' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $nupkg) { throw "No RvtMcp.Server nupkg in $outDir" }
Write-Host "Packed: $($nupkg.FullName)"

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
