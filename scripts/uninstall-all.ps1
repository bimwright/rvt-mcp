<#
.SYNOPSIS
  Remove RvtMcp from this machine (Revit add-ins + server + discovery files + spill cache).
  Personal data under %LOCALAPPDATA%\RvtMcp is kept unless -Purge.
  MCP client configs are never read or changed.

.DESCRIPTION
  Runs this sweep:
    1. RvtMcp add-ins for every Revit year 2022-2027, including other manifests
       that carry RvtMcp's AddInId such as Bimwright-era copies (delegates to
       install.ps1 -Uninstall).
    2. Legacy .NET global tool RvtMcp.Server, if present.
    3. MCP client configs are not touched: remove the 'rvt-mcp' entry from each
       client you configured yourself (see AGENTS.md).
    4. Server copies, discovery files (revit-YYYY.json) and the spill cache in
       %LOCALAPPDATA%\RvtMcp\. A server copy that an MCP client is still
       running is kept whole and reported; close that client and run again.
       Everything else there - settings, locales\, ToolBaker data (bake.db +
       sidecars, baked\), firm-profiles\, shared-parameters.txt, the
       .migrated-from-bimwright marker, logs, journals, captures and anything
       unrecognized - is kept. -Purge deletes the whole folder; -Purge -KeepLogs
       keeps logs.

  Each step is independently skippable if the target does not exist. Failure mid-step
  does not abort the chain; exit code 1 is returned at the end if any step failed.

.PARAMETER WhatIf
  Print the full plan, write nothing.

.PARAMETER Yes
  Skip the interactive confirmation prompt.

.PARAMETER KeepLogs
  Only matters with -Purge (without it, logs are kept anyway). Preserves:
  - any `logs\` subdirectory (recursively)
  - any loose `*.log` / `*.jsonl` files at the root of %LOCALAPPDATA%\RvtMcp\

.PARAMETER Purge
  Delete the whole %LOCALAPPDATA%\RvtMcp\ tree during step 4, including
  settings, locales\, ToolBaker data (bake.db + sidecars, baked\),
  firm-profiles\, shared-parameters.txt, the .migrated-from-bimwright marker,
  journals, captures and logs. Combine with -KeepLogs to still keep logs.

.EXAMPLE
  pwsh scripts/uninstall-all.ps1 -WhatIf
  pwsh scripts/uninstall-all.ps1 -Yes
  pwsh scripts/uninstall-all.ps1 -Purge
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Yes,
    [switch]$KeepLogs,
    [switch]$Purge
)

$ErrorActionPreference = 'Stop'

$script:handled = @()
$script:skipped = @()
$script:failed  = @()

# Duplicated from install.ps1 on purpose: both scripts are standalone entry points.
# A running server's exe cannot be deleted. Probe it first so a copy that an
# MCP client still runs is kept whole instead of half-deleted.
function Remove-ServerCopy([string]$Dir) {
    foreach ($name in 'rvt-mcp.exe', 'RvtMcp.Server.exe') {
        $exe = Join-Path $Dir $name
        if (Test-Path -LiteralPath $exe) {
            try { Remove-Item -LiteralPath $exe -Force -ErrorAction Stop } catch { return $false }
        }
    }
    try { Remove-Item -LiteralPath $Dir -Recurse -Force -ErrorAction Stop; return $true }
    catch { Write-Warning ("Could not fully remove {0}: {1}" -f $Dir, $_.Exception.Message); return $false }
}

function Confirm-Sweep {
    param([string[]]$PlannedTargets)
    Write-Host ""
    Write-Host "=== uninstall-all.ps1 - planned targets ==="
    foreach ($t in $PlannedTargets) { Write-Host "  - $t" }
    Write-Host ""
    if ($Yes) { return $true }
    $ans = Read-Host "Proceed? (y/N)"
    return ($ans -match '^(y|yes)$')
}

function Invoke-Step1-Plugin {
    $installScript = Join-Path $PSScriptRoot 'install.ps1'
    if (-not (Test-Path $installScript)) {
        Write-Warning "[step1] install.ps1 not found at $installScript - cannot remove plugin"
        $script:failed += 'step1-plugin'
        return
    }
    try {
        if ($PSCmdlet.ShouldProcess($installScript, 'Delegate plugin uninstall')) {
            & $installScript -Uninstall
        } else {
            Write-Host "[step1] (WhatIf) would call: $installScript -Uninstall"
        }
        $script:handled += 'step1-plugin'
    } catch {
        Write-Warning ("[step1] plugin uninstall failed: {0}" -f $_.Exception.Message)
        $script:failed += 'step1-plugin'
    }
}

function Invoke-Step2-DotnetTool {
    $toolName = 'RvtMcp.Server'
    try {
        $list = & dotnet tool list -g 2>&1 | Out-String
    } catch {
        Write-Warning "[step2] 'dotnet' not on PATH - cannot check global tools"
        $script:skipped += 'step2-dotnet-tool'
        return
    }

    if ($list -notmatch [regex]::Escape($toolName.ToLower())) {
        Write-Host "[step2] $toolName not installed - nothing to remove"
        $script:skipped += 'step2-dotnet-tool'
        return
    }

    try {
        if ($PSCmdlet.ShouldProcess($toolName, 'dotnet tool uninstall -g')) {
            & dotnet tool uninstall -g $toolName
        } else {
            Write-Host "[step2] (WhatIf) would run: dotnet tool uninstall -g $toolName"
        }
        $script:handled += 'step2-dotnet-tool'
    } catch {
        Write-Warning ("[step2] uninstall failed: {0}" -f $_.Exception.Message)
        $script:failed += 'step2-dotnet-tool'
    }
}

function Invoke-Step4-Discovery {
    param([string]$Root)
    if (-not (Test-Path -LiteralPath $Root)) {
        Write-Host "[step4] $Root not present - nothing to remove"
        $script:skipped += 'step4-discovery'
        return
    }

    $hadToolBakerData = (Test-Path -LiteralPath (Join-Path $Root 'bake.db')) -or (Test-Path -LiteralPath (Join-Path $Root 'baked')) -or
        (@(Get-ChildItem -LiteralPath $Root -Force -File -Filter 'bake.db-*' -ErrorAction SilentlyContinue).Count -gt 0)

    # Server copies first: a copy an MCP client still runs cannot be deleted
    # (its exe is locked). Keep it whole and report it instead of half-deleting.
    $inUse = @()
    $serverParent = Join-Path $Root 'rvt\server'
    if (Test-Path -LiteralPath $serverParent) {
        foreach ($dir in Get-ChildItem -LiteralPath $serverParent -Directory -Force) {
            if ($PSCmdlet.ShouldProcess($dir.FullName, 'Remove server copy')) {
                if (-not (Remove-ServerCopy $dir.FullName)) { $inUse += $dir.FullName }
            }
        }
    }
    if ($inUse.Count) {
        Write-Warning ("[step4] server copies still used by an MCP client were kept: {0}. Close MCP clients that use rvt-mcp, then run again." -f ($inUse -join ', '))
        $script:failed += 'step4-discovery'
    }

    if ($Purge -and -not $KeepLogs -and $inUse.Count -eq 0) {
        if ($PSCmdlet.ShouldProcess($Root, 'Remove-Item -Recurse')) {
            try {
                Remove-Item -LiteralPath $Root -Recurse -Force
            } catch {
                Write-Warning ("[step4] failed to remove {0}: {1}" -f $Root, $_.Exception.Message)
                $script:failed += 'step4-discovery'
                return
            }
        }
        $verb = if ($WhatIfPreference) { 'preview remove' } else { 'removed' }
        Write-Host ("[step4] {0} {1}" -f $verb, $Root)
        $script:handled += 'step4-discovery'
        if ($hadToolBakerData) { $script:handled += 'step5-toolbaker (contained)' }
        return
    }

    # Default-deny: only known-safe entries are removed. Everything else -
    # settings, translations, ToolBaker data, logs, captures, and anything we do
    # not recognize - stays. LiteralPath everywhere: names may contain [ ].
    $preserved = @()
    foreach ($e in Get-ChildItem -LiteralPath $Root -Force) {
        if ($e.Name -eq 'rvt' -and $inUse.Count) {
            $preserved += $e.Name
            continue
        }
        $remove = $false
        if ($Purge) {
            if (-not $KeepLogs) {
                $remove = $true
            } elseif ($e.PSIsContainer) {
                $remove = ($e.Name -ne 'logs')
            } else {
                $remove = ($e.Extension -notin @('.log', '.jsonl'))
            }
        } else {
            $remove = ($e.Name -in @('rvt', 'spill')) -or
                (-not $e.PSIsContainer -and $e.Name -match '^revit-\d{4}\.json$')
        }
        if (-not $remove) {
            $preserved += $e.Name
            continue
        }
        if ($PSCmdlet.ShouldProcess($e.FullName, 'Remove-Item -Recurse')) {
            try {
                Remove-Item -LiteralPath $e.FullName -Recurse -Force
            } catch {
                Write-Warning ("[step4] failed to remove {0}: {1}" -f $e.FullName, $_.Exception.Message)
                $script:failed += 'step4-discovery'
                return
            }
        }
    }
    if (-not $WhatIfPreference -and (Test-Path -LiteralPath $Root) -and @(Get-ChildItem -LiteralPath $Root -Force).Count -eq 0) {
        try {
            Remove-Item -LiteralPath $Root -Force
        } catch {
            Write-Warning ("[step4] failed to remove empty {0}: {1}" -f $Root, $_.Exception.Message)
            $script:failed += 'step4-discovery'
            return
        }
    }
    $keptMsg = if ($preserved.Count -gt 0) { ($preserved -join ', ') } else { '(nothing to keep)' }
    $verb = if ($WhatIfPreference) { 'preview clean' } else { 'cleaned' }
    Write-Host ("[step4] {0} {1} (kept: {2})" -f $verb, $Root, $keptMsg)
    if (-not $Purge -and $preserved.Count -gt 0) {
        Write-Host "[step4] kept personal data and logs - run with -Purge to delete everything"
    }
    if ($inUse.Count -eq 0) { $script:handled += 'step4-discovery' }
    if ($Purge -and $hadToolBakerData) { $script:handled += 'step5-toolbaker (contained)' }
}

# --- Main ---
$step4Plan = 'Step4: %LOCALAPPDATA%\RvtMcp\ server copies (kept while an MCP client still runs them), discovery files and spill cache (settings, translations, ToolBaker data, logs and captures are kept)'
if ($Purge) {
    $step4Plan = 'Step4 (-Purge): PERMANENTLY delete %LOCALAPPDATA%\RvtMcp\ including settings, translations, ToolBaker data, firm profiles, shared parameters, captures and logs'
    if ($KeepLogs) { $step4Plan += ' (logs kept)' }
}
$planned = @(
    'Step1: RvtMcp add-ins for Revit 2022-2027, including legacy manifests with the same AddInId (via install.ps1 -Uninstall)'
    'Step2: global tool RvtMcp.Server, if present'
    'Step3: MCP client configs - not touched; remove the rvt-mcp entry from your clients yourself (see AGENTS.md)'
    $step4Plan
)

if (-not (Confirm-Sweep $planned)) {
    Write-Host "Aborted by user."
    return
}

try {
    Invoke-Step1-Plugin
    Invoke-Step2-DotnetTool
    Invoke-Step4-Discovery -Root (Join-Path $env:LOCALAPPDATA 'RvtMcp')
} catch {
    Write-Warning ("[main] unexpected error - summary follows. Error: {0}" -f $_.Exception.Message)
    $script:failed += 'main-unexpected-error'
} finally {
    Write-Host ""
    Write-Host "=== uninstall-all.ps1 summary ==="
    $mode = if ($WhatIfPreference) { 'WhatIf (no changes)' } else { 'Execute' }
    Write-Host ("Mode   : {0}" -f $mode)
    Write-Host ("Handled: {0}" -f (($script:handled) -join ', '))
    if ($script:skipped.Count -gt 0) { Write-Host ("Skipped: {0}" -f (($script:skipped) -join ', ')) }
    if ($script:failed.Count  -gt 0) { Write-Host ("Failed : {0}" -f (($script:failed)  -join ', ')) }
    Write-Host "Clients: MCP client entries were not changed. Remove the 'rvt-mcp' entry from each MCP client you configured (see AGENTS.md)."
}

if ($script:failed.Count -gt 0) { exit 1 } else { exit 0 }
