<#
.SYNOPSIS
  Install or uninstall Bimwright Revit plugin(s) for every installed Revit year.

.DESCRIPTION
  Detects installed Revit years via HKLM:\SOFTWARE\Autodesk\Revit\<year>\ and, for
  each year that has a matching build/plugin-zip/Bimwright.Rvt.Plugin.R<nn>.zip, extracts
  the zip to %APPDATA%\Autodesk\Revit\Addins\<year>\Bimwright\ and copies the .addin
  manifest up to %APPDATA%\Autodesk\Revit\Addins\<year>\.

  With -Uninstall, removes both the Bimwright\ folder and the Bimwright.R<nn>.addin
  file for every detected year.

  The script ships inside the release ZIP alongside the per-version plugin zips, so
  end-users run it directly without needing the repo checked out.

.PARAMETER SourceDir
  Directory containing Bimwright.Rvt.Plugin.R<nn>.zip files. Default: build/plugin-zip/
  relative to the repo root (parent of scripts/). Release bundles override this.

.PARAMETER Uninstall
  Remove the plugin from every detected Revit year.

.PARAMETER Years
  Optional explicit list of years (e.g. 2023,2025). Default: auto-detect via registry.

.EXAMPLE
  pwsh scripts/install.ps1
  pwsh scripts/install.ps1 -Uninstall
  pwsh scripts/install.ps1 -Years 2023,2025 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SourceDir,
    [switch]$Uninstall,
    [int[]]$Years
)

$ErrorActionPreference = 'Stop'

if (-not $SourceDir) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $SourceDir = Join-Path $repoRoot 'build\plugin-zip'
}

function Get-InstalledRevitYears {
    $detected = @()
    $root = 'HKLM:\SOFTWARE\Autodesk\Revit'
    if (-not (Test-Path $root)) { return $detected }
    foreach ($year in 2022..2027) {
        $yearKey = Join-Path $root "$year"
        if (Test-Path $yearKey) { $detected += $year }
    }
    return $detected
}

function Get-AddinsRoot([int]$year) {
    return Join-Path $env:APPDATA ("Autodesk\Revit\Addins\{0}" -f $year)
}

if (-not $Years -or $Years.Count -eq 0) {
    $Years = Get-InstalledRevitYears
    if ($Years.Count -eq 0) {
        Write-Warning "No Revit installations detected under HKLM:\SOFTWARE\Autodesk\Revit\. Use -Years to force explicit list."
        return
    }
    Write-Host ("Detected Revit years: {0}" -f ($Years -join ', '))
}

$handled = @()
$skipped = @()

foreach ($year in $Years) {
    $yearTwo = "{0:D2}" -f ($year - 2000)   # 2023 -> 23
    $addinFile = "Bimwright.R$yearTwo.addin"
    $addinsRoot = Get-AddinsRoot $year
    $pluginDir = Join-Path $addinsRoot 'Bimwright'
    $addinPath = Join-Path $addinsRoot $addinFile

    if ($Uninstall) {
        $didSomething = $false
        if (Test-Path $pluginDir) {
            if ($PSCmdlet.ShouldProcess($pluginDir, 'Remove plugin folder')) {
                Remove-Item $pluginDir -Recurse -Force
            }
            $didSomething = $true
        }
        if (Test-Path $addinPath) {
            if ($PSCmdlet.ShouldProcess($addinPath, 'Remove addin manifest')) {
                Remove-Item $addinPath -Force
            }
            $didSomething = $true
        }
        if ($didSomething) {
            Write-Host ("[R{0}] uninstalled from {1}" -f $yearTwo, $addinsRoot)
            $handled += "R$yearTwo"
        } else {
            Write-Host ("[R{0}] nothing to remove at {1}" -f $yearTwo, $addinsRoot)
            $skipped += "R$yearTwo"
        }
        continue
    }

    # Install path
    $zip = Join-Path $SourceDir ("Bimwright.Rvt.Plugin.R{0}.zip" -f $yearTwo)
    if (-not (Test-Path $zip)) {
        Write-Warning ("[R{0}] skipped — missing zip {1}" -f $yearTwo, $zip)
        $skipped += "R$yearTwo"
        continue
    }

    if (-not (Test-Path $addinsRoot)) {
        if ($PSCmdlet.ShouldProcess($addinsRoot, 'Create Revit addins directory')) {
            New-Item -ItemType Directory -Path $addinsRoot -Force | Out-Null
        }
    }

    if (Test-Path $pluginDir) {
        if ($PSCmdlet.ShouldProcess($pluginDir, 'Clean previous install')) {
            Remove-Item $pluginDir -Recurse -Force
        }
    }
    if ($PSCmdlet.ShouldProcess($pluginDir, 'Create plugin folder')) {
        New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
    }

    # Peek into zip (works under -WhatIf too) to verify the addin manifest is present
    # before we commit to the Expand/Move sequence.
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $zipHasAddin = $false
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $zipHasAddin = @($archive.Entries | Where-Object { $_.Name -eq $addinFile }).Count -gt 0
    } finally {
        $archive.Dispose()
    }
    if (-not $zipHasAddin) {
        Write-Warning ("[R{0}] zip {1} does not contain {2} — skipping" -f $yearTwo, $zip, $addinFile)
        $skipped += "R$yearTwo"
        continue
    }

    if ($PSCmdlet.ShouldProcess($zip, "Extract to $pluginDir")) {
        Expand-Archive -Path $zip -DestinationPath $pluginDir -Force
    }

    # .addin manifest must sit at addins root, not inside Bimwright\
    $extractedAddin = Join-Path $pluginDir $addinFile
    if ($PSCmdlet.ShouldProcess($addinPath, 'Move addin manifest to addins root')) {
        Move-Item -Path $extractedAddin -Destination $addinPath -Force
    }

    Write-Host ("[R{0}] installed -> {1}" -f $yearTwo, $pluginDir)
    $handled += "R$yearTwo"
}

Write-Host ""
Write-Host "=== install.ps1 summary ==="
Write-Host ("Mode   : {0}" -f ($(if ($Uninstall) { 'Uninstall' } else { 'Install' })))
Write-Host ("Years  : {0}" -f ($Years -join ', '))
Write-Host ("Handled: {0}" -f ($handled -join ', '))
if ($skipped.Count -gt 0) {
    Write-Host ("Skipped: {0}" -f ($skipped -join ', '))
}
