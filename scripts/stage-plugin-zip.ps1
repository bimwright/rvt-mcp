<#
.SYNOPSIS
  Stage RvtMcp plugin shells into build/plugin-zip/ for release packaging.

.DESCRIPTION
  For each Revit year R22..R27, copies the built plugin DLL + .addin + runtime deps
  from src/plugin-rNN/bin/<Config>/<TFM>/ into build/plugin-zip/R<nn>/, then produces
  build/plugin-zip/RvtMcp.Plugin.R<nn>.zip.

  Consumed by install.ps1 (P4-003) and the GitHub Release asset pipeline (P4-004).

.PARAMETER Config
  Build configuration to source DLLs from. Default: Release.

.PARAMETER RepoRoot
  Repo root path. Default: parent directory of the scripts/ folder.

.EXAMPLE
  pwsh scripts/stage-plugin-zip.ps1
  pwsh scripts/stage-plugin-zip.ps1 -Config Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Config = 'Release',
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

# year-number → TFM mapping (must match plugin-rNN/*.csproj TargetFramework)
$shells = @(
    @{ Year = 22; Tfm = 'net48' },
    @{ Year = 23; Tfm = 'net48' },
    @{ Year = 24; Tfm = 'net48' },
    @{ Year = 25; Tfm = 'net8.0-windows7.0' },
    @{ Year = 26; Tfm = 'net8.0-windows7.0' },
    @{ Year = 27; Tfm = 'net10.0-windows7.0' }
)

$stageRoot = Join-Path $RepoRoot 'build\plugin-zip'
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null

$staged = @()
$skipped = @()

foreach ($s in $shells) {
    $year = $s.Year
    $tfm = $s.Tfm
    $pluginDir = Join-Path $RepoRoot ("src\plugin-r{0}" -f $year)
    $binDir = Join-Path $pluginDir ("bin\{0}\{1}" -f $Config, $tfm)
    $pluginDll = Join-Path $binDir 'RvtMcp.Plugin.dll'
    $addinFile = Join-Path $pluginDir ("RvtMcp.R{0}.addin" -f $year)

    if (-not (Test-Path $pluginDll)) {
        Write-Warning "R$year not staged — missing $pluginDll (build first with: dotnet build $pluginDir -c $Config)"
        $skipped += "R$year"
        continue
    }
    if (-not (Test-Path $addinFile)) {
        Write-Warning "R$year not staged — missing addin manifest $addinFile"
        $skipped += "R$year"
        continue
    }

    $destDir = Join-Path $stageRoot ("R{0}" -f $year)
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null

    # Runtime deps that ship alongside RvtMcp.Plugin.dll.
    # Glob matches the Deploy target in src/plugin-r*/*.csproj — keep in sync.
    $patterns = @(
        'RvtMcp.*.dll',
        'Newtonsoft.Json.dll',
        'Microsoft.Data.Sqlite.dll',
        'SQLitePCLRaw*.dll',
        'Microsoft.CodeAnalysis*.dll',
        'System.Collections.Immutable.dll',   # net48 only, harmless glob-miss on net8/10
        'System.Reflection.Metadata.dll',     # net48 only
        'System.Memory.dll',                  # SQLitePCLRaw net48 dependencies
        'System.Buffers.dll',
        'System.Numerics.Vectors.dll',
        'System.Runtime.CompilerServices.Unsafe.dll'
    )
    foreach ($p in $patterns) {
        Get-ChildItem -Path $binDir -Filter $p -File -ErrorAction SilentlyContinue |
            Copy-Item -Destination $destDir -Force
    }

    # Native SQLite runtime. Revit is Windows x64 only for this package, so do
    # not ship Linux/macOS/wasm native assets from Microsoft.Data.Sqlite.
    $nativeSqlite = Join-Path $binDir 'runtimes\win-x64\native\e_sqlite3.dll'
    if (-not (Test-Path $nativeSqlite)) {
        throw "Native SQLite missing for Revit 20${year}; rebuild before packaging."
    }
    $nativeDest = Join-Path $destDir 'runtimes\win-x64\native'
    New-Item -ItemType Directory -Path $nativeDest -Force | Out-Null
    Copy-Item -Path $nativeSqlite -Destination $nativeDest -Force

    # Revit add-ins are not launched by dotnet.exe, so native assets under
    # runtimes/win-x64/native are not always resolved by the host. Keep a
    # root copy beside RvtMcp.Plugin.dll for Microsoft.Data.Sqlite.
    Copy-Item -Path $nativeSqlite -Destination $destDir -Force

    # Addin manifest at plugin root.
    Copy-Item -Path $addinFile -Destination $destDir -Force

    $zipPath = Join-Path $stageRoot ("RvtMcp.Plugin.R{0}.zip" -f $year)
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $destDir '*') -DestinationPath $zipPath -Force

    $size = (Get-Item $zipPath).Length
    Write-Host ("[R{0}] staged -> {1} ({2:N0} bytes)" -f $year, $zipPath, $size)
    $staged += "R$year"
}

Write-Host ""
Write-Host "=== stage-plugin-zip summary ==="
Write-Host ("Config : {0}" -f $Config)
Write-Host ("Staged : {0}" -f ($staged -join ', '))
if ($skipped.Count -gt 0) {
    Write-Host ("Skipped: {0} (not built)" -f ($skipped -join ', '))
}
Write-Host ("Output : {0}" -f $stageRoot)

if ($staged.Count -eq 0) {
    throw "No plugin shells staged. Build at least one with: dotnet build src/plugin-r<nn> -c $Config"
}
