#Requires -Version 5.1
<#
.SYNOPSIS
  Install, update or uninstall the RvtMcp Revit add-ins and MCP server.

.DESCRIPTION
  In a client setup ZIP, this script installs:
    - the matching per-Revit add-in from plugins/ for every installed Revit
      2022-2027 (a year counts when its Revit.exe exists)
    - the self-contained MCP server from server/ at the fixed path
      %LOCALAPPDATA%\RvtMcp\rvt\server\current\rvt-mcp.exe
    - a default %LOCALAPPDATA%\RvtMcp\rvtmcp.config.json with toolsets=["all"]
      (skipped when the file already sets toolsets)

  Client configs stay untouched unless -Client asks for wiring: it then
  applies the procedures in docs/mcp-client-wiring.md - minimal text edits
  (JSONC comments survive), a <config>.bak backup first, client-native
  verification where available, custom launchers and legacy bimwright-rvt*
  entries reported but never replaced. Updating keeps the server path, so
  wired clients only need a restart.

  Every replacement is recorded and rolled back on error. Other add-in
  manifests carrying RvtMcp's AddInId (Bimwright-era copies) are removed; a
  machine-wide copy blocks the install. The installed add-ins are verified
  against the package and the server is started once with --help.

  It also remains compatible with the older plugin-only release layout where
  per-Revit plugin ZIPs sit beside this script.

.PARAMETER SourceDir
  Setup root or plugin ZIP source directory. Defaults to the current setup root
  when server/ or plugins/ exists beside this script; otherwise defaults to
  build/plugin-zip/ relative to the repo root.

.PARAMETER Uninstall
  Remove the RvtMcp add-ins for every Revit year 2022-2027 (or -Years),
  including other manifests carrying RvtMcp's AddInId. The server and user data
  stay; use uninstall-all.ps1 for a full removal.

.PARAMETER Client
  Wire MCP clients after installing: a comma list of client names
  (claude, codex, cursor, vscode, ...), 'auto'/'all' for every detected
  client, or 'none' (default) to leave client configs untouched. Every
  edit is previewed under -WhatIf and backed up to <config>.bak first.
  With -Uninstall, removes the rvt-mcp entry instead.

.PARAMETER WireClient
  Deprecated alias of -Client; merged into the client list if passed.

.PARAMETER PruneOldServers
  Remove legacy version-named server copies (for example 0.6.2\) beside
  current\ after a successful install. Repoint clients that still use them
  first.

.EXAMPLE
  pwsh .\install.ps1 -WhatIf
  pwsh .\install.ps1
  pwsh .\install.ps1 -Client auto           # wire every detected MCP client
  pwsh .\install.ps1 -Client cursor,claude  # wire specific clients
  pwsh .\install.ps1 -Years 2024
  pwsh .\install.ps1 -Uninstall -Client cursor
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SourceDir,
    [switch]$Uninstall,
    [ValidateRange(2022, 2027)][int[]]$Years,
    [ValidateSet('none', 'auto', 'all',
        'claude', 'claude-desktop', 'codex', 'grok',
        'cursor', 'cline', 'gemini', 'antigravity', 'devin', 'lmstudio',
        'kiro', 'qwen', 'windsurf', 'opencode', 'kilo', 'vscode', 'kun',
        'zed', 'cherry-studio')]
    [string[]]$Client = @('none'),
    [ValidateSet('opencode', 'codex', 'kilo')]
    [string]$WireClient,
    [string]$ServerInstallRoot,
    [switch]$PruneOldServers
)

$ErrorActionPreference = 'Stop'

if (-not $SourceDir) {
    $hasSetupLayout = (Test-Path (Join-Path $PSScriptRoot 'server')) -or (Test-Path (Join-Path $PSScriptRoot 'plugins'))
    if ($hasSetupLayout) {
        $SourceDir = $PSScriptRoot
    } else {
        $repoRoot = Split-Path -Parent $PSScriptRoot
        $SourceDir = Join-Path $repoRoot 'build\plugin-zip'
    }
}

if (Test-Path $SourceDir) {
    $SourceDir = (Resolve-Path $SourceDir).Path
}

$pluginSourceDir = if (Test-Path (Join-Path $SourceDir 'plugins')) {
    Join-Path $SourceDir 'plugins'
} else {
    $SourceDir
}

$serverSourceDir = if (Test-Path (Join-Path $SourceDir 'server')) {
    Join-Path $SourceDir 'server'
} else {
    $null
}

$manifestPath = Join-Path $SourceDir 'manifest.json'
$manifest = $null
$setupVersion = 'dev'
if (Test-Path $manifestPath) {
    try {
        $manifest = Get-Content -Raw -Path $manifestPath | ConvertFrom-Json
        if ($manifest.version) { $setupVersion = [string]$manifest.version }
    } catch {
        throw ("[setup] could not parse manifest.json: {0}" -f $_.Exception.Message)
    }
}
if ($setupVersion -notmatch '^v?\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$' -and $setupVersion -ne 'dev') {
    throw '[setup] Invalid version in manifest.json.'
}

# Fixed path for every version: MCP clients are configured once and an update
# only replaces the files behind it.
if (-not $ServerInstallRoot) {
    $ServerInstallRoot = Join-Path $env:LOCALAPPDATA 'RvtMcp\rvt\server\current'
}

# A year counts only when Revit.exe exists: registry keys survive uninstalls.
function Get-InstalledRevitYears([string]$RegistryRoot = 'HKLM:\SOFTWARE\Autodesk\Revit', [string]$ProgramFilesRoot = $env:ProgramFiles) {
    $detected = @()
    foreach ($year in 2022..2027) {
        $candidates = @()
        $yearKey = Join-Path $RegistryRoot "$year"
        if (Test-Path -LiteralPath $yearKey) {
            foreach ($sub in Get-ChildItem -LiteralPath $yearKey -ErrorAction SilentlyContinue) {
                $location = (Get-ItemProperty -LiteralPath $sub.PSPath -ErrorAction SilentlyContinue).InstallationLocation
                if ($location) { $candidates += (Join-Path $location 'Revit.exe') }
            }
        }
        if ($ProgramFilesRoot) { $candidates += (Join-Path $ProgramFilesRoot "Autodesk\Revit $year\Revit.exe") }
        if (@($candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }).Count) { $detected += $year }
    }
    return $detected
}

function Get-AddinsRoot([int]$year) {
    return Join-Path $env:APPDATA ("Autodesk\Revit\Addins\{0}" -f $year)
}

function Get-MachineAddinsRoot([int]$year) {
    return Join-Path $env:ProgramData ("Autodesk\Revit\Addins\{0}" -f $year)
}

# Unreadable or malformed manifests belong to someone else: return $null, never throw.
function Read-AddinManifest([string]$Path) {
    try { [xml]$xml = [IO.File]::ReadAllText($Path) } catch { return $null }
    $ids = @(); $assemblies = @()
    try {
        foreach ($node in @($xml.SelectNodes('//AddIn'))) {
            foreach ($name in 'AddInId', 'ClientId') {
                $value = $node.SelectSingleNode($name)
                if ($value -and $value.InnerText) { $ids += $value.InnerText.Trim().Trim('{', '}').ToLowerInvariant() }
            }
            $assembly = $node.SelectSingleNode('Assembly')
            if ($assembly -and $assembly.InnerText) {
                $asm = $assembly.InnerText.Trim()
                if (-not [IO.Path]::IsPathRooted($asm)) { $asm = Join-Path (Split-Path -Parent $Path) $asm }
                $assemblies += [IO.Path]::GetFullPath($asm)
            }
        }
    } catch { return $null }
    return [pscustomobject]@{ Ids = $ids; Assemblies = $assemblies }
}

function Find-RvtMcpManifests([string]$Folder, [int]$Year) {
    if (-not $Folder -or -not (Test-Path -LiteralPath $Folder -PathType Container)) { return @() }
    $id = Get-RvtMcpAddinId $Year
    $found = @()
    foreach ($file in Get-ChildItem -LiteralPath $Folder -Filter '*.addin' -File -ErrorAction SilentlyContinue) {
        $manifest = Read-AddinManifest $file.FullName
        if ($manifest -and ($manifest.Ids -contains $id)) { $found += $file.FullName }
    }
    return $found
}

# Other manifests in the year folder that carry RvtMcp's AddInId (Bimwright-era
# copies, stray duplicates), plus assembly folders directly under the year
# folder that only those manifests use. Revit loads one manifest per AddInId,
# so a leftover could silently shadow the add-in being installed.
function Get-DuplicateAddinItems([int]$Year, [string]$AddinsRoot, [string]$KeepManifest) {
    if (-not (Test-Path -LiteralPath $AddinsRoot -PathType Container)) { return @() }
    $keep = [IO.Path]::GetFullPath($KeepManifest)
    $dupes = @(Find-RvtMcpManifests -Folder $AddinsRoot -Year $Year | Where-Object { $_ -ne $keep })
    if ($dupes.Count -eq 0) { return @() }
    $root = [IO.Path]::GetFullPath($AddinsRoot).TrimEnd('\')
    $stillUsed = @()
    foreach ($file in Get-ChildItem -LiteralPath $AddinsRoot -Filter '*.addin' -File) {
        if ($dupes -contains $file.FullName) { continue }
        $manifest = Read-AddinManifest $file.FullName
        if ($manifest) { $stillUsed += $manifest.Assemblies }
    }
    $items = @()
    foreach ($dupe in $dupes) {
        $items += $dupe
        foreach ($asm in (Read-AddinManifest $dupe).Assemblies) {
            $dir = Split-Path -Parent $asm
            if ((Split-Path -Parent $dir) -ne $root -or (Split-Path -Leaf $dir) -eq 'RvtMcp') { continue }
            if (@($stillUsed | Where-Object { $_.StartsWith($dir + '\', [StringComparison]::OrdinalIgnoreCase) }).Count) { continue }
            if ((Test-Path -LiteralPath $dir -PathType Container) -and $items -notcontains $dir) { $items += $dir }
        }
    }
    return $items
}

function Find-ServerSourceExe {
    param([string]$ServerDir)
    if (-not $ServerDir) { return $null }
    $preferred = Join-Path $ServerDir 'rvt-mcp.exe'
    if (Test-Path $preferred) { return $preferred }
    $fallback = Join-Path $ServerDir 'RvtMcp.Server.exe'
    if (Test-Path $fallback) { return $fallback }
    return $null
}

function Get-RvtMcpAddinId([int]$Year) {
    # Product identity, unchanged since v0.1.0: Bimwright-era manifests carry the same IDs.
    $ids = @{
        2022 = 'ee3a01ad-2e01-4a8d-825e-3b24d56075d5'
        2023 = '43bec7a5-3c7d-4547-9b4a-80b7914d6258'
        2024 = '460442af-5b1e-4bf8-9f50-f27047ace447'
        2025 = '5de5a098-6d35-41cd-9280-91380d4f5d46'
        2026 = '5e077288-82fd-4b2f-9f4e-a1849c38bb00'
        2027 = 'd50b0e16-5a4e-47e6-87ea-fa2890c758e3'
    }
    if (-not $ids.ContainsKey($Year)) { throw "No RvtMcp AddInId for Revit $Year." }
    return $ids[$Year]
}

function Get-AgentsGuidePath([string]$ScriptRoot) {
    if ($ScriptRoot) {
        foreach ($candidate in @((Join-Path $ScriptRoot 'AGENTS.md'), (Join-Path (Split-Path -Parent $ScriptRoot) 'AGENTS.md'))) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
    return 'https://github.com/bimwright/rvt-mcp/blob/master/AGENTS.md'
}

# Every backup is beside its target. Never delete a caller-supplied directory
# unless it is a recorded target of this transaction or our unique staging area.
function Remove-InstallPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $allowed = @($script:installStage)
    foreach ($change in $script:installChanges) { $allowed += $change.Path; $allowed += $change.Backup }
    if ($full -notin $allowed -or $full.TrimEnd('\') -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) {
        throw "Refusing cleanup outside this install transaction: $full"
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}

function Set-InstallPath([string]$Source, [string]$Destination) {
    $full = [IO.Path]::GetFullPath($Destination)
    if ($full.TrimEnd('\') -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) { throw 'Cannot install into a drive root.' }
    $parent = Split-Path -Parent $full
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $backup = $null
    if (Test-Path -LiteralPath $full) {
        $backup = $full + '.rvtmcp-rollback-' + [guid]::NewGuid().ToString('N')
        Move-Item -LiteralPath $full -Destination $backup
    }
    $script:installChanges.Add([pscustomobject]@{Path=$full;Backup=$backup})
    Move-Item -LiteralPath $Source -Destination $full
}

function Move-ToRollback([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $backup = $full + '.rvtmcp-rollback-' + [guid]::NewGuid().ToString('N')
    Move-Item -LiteralPath $full -Destination $backup
    $script:installChanges.Add([pscustomobject]@{Path=$full;Backup=$backup})
}

function Undo-InstallChanges {
    $failures = @()
    for ($i = $script:installChanges.Count - 1; $i -ge 0; $i--) {
        $change = $script:installChanges[$i]
        try {
            Remove-InstallPath $change.Path
            if ($change.Backup) { Move-Item -LiteralPath $change.Backup -Destination $change.Path }
        } catch { $failures += "$($change.Path): $_ (backup: $($change.Backup))" }
    }
    if ($failures.Count) { throw ("Rollback incomplete; retain backup files and restore manually:`n" + ($failures -join "`n")) }
}

function Assert-RevitClosed {
    if (@(Get-Process -Name Revit -ErrorAction SilentlyContinue).Count) {
        throw 'Revit running. Close every Revit window before installing or uninstalling plugins; no files have been replaced.'
    }
}

function Assert-PluginArchive([string]$Zip, [string]$AddinFile, [int]$Year) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -match '(^[\\/]|:|(^|[\\/])\.\.([\\/]|$))') { throw "Unsafe ZIP entry: $($entry.FullName)" }
        }
        if (@($archive.Entries | Where-Object { $_.FullName -eq $AddinFile }).Count -ne 1 -or
            @($archive.Entries | Where-Object { $_.FullName -eq 'RvtMcp.Plugin.dll' }).Count -ne 1) {
            throw "Invalid plugin ZIP $Zip : expected root $AddinFile and RvtMcp.Plugin.dll."
        }
        # The manifest must carry this year's RvtMcp AddInId and point at the
        # plugin folder the installer creates.
        $entry = @($archive.Entries | Where-Object { $_.FullName -eq $AddinFile })[0]
        $reader = New-Object IO.StreamReader($entry.Open())
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $valid = $false
        try {
            [xml]$xml = $text
            $nodes = @($xml.SelectNodes('//AddIn'))
            $valid = $nodes.Count -eq 1 -and
                $nodes[0].SelectSingleNode('AddInId').InnerText.Trim().Trim('{', '}').ToLowerInvariant() -eq (Get-RvtMcpAddinId $Year) -and
                $nodes[0].SelectSingleNode('Assembly').InnerText.Trim() -eq 'RvtMcp\RvtMcp.Plugin.dll'
        } catch { $valid = $false }
        if (-not $valid) { throw "Plugin ZIP for Revit $Year has an unexpected add-in manifest: $Zip" }
    } finally { $archive.Dispose() }
}

function Assert-SetupManifest([string]$Root, $Manifest) {
    if (-not $Manifest) { return } # Legacy plugin-only ZIP layout has no manifest.
    if (-not $Manifest.files) { throw 'Setup manifest has no file checksums.' }
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    foreach ($file in $Manifest.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $Root $file.path))
        if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid manifest path: $($file.path)" }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Setup file missing: $($file.path)" }
        # Get-FileHash's provider reads honor inherited WhatIf in Windows
        # PowerShell 5.1. Hash directly so previews still validate actual bytes.
        $stream = [IO.File]::OpenRead($path)
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $sha.Dispose(); $stream.Dispose() }
        if ($hash -ne $file.sha256) {
            throw "Setup checksum failed: $($file.path). Download and extract the setup ZIP again."
        }
    }
}

function Get-StreamSha256($Stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Stream)).Replace('-', '') } finally { $sha.Dispose() }
}

# The installed add-in must be exactly what the package holds, and it must be
# the only manifest carrying RvtMcp's AddInId for that Revit year.
function Assert-InstalledPlugin([int]$Year, [string]$Zip, [string]$AddinPath, [string]$PluginDir) {
    $prefix = "Add-in verification failed for Revit ${Year}:"
    $addinFull = [IO.Path]::GetFullPath($AddinPath)
    $manifests = @(Find-RvtMcpManifests -Folder (Split-Path -Parent $addinFull) -Year $Year) + @(Find-RvtMcpManifests -Folder (Get-MachineAddinsRoot $Year) -Year $Year)
    if ($manifests.Count -ne 1 -or $manifests[0] -ne $addinFull) {
        throw "$prefix expected exactly one manifest with RvtMcp's AddInId at $addinFull, found: $($manifests -join ', ')"
    }
    foreach ($asm in (Read-AddinManifest $addinFull).Assemblies) {
        if (-not (Test-Path -LiteralPath $asm -PathType Leaf)) { throw "$prefix assembly not found: $asm" }
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        foreach ($entry in $archive.Entries) {
            if (-not $entry.Name) { continue } # directory entry
            $target = if ($entry.FullName -eq (Split-Path -Leaf $addinFull)) { $addinFull } else { Join-Path $PluginDir ($entry.FullName -replace '/', '\') }
            if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "$prefix missing file $target" }
            $expectedStream = $entry.Open()
            try { $expected = Get-StreamSha256 $expectedStream } finally { $expectedStream.Dispose() }
            $actualStream = [IO.File]::OpenRead($target)
            try { $actual = Get-StreamSha256 $actualStream } finally { $actualStream.Dispose() }
            if ($expected -ne $actual) { throw "$prefix $target differs from the package" }
        }
    } finally { $archive.Dispose() }
}

function Install-RvtMcpServer {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [string]$ServerDir,
        [string]$InstallRoot
    )
    $sourceExe = Find-ServerSourceExe -ServerDir $ServerDir
    if (-not $sourceExe) { return $null }

    $plannedExe = Join-Path $InstallRoot (Split-Path -Leaf $sourceExe)
    if ($PSCmdlet.ShouldProcess($InstallRoot, 'Install self-contained RvtMcp RVT server')) {
        Set-InstallPath -Source $ServerDir -Destination $InstallRoot
        Write-Host ("[server] installed -> {0}" -f $plannedExe)
    } else {
        Write-Host ("[server] preview install -> {0}" -f $plannedExe)
    }
    return $plannedExe
}

# --help returns before any side effect in src/server/Program.cs, so this only
# proves the installed executable can start (antivirus, policy, corruption).
function Test-ServerExecutable([string]$Path, [int]$TimeoutSeconds = 30, [string]$Arguments = '--help') {
    $hint = 'Antivirus or policy may have blocked it. Previous installation restored.'
    $psi = New-Object Diagnostics.ProcessStartInfo $Path
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    try { $process = [Diagnostics.Process]::Start($psi) }
    catch { throw ("Server executable could not start ({0}). {1}" -f $_.Exception.Message, $hint) }
    try {
        $null = $process.StandardOutput.ReadToEndAsync()
        $null = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            throw ("Server executable could not start (no exit after {0} s). {1}" -f $TimeoutSeconds, $hint)
        }
        if ($process.ExitCode -ne 0) { throw ("Server executable could not start (exit code {0}). {1}" -f $process.ExitCode, $hint) }
    } finally { $process.Dispose() }
}

function Get-OtherServerVersions([string]$InstallRoot) {
    $parent = Split-Path -Parent $InstallRoot
    if (-not $parent -or -not (Test-Path -LiteralPath $parent)) { return @() }
    $current = Split-Path -Leaf $InstallRoot
    return @(Get-ChildItem -LiteralPath $parent -Directory | Where-Object {
        $_.Name -ne $current -and $_.Name -match '^v?\d+\.\d+\.\d+([-+][A-Za-z0-9.-]+)?$'
    })
}

function Test-ServerCopy([string]$Dir) {
    return (Test-Path -LiteralPath (Join-Path $Dir 'rvt-mcp.exe')) -or (Test-Path -LiteralPath (Join-Path $Dir 'RvtMcp.Server.exe'))
}

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

# Only names this installer creates: <name>.rvtmcp-rollback-<32 hex>.
function Get-LeftoverServerCopies([string]$ServerParent) {
    if (-not (Test-Path -LiteralPath $ServerParent -PathType Container)) { return @() }
    return @(Get-ChildItem -LiteralPath $ServerParent -Directory | Where-Object { $_.Name -match '^.+\.rvtmcp-rollback-[0-9a-f]{32}$' })
}

# Legacy version directories (from installers before server\current) are kept
# by default because clients may still point at them; -PruneOldServers opts
# into removal. Only version-shaped directories are touched (current\, dev\ and
# arbitrary names are preserved). Each move is recorded in the transaction:
# rollback restores them, and the post-install sweep deletes the backups with
# the exe-first rule. A locked directory is skipped, not fatal.
function Remove-StaleServerVersions {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param([string]$InstallRoot)
    foreach ($dir in Get-OtherServerVersions -InstallRoot $InstallRoot) {
        if ($PSCmdlet.ShouldProcess($dir.FullName, 'Remove stale server version')) {
            $backup = "$($dir.FullName).rvtmcp-rollback-$([guid]::NewGuid().ToString('N'))"
            try {
                Move-Item -LiteralPath $dir.FullName -Destination $backup
                $script:installChanges.Add([pscustomobject]@{Path=$dir.FullName;Backup=$backup})
                Write-Host ("[server] removed stale version -> {0}" -f $dir.FullName)
            } catch {
                Write-Warning ("[server] could not remove stale version {0}: {1}" -f $dir.FullName, $_.Exception.Message)
            }
        } else {
            Write-Host ("[server] preview remove stale version -> {0}" -f $dir.FullName)
        }
    }
}

# ==== MCP client wiring (docs/mcp-client-wiring.md, scripted) ===========
# -Client wires the installed server into MCP clients after a successful
# install. File configs get a minimal text edit - never a JSON round-trip,
# so JSONC comments and key order survive - with a <path>.bak backup and a
# parse-check that restores the backup on failure. CLI clients go through
# their own `mcp add`. Nothing is touched unless -Client names it.
# The config entry key is always the literal 'rvt-mcp'.

# Per-client spec. Kind: cli (own `mcp` command), file (edit config),
# deeplink (no safe file path - hand the user a cherrystudio:// URL).
# RootKey/EntryKind describe the entry shape; DetectPaths/Paths use
# %USERPROFILE%/%APPDATA%/%LOCALAPPDATA% so tests can sandbox them.
function Get-McpClientSpecs {
    $up = $env:USERPROFILE; $la = $env:LOCALAPPDATA; $ra = $env:APPDATA
    @(
        [pscustomobject]@{ Name='claude';    Kind='cli'; Cli='claude'; DetectPaths=@((Join-Path $up '.claude.json')); Paths=@((Join-Path $up '.claude.json')); RootKey='mcpServers'; EntryKind='standard' }
        [pscustomobject]@{ Name='codex';     Kind='cli'; Cli='codex';  DetectPaths=@((Join-Path $up '.codex')) }
        [pscustomobject]@{ Name='grok';      Kind='cli'; Cli='grok';   DetectPaths=@((Join-Path $up '.grok')) }
        # claude-desktop: MSIX keeps its virtualized Roaming under the package
        # LocalCache - the package family name varies by install channel, so
        # glob any name. Classic/native installers use %APPDATA%\Claude; the
        # AnthropicClaude installer still reads that same Roaming path.
        [pscustomobject]@{ Name='claude-desktop'; Kind='file'; EntryKind='standard'; RootKey='mcpServers'; ProcName='claude'; ProcPathLike='*WindowsApps*'; Paths=@((Join-Path $la 'Packages\*\LocalCache\Roaming\Claude\claude_desktop_config.json'), (Join-Path $ra 'Claude\claude_desktop_config.json')); DetectPaths=@((Join-Path $la 'Packages\*\LocalCache\Roaming\Claude'), (Join-Path $ra 'Claude'), (Join-Path $la 'AnthropicClaude')) }
        [pscustomobject]@{ Name='cursor';    Kind='file'; EntryKind='standard'; RootKey='mcpServers'; Paths=@((Join-Path $up '.cursor\mcp.json'));            DetectPaths=@((Join-Path $up '.cursor')) }
        # Cline and Roo-style VS Code extensions keep their MCP settings under
        # globalStorage\<publisher>.<ext>\settings - first existing wins.
        [pscustomobject]@{ Name='cline';     Kind='file'; EntryKind='standard'; RootKey='mcpServers'; Paths=@((Join-Path $ra 'Code\User\globalStorage\saoudrizwan.claude-dev\settings\cline_mcp_settings.json'), (Join-Path $ra 'Code\User\globalStorage\rooveterinaryinc.roo-cline\settings\mcp_settings.json'), (Join-Path $ra 'Code\User\globalStorage\rooveterinaryinc.roo-cline\settings\cline_mcp_settings.json')); DetectPaths=@((Join-Path $ra 'Code\User\globalStorage\saoudrizwan.claude-dev'), (Join-Path $ra 'Code\User\globalStorage\rooveterinaryinc.roo-cline')) }
        [pscustomobject]@{ Name='gemini';    Kind='file'; EntryKind='standard'; RootKey='mcpServers'; Paths=@((Join-Path $up '.gemini\settings.json'));           DetectPaths=@((Join-Path $up '.gemini\settings.json')) }
        [pscustomobject]@{ Name='antigravity'; Kind='file'; EntryKind='standard'; RootKey='mcpServers'; Paths=@((Join-Path $up '.gemini\antigravity\mcp_config.json'), (Join-Path $up '.gemini\config\mcp_config.json')); DetectPaths=@((Join-Path $up '.gemini\antigravity'), (Join-Path $up '.gemini\config')) }
        [pscustomobject]@{ Name='devin';     Kind='file'; EntryKind='standard'; RootKey='mcpServers'; Paths=@((Join-Path $ra 'Devin\mcp_config.json'));              DetectPaths=@((Join-Path $ra 'Devin')) }
        [pscustomobject]@{ Name='lmstudio';  Kind='file'; EntryKind='standard'; RootKey='mcpServers'; Paths=@((Join-Path $up '.lmstudio\mcp.json'));                 DetectPaths=@((Join-Path $up '.lmstudio')) }
        [pscustomobject]@{ Name='kiro';      Kind='file'; EntryKind='standard'; RootKey='mcpServers'; Paths=@((Join-Path $up '.kiro\settings\mcp.json'));            DetectPaths=@((Join-Path $up '.kiro')) }
        [pscustomobject]@{ Name='qwen';      Kind='file'; EntryKind='standard'; RootKey='mcpServers'; Paths=@((Join-Path $up '.qwen\settings.json'));                DetectPaths=@((Join-Path $up '.qwen')) }
        [pscustomobject]@{ Name='windsurf';  Kind='file'; EntryKind='standard'; RootKey='mcpServers'; Paths=@((Join-Path $up '.codeium\windsurf\mcp_config.json'));  DetectPaths=@((Join-Path $up '.codeium\windsurf')) }
        [pscustomobject]@{ Name='opencode';  Kind='file'; EntryKind='opencode'; RootKey='mcp';        Paths=@((Join-Path $up '.config\opencode\opencode.json'));     DetectPaths=@((Join-Path $up '.config\opencode')) }
        [pscustomobject]@{ Name='kilo';      Kind='file'; EntryKind='opencode'; RootKey='mcp';        Paths=@((Join-Path $up '.config\kilo\kilo.jsonc'));            DetectPaths=@((Join-Path $up '.config\kilo')) }
        [pscustomobject]@{ Name='vscode';    Kind='file'; EntryKind='vscode';   RootKey='servers';    Paths=@((Join-Path $ra 'Code\User\mcp.json'));                 DetectPaths=@((Join-Path $ra 'Code\User')) }
        [pscustomobject]@{ Name='kun';       Kind='file'; EntryKind='kun';      RootKey='servers';    Paths=@((Join-Path $up '.kun\mcp.json'));                      DetectPaths=@((Join-Path $up '.kun')) }
        [pscustomobject]@{ Name='zed';       Kind='file'; EntryKind='zed';      RootKey='context_servers'; Paths=@((Join-Path $ra 'Zed\settings.json'));             DetectPaths=@((Join-Path $ra 'Zed')) }
        [pscustomobject]@{ Name='cherry-studio'; Kind='deeplink'; DetectPaths=@((Join-Path $ra 'Cherry Studio')) }
    )
}

# The rvt-mcp entry literal per client shape (path escaped for JSON).
function Get-McpEntryText([string]$EntryKind, [string]$Exe) {
    $e = $Exe.Replace('\', '\\')
    switch ($EntryKind) {
        'opencode' { return '{ "type": "local", "command": ["' + $e + '"], "enabled": true }' }
        'vscode'   { return '{ "type": "stdio", "command": "' + $e + '", "args": [] }' }
        'kun'      { return '{ "command": "' + $e + '", "args": [], "env": {}, "url": null }' }
        'zed'      { return '{ "command": { "path": "' + $e + '", "args": [] } }' }
        default    { return '{ "command": "' + $e + '", "args": [] }' }
    }
}

# Next index in $Text that is not whitespace or a // / /* */ comment.
function Skip-JsonSpace([string]$Text, [int]$i) {
    while ($i -lt $Text.Length) {
        $c = $Text[$i]
        if ($c -eq ' ' -or $c -eq "`t" -or $c -eq "`r" -or $c -eq "`n") { $i++; continue }
        if ($c -eq '/' -and $i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '/') {
            while ($i -lt $Text.Length -and $Text[$i] -ne "`n") { $i++ }
            continue
        }
        if ($c -eq '/' -and $i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '*') {
            $i += 2
            while ($i + 1 -lt $Text.Length -and -not ($Text[$i] -eq '*' -and $Text[$i + 1] -eq '/')) { $i++ }
            $i += 2
            continue
        }
        break
    }
    return $i
}

# Index of the char that ends the JSON value starting at $i (string,
# object/array via bracket balance, or scalar until , } ] ). Strings and
# comments are skipped, so braces inside them cannot unbalance the count.
function Get-JsonValueEnd([string]$Text, [int]$i) {
    $i = Skip-JsonSpace $Text $i
    if ($i -ge $Text.Length) { return $i }
    if ($Text[$i] -eq '"') {
        $i++
        while ($i -lt $Text.Length) {
            if ($Text[$i] -eq '\') { $i += 2; continue }
            if ($Text[$i] -eq '"') { return $i + 1 }
            $i++
        }
        return $i
    }
    if ($Text[$i] -eq '{' -or $Text[$i] -eq '[') {
        $open = $Text[$i]; $close = $(if ($open -eq '{') { '}' } else { ']' })
        $depth = 0
        while ($i -lt $Text.Length) {
            $c = $Text[$i]
            if ($c -eq '"') { $i = Get-JsonValueEnd $Text $i; continue }
            if ($c -eq '/' -and $i + 1 -lt $Text.Length -and ($Text[$i + 1] -eq '/' -or $Text[$i + 1] -eq '*')) {
                $i = Skip-JsonSpace $Text $i; continue
            }
            if ($c -eq $open) { $depth++ }
            elseif ($c -eq $close) { $depth--; if ($depth -eq 0) { return $i + 1 } }
            $i++
        }
        return $i
    }
    while ($i -lt $Text.Length -and $Text[$i] -notin @(',', '}', ']')) { $i++ }
    return $i
}

# Span of `"name": <value>` inside the object whose '{' is at $ObjOpen.
# Returns MemberStart..MemberEnd (value end, comma excluded) or $null.
function Get-JsonMemberSpan([string]$Text, [int]$ObjOpen, [string]$Name) {
    $i = Skip-JsonSpace $Text ($ObjOpen + 1)
    while ($i -lt $Text.Length -and $Text[$i] -ne '}') {
        if ($Text[$i] -eq '"') {
            $strStart = $i
            $i = Get-JsonValueEnd $Text $i
            $member = $Text.Substring($strStart + 1, $i - $strStart - 2)
            $afterColon = Skip-JsonSpace $Text $i
            if ($member -eq $Name -and $afterColon -lt $Text.Length -and $Text[$afterColon] -eq ':') {
                $valueStart = Skip-JsonSpace $Text ($afterColon + 1)
                return [pscustomobject]@{ MemberStart = $strStart; ValueStart = $valueStart; MemberEnd = (Get-JsonValueEnd $Text $valueStart) }
            }
            if ($afterColon -lt $Text.Length -and $Text[$afterColon] -eq ':') {
                $i = Get-JsonValueEnd $Text (Skip-JsonSpace $Text ($afterColon + 1))
            }
            $i = Skip-JsonSpace $Text $i
            continue
        }
        if ($Text[$i] -eq ',') { $i = Skip-JsonSpace $Text ($i + 1); continue }
        $i++
    }
    return $null
}

# All member names of the JSON object whose '{' is at $ObjOpen.
function Get-JsonObjectMemberNames([string]$Text, [int]$ObjOpen) {
    $names = New-Object System.Collections.Generic.List[string]
    $i = Skip-JsonSpace $Text ($ObjOpen + 1)
    while ($i -lt $Text.Length -and $Text[$i] -ne '}') {
        if ($Text[$i] -eq '"') {
            $s = $i; $i = Get-JsonValueEnd $Text $i
            $names.Add($Text.Substring($s + 1, $i - $s - 2))
            $afterColon = Skip-JsonSpace $Text $i
            if ($afterColon -lt $Text.Length -and $Text[$afterColon] -eq ':') {
                $i = Get-JsonValueEnd $Text (Skip-JsonSpace $Text ($afterColon + 1))
            }
            $i = Skip-JsonSpace $Text $i
            continue
        }
        $i++
    }
    return $names
}

# Skip-JsonSpace backwards: whitespace and /* */ blocks; // line comments are
# left for the parse-check to catch (rare between a comma and a member).
function Skip-JsonSpaceBack([string]$Text, [int]$i) {
    while ($i -ge 0) {
        $c = $Text[$i]
        if ($c -eq ' ' -or $c -eq "`t" -or $c -eq "`r" -or $c -eq "`n") { $i--; continue }
        if ($i -ge 1 -and $Text[$i] -eq '/' -and $Text[$i - 1] -eq '*') {
            $i -= 2
            while ($i -ge 0 -and -not ($Text[$i] -eq '/' -and $i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '*')) { $i-- }
            $i--
            continue
        }
        break
    }
    return $i
}

# Strips // and /* */ comments so ConvertFrom-Json can verify JSONC files.
function ConvertFrom-JsoncText([string]$Text) {
    $out = New-Object Text.StringBuilder
    $i = 0
    while ($i -lt $Text.Length) {
        $c = $Text[$i]
        if ($c -eq '"') {
            $end = Get-JsonValueEnd $Text $i
            [void]$out.Append($Text.Substring($i, $end - $i)); $i = $end; continue
        }
        if ($c -eq '/' -and $i + 1 -lt $Text.Length -and ($Text[$i + 1] -eq '/' -or $Text[$i + 1] -eq '*')) {
            $i = Skip-JsonSpace $Text $i; continue
        }
        [void]$out.Append($c); $i++
    }
    return ($out.ToString() | ConvertFrom-Json)
}

# Back up, write, and parse-verify a config edit; a failed parse restores
# the .bak so a bad edit can never leave a broken client config behind.
function Save-McpConfigText([string]$Path, [string]$NewText, [string]$OldText) {
    $bak = "$Path.bak"
    Copy-Item -LiteralPath $Path -Destination $bak -Force
    try { $null = ConvertFrom-JsoncText $NewText }
    catch {
        Copy-Item -LiteralPath $bak -Destination $Path -Force
        throw "Edit produced invalid JSON for $Path - restored backup. ($($_.Exception.Message))"
    }
    [IO.File]::WriteAllText($Path, $NewText)
}

# Minimal-edit add/remove of the rvt-mcp entry in a file config.
# Returns a status word: created|added|repointed|already|custom|removed|absent|previewed.
function Set-McpConfigEntry {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [string]$Path, [string]$RootKey, [string]$EntryKind,
        [string]$Exe, [switch]$Remove
    )
    $full = [IO.Path]::GetFullPath($Path)
    $escapedExe = if ($Exe) { $Exe.Replace('\', '\\') } else { $null }
    $entryText = if ($Exe) { Get-McpEntryText $EntryKind $Exe } else { $null }
    # Spans the whole JSON string value (quote to quote), so a repoint replaces
    # the full path rather than splicing the new one after the old prefix.
    $legacyRx = '(?i)(?<=")[^"]*?rvt(?:\\{2}|\\)+server(?:\\{2}|\\)+(?!current(?:\\{2}|\\)+)[^"\\]+(?:\\{2}|\\)+rvt-mcp\.exe(?=")'

    if (-not (Test-Path -LiteralPath $full)) {
        if ($Remove) { return 'absent' }
        $body = '{ "' + $RootKey + '": { "rvt-mcp": ' + $entryText + ' } }'
        if ($PSCmdlet.ShouldProcess($full, 'Wire rvt-mcp into client config')) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $full) -Force | Out-Null
            [IO.File]::WriteAllText($full, $body)
            return 'created'
        }
        return 'previewed'
    }

    $text = [IO.File]::ReadAllText($full)
    if ($text -notmatch '\S') {
        # Empty file (windsurf ships one) - write the full skeleton.
        if ($Remove) { return 'absent' }
        $body = '{ "' + $RootKey + '": { "rvt-mcp": ' + $entryText + ' } }'
        if (-not $PSCmdlet.ShouldProcess($full, 'Wire rvt-mcp into client config')) { return 'previewed' }
        Save-McpConfigText $full $body $text
        return 'created'
    }
    $rootOpen = Skip-JsonSpace $text 0
    if ($rootOpen -ge $text.Length -or $text[$rootOpen] -ne '{') {
        Write-Warning "[client] $full is not a JSON object; left unchanged."
        return 'skipped'
    }
    # An unparseable existing file is reported and left alone - never
    # clobbered, never edited blind.
    try { $null = ConvertFrom-JsoncText $text } catch {
        Write-Warning "[client] $full is not valid JSON; left unchanged."
        return 'skipped'
    }
    $keySpan = Get-JsonMemberSpan $text $rootOpen $RootKey
    # Legacy pre-rename entries are reported, never removed silently.
    if ($null -ne $keySpan -and $text[$keySpan.ValueStart] -eq '{') {
        $legacy = @(Get-JsonObjectMemberNames $text $keySpan.ValueStart | Where-Object { $_ -like 'bimwright-rvt*' })
        if ($legacy.Count) {
            Write-Warning "[client] $full has legacy entries ($($legacy -join ', ')) - left in place; remove them in the client if unwanted."
        }
    }

    if ($Remove) {
        if ($null -eq $keySpan -or $text[$keySpan.ValueStart] -ne '{') { return 'absent' }
        $entrySpan = Get-JsonMemberSpan $text $keySpan.ValueStart 'rvt-mcp'
        if ($null -eq $entrySpan) { return 'absent' }
        # Cut the member plus one adjacent comma (trailing preferred).
        $after = Skip-JsonSpace $text $entrySpan.MemberEnd
        $from = $entrySpan.MemberStart; $to = $entrySpan.MemberEnd
        if ($after -lt $text.Length -and $text[$after] -eq ',') {
            $to = $after + 1
        } else {
            $before = Skip-JsonSpaceBack $text ($entrySpan.MemberStart - 1)
            if ($before -ge 0 -and $text[$before] -eq ',') { $from = $before }
        }
        if (-not $PSCmdlet.ShouldProcess($full, 'Remove rvt-mcp from client config')) { return 'previewed' }
        Save-McpConfigText $full ($text.Substring(0, $from) + $text.Substring($to)) $text
        return 'removed'
    }

    if ($null -eq $keySpan -or $text[$keySpan.ValueStart] -ne '{') {
        # Insert "<RootKey>": { "rvt-mcp": <entry> } as the first root member.
        $inner = '"' + $RootKey + '": { "rvt-mcp": ' + $entryText + ' }'
        $next = Skip-JsonSpace $text ($rootOpen + 1)
        if ($next -lt $text.Length -and $text[$next] -ne '}') { $inner += ',' }
        $newText = $text.Insert($rootOpen + 1, "`n    " + $inner + "`n")
        if (-not $PSCmdlet.ShouldProcess($full, 'Wire rvt-mcp into client config')) { return 'previewed' }
        Save-McpConfigText $full $newText $text
        return 'added'
    }

    $entrySpan = Get-JsonMemberSpan $text $keySpan.ValueStart 'rvt-mcp'
    if ($null -eq $entrySpan) {
        $next = Skip-JsonSpace $text ($keySpan.ValueStart + 1)
        $inner = '"rvt-mcp": ' + $entryText
        if ($next -lt $text.Length -and $text[$next] -ne '}') { $inner += ',' }
        $newText = $text.Insert($keySpan.ValueStart + 1, "`n        " + $inner + "`n    ")
        if (-not $PSCmdlet.ShouldProcess($full, 'Wire rvt-mcp into client config')) { return 'previewed' }
        Save-McpConfigText $full $newText $text
        return 'added'
    }

    # Entry exists: identical -> already; legacy versioned path -> repoint;
    # anything else is a custom launcher and is reported, never replaced.
    $memberText = $text.Substring($entrySpan.MemberStart, $entrySpan.MemberEnd - $entrySpan.MemberStart)
    $normalized = $memberText -replace '\\\\', '\'
    if ($normalized.Contains($Exe)) { return 'already' }
    if ($memberText -match $legacyRx) {
        # MatchEvaluator returns the escaped path literally - no regex
        # metachar reinterpretation of the replacement string.
        $newMember = [regex]::Replace($memberText, $legacyRx, { param($m) $escapedExe })
        $newText = $text.Substring(0, $entrySpan.MemberStart) + $newMember + $text.Substring($entrySpan.MemberEnd)
        if (-not $PSCmdlet.ShouldProcess($full, 'Repoint rvt-mcp to the current server')) { return 'previewed' }
        Save-McpConfigText $full $newText $text
        return 'repointed'
    }
    return 'custom'
}

# First existing candidate wins. When none exists: a globbed package dir that
# already contains the app's cache (MSIX) beats a stray literal dir - an MSIX
# app never reads the real %APPDATA%; else a literal path under a detected
# install dir; else the first literal path (create-if-absent).
function Resolve-McpConfigPath($spec) {
    foreach ($p in $spec.Paths) {
        $hit = @(Resolve-Path $p -ErrorAction SilentlyContinue)
        if ($hit.Count) { return $hit[0].Path }
    }
    if ($spec.Paths[0] -match '\*') {
        # Rebuild inside any package dir that already holds the app's cache -
        # works regardless of the package family name (enterprise repackage).
        $tail = $spec.Paths[0].Substring($spec.Paths[0].IndexOf('*\') + 2)
        $innerDir = Split-Path -Parent $tail
        $baseDir = $spec.Paths[0].Substring(0, $spec.Paths[0].IndexOf('*\'))
        if (Test-Path -LiteralPath $baseDir) {
            $pkg = @(Get-ChildItem -LiteralPath $baseDir -Directory -ErrorAction SilentlyContinue |
                Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName $innerDir) } |
                Select-Object -First 1)
            if ($pkg.Count) { return Join-Path $pkg[0].FullName $tail }
        }
    }
    foreach ($p in $spec.Paths) {
        if ($p -match '\*') { continue }
        foreach ($d in @($spec.DetectPaths)) {
            if ($d -and $d -notmatch '\*' -and $p.StartsWith($d, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $d)) { return $p }
        }
    }
    return @($spec.Paths | Where-Object { $_ -notmatch '\*' } | Select-Object -First 1)
}

# codex `mcp add` replaces the whole entry - pull args forward from the
# existing [mcp_servers.rvt-mcp] table so a repoint doesn't drop them.
function Read-CodexEntryArgs([string]$TomlPath) {
    if (-not (Test-Path -LiteralPath $TomlPath)) { return @() }
    $text = [IO.File]::ReadAllText($TomlPath)
    $m = [regex]::Match($text, '(?ms)^\[mcp_servers\.rvt-mcp\]\s*$(.*?)(?=^\[|\z)')
    if (-not $m.Success) { return @() }
    $a = [regex]::Match($m.Groups[1].Value, 'args\s*=\s*\[(.*?)\]')
    if (-not $a.Success) { return @() }
    return @([regex]::Matches($a.Groups[1].Value, '"((?:[^"\\]|\\.)*)"') | ForEach-Object { $_.Groups[1].Value -replace '\\(.)', '$1' })
}

# Fresh installs default the server to the full tool surface: seed
# rvtmcp.config.json with toolsets=["all"]. An existing toolsets key is the
# user's explicit choice and is kept; --toolsets/--read-only args and
# BIMWRIGHT_* env vars still outrank the file. The write rides the install
# transaction, so a failed install restores the previous config.
function Set-DefaultToolsetsConfig {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param([string]$ConfigPath)
    $full = [IO.Path]::GetFullPath($ConfigPath)
    $existed = Test-Path -LiteralPath $full
    $root = $null
    if ($existed) {
        try { $root = Get-Content -LiteralPath $full -Raw | ConvertFrom-Json }
        catch {
            Write-Warning ("[config] {0} is not valid JSON; left unchanged." -f $full)
            return 'skipped'
        }
        if ($null -ne $root -and $root.PSObject.Properties.Match('toolsets').Count) { return 'kept' }
    }
    if (-not $PSCmdlet.ShouldProcess($full, 'Seed default toolsets=all in rvtmcp.config.json')) {
        Write-Host ("[config] preview seed toolsets=all -> {0}" -f $full)
        return 'previewed'
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $full) -Force | Out-Null
    if ($existed) { Move-ToRollback $full }
    else { $script:installChanges.Add([pscustomobject]@{Path=$full;Backup=$null}) }
    if ($null -eq $root) { $root = [pscustomobject]@{} }
    $root | Add-Member -NotePropertyName toolsets -NotePropertyValue @('all') -Force
    [IO.File]::WriteAllText($full, ($root | ConvertTo-Json -Depth 8))
    return $(if ($existed) { 'merged' } else { 'seeded' })
}

# A client counts as installed when its CLI is on PATH, its config file
# exists, or its config directory does.
function Test-McpClientDetected($spec) {
    if ($spec.Cli -and (Get-Command $spec.Cli -ErrorAction SilentlyContinue)) { return $true }
    foreach ($p in @($spec.Paths) + @($spec.DetectPaths)) {
        if ($p -and (Test-Path $p)) { return $true }
    }
    return $false
}

# Wire (Mode=Add) or unwire (Mode=Remove) the requested clients.
# $Clients entries: names, 'auto'/'all' (every detected client), 'none'.
# Per-client failures are collected, never thrown - a bad client edit must
# not roll back an otherwise-good install.
function Invoke-McpClientWiring {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param([string[]]$Clients, [string]$Exe, [ValidateSet('Add', 'Remove')][string]$Mode)
    $report = New-Object System.Collections.Generic.List[string]
    $requested = @($Clients | Where-Object { $_ -and $_ -ne 'none' })
    if ($requested.Count -eq 0) { return $report }

    $specs = Get-McpClientSpecs
    if ($requested | Where-Object { $_ -in @('auto', 'all', 'Auto') }) {
        $requested = @($specs | Where-Object { Test-McpClientDetected $_ } | ForEach-Object { $_.Name })
        if ($requested.Count -eq 0) { $report.Add('auto: no MCP clients detected'); return $report }
    }

    foreach ($name in $requested) {
        $spec = $specs | Where-Object { $_.Name -eq $name } | Select-Object -First 1
        if (-not $spec) { $report.Add("${name}: unknown client"); continue }
        try {
            switch ($spec.Kind) {
                'cli' {
                    $cli = Get-Command $spec.Cli -ErrorAction SilentlyContinue
                    if (-not $cli -and $spec.Name -eq 'claude' -and $spec.Paths -and (Test-Path $spec.Paths[0])) {
                        # Claude Code without the CLI on PATH: ~/.claude.json
                        # carries the user-scope mcpServers key - same shape.
                        $st = Set-McpConfigEntry -Path (Resolve-McpConfigPath $spec) -RootKey $spec.RootKey -EntryKind $spec.EntryKind -Exe $Exe -Remove:($Mode -eq 'Remove')
                        $report.Add("claude: $st (file fallback ~/.claude.json)")
                        continue
                    }
                    if (-not $cli) { $report.Add("$($spec.Name): $($spec.Cli) CLI not on PATH"); continue }
                    if ($Mode -eq 'Add') {
                        if (-not $PSCmdlet.ShouldProcess($spec.Name, 'wire rvt-mcp via client CLI')) { $report.Add("$($spec.Name): previewed"); continue }
                        switch ($spec.Name) {
                            'claude' {
                                $null = & claude mcp remove rvt-mcp -s local 2>$null  # drop any project-local shadow
                                $out = & claude mcp add -s user rvt-mcp -- $Exe 2>&1
                                $verify = & claude mcp get rvt-mcp 2>&1
                            }
                            'codex' {
                                $toml = Join-Path ($(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' })) 'config.toml'
                                $oldArgs = Read-CodexEntryArgs $toml
                                $out = & codex mcp add rvt-mcp -- $Exe @oldArgs 2>&1
                                $verify = & codex mcp get rvt-mcp --json 2>&1
                            }
                            'grok' {
                                $out = & grok mcp add rvt-mcp $Exe --transport stdio 2>&1
                                $verify = & grok mcp list 2>&1
                            }
                        }
                        $verified = ($verify | Out-String) -match 'rvt-mcp'
                        $report.Add("$($spec.Name): " + $(if ($verified) { 'wired' } else { 'wired (verify inconclusive - check the client)' }))
                        if ($spec.Name -eq 'grok' -and ($verify | Out-String) -match '\(project\)') {
                            $report.Add('grok: a project-scope entry may shadow the user one - check grok mcp list from the repo')
                        }
                    } else {
                        if (-not $PSCmdlet.ShouldProcess($spec.Name, 'remove rvt-mcp via client CLI')) { $report.Add("$($spec.Name): previewed"); continue }
                        switch ($spec.Name) {
                            'claude' { $null = & claude mcp remove rvt-mcp -s user 2>&1 }
                            'codex'  { $null = & codex mcp remove rvt-mcp 2>&1 }
                            'grok'   { $null = & grok mcp remove rvt-mcp 2>&1 }
                        }
                        $report.Add("$($spec.Name): removed")
                    }
                }
                'file' {
                    if ($Mode -eq 'Add' -and -not (Test-McpClientDetected $spec)) {
                        $report.Add("$($spec.Name): not detected - skipping (checked: $($spec.DetectPaths -join '; '))")
                        continue
                    }
                    $path = Resolve-McpConfigPath $spec
                    if (-not $path) { $report.Add("$($spec.Name): no config file found - wire it manually per docs/mcp-client-wiring.md"); continue }
                    $status = Set-McpConfigEntry -Path $path -RootKey $spec.RootKey -EntryKind $spec.EntryKind -Exe $Exe -Remove:($Mode -eq 'Remove')
                    $report.Add("$($spec.Name): $status -> $path")
                    # Some apps (Claude Desktop) persist their config from
                    # memory on quit - an edit made while the app runs can be
                    # overwritten. Surface it instead of silently losing the entry.
                    if ($spec.ProcName -and $status -in @('added', 'created', 'repointed', 'removed')) {
                        $running = @(Get-Process -Name $spec.ProcName -ErrorAction SilentlyContinue |
                            Where-Object { -not $spec.ProcPathLike -or $_.Path -like $spec.ProcPathLike })
                        if ($running.Count) {
                            $report.Add("$($spec.Name): app is running - fully quit it and verify the entry survives before trusting it")
                        }
                    }
                }
                'deeplink' {
                    if ($Mode -eq 'Remove') { $report.Add('cherry-studio: remove rvt-mcp in Settings -> MCP Servers'); continue }
                    $json = '{"mcpServers":{"rvt-mcp":{"command":"' + $Exe.Replace('\', '\\') + '","args":[]}}}'
                    $url = 'cherrystudio://mcp/install?servers=' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
                    $report.Add('cherry-studio: open this URL to install: ' + $url)
                }
            }
        } catch {
            $report.Add("$($spec.Name): failed - $($_.Exception.Message)")
        }
    }
    return $report
}

if (-not $Years -or $Years.Count -eq 0) {
    if ($Uninstall) {
        # Removal must not depend on Revit still being installed.
        $Years = 2022..2027
    } else {
        $Years = @(Get-InstalledRevitYears)
        if ($Years.Count -eq 0) {
            Write-Warning "No installed Revit 2022-2027 found (no Revit.exe). Use -Years to force an explicit list."
            return
        }
        Write-Host ("Detected Revit years: {0}" -f ($Years -join ', '))
    }
}

$agentsGuide = Get-AgentsGuidePath $PSScriptRoot
if ($WireClient) {
    Write-Warning 'install.ps1 -WireClient is deprecated; merged into -Client.'
    $Client = @($Client | Where-Object { $_ -ne 'none' }) + @($WireClient)
}

$handled = @()
$skipped = @()
$previewed = @()
$removedDuplicates = @()
$verified = @()
$inUse = @()
$legacyServers = @()
$serverCommand = $null
$serverCheck = 'not run'
$configDefault = $null
$wiredClients = @()
# A caller-supplied -ServerInstallRoot's parent is not ours to prune or sweep.
$ownServerRoot = -not $PSBoundParameters.ContainsKey('ServerInstallRoot')
$Years = @($Years | Sort-Object -Unique)
$script:installChanges = New-Object System.Collections.Generic.List[object]
$script:installStage = $null

try {
    # Validate every selected payload before replacing any installed file.
    if (-not $WhatIfPreference) { Assert-RevitClosed }
    if (-not $Uninstall) {
        Assert-SetupManifest -Root $SourceDir -Manifest $manifest
        foreach ($year in $Years) {
            $yearTwo = $year - 2000
            $zip = Join-Path $pluginSourceDir "RvtMcp.Plugin.R$yearTwo.zip"
            if (-not (Test-Path -LiteralPath $zip)) { throw "Missing plugin ZIP for Revit ${year}: $zip" }
            Assert-PluginArchive -Zip $zip -AddinFile "RvtMcp.R$yearTwo.addin" -Year $year
        }
        if ($serverSourceDir -and -not (Find-ServerSourceExe $serverSourceDir)) { throw 'Setup server executable is missing.' }
    }
    # A machine-wide manifest with the same AddInId would shadow or be shadowed
    # by the per-user one, and a per-user installer cannot remove it.
    foreach ($year in $Years) {
        $machine = @(Find-RvtMcpManifests -Folder (Get-MachineAddinsRoot $year) -Year $year)
        if ($machine.Count) {
            $message = "A machine-wide RvtMcp add-in for Revit $year exists at $($machine -join ', ') (same AddInId). Remove it with administrator rights"
            if ($Uninstall) { Write-Warning "$message; it was left in place." }
            else { throw "$message, then run the installer again. Nothing was changed." }
        }
    }
    if (-not $Uninstall) {
        if (-not $WhatIfPreference) {
            $script:installStage = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('rvtmcp-install-' + [guid]::NewGuid().ToString('N'))))
            New-Item -ItemType Directory -Path $script:installStage | Out-Null
            foreach ($year in $Years) {
                $zip = Join-Path $pluginSourceDir ("RvtMcp.Plugin.R{0}.zip" -f ($year - 2000))
                Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $script:installStage "$year")
            }
            if ($serverSourceDir) {
                Copy-Item -LiteralPath $serverSourceDir -Destination (Join-Path $script:installStage 'server') -Recurse
                $serverSourceDir = Join-Path $script:installStage 'server'
            }
            # Recheck after staging; a user may have launched Revit meanwhile.
            Assert-RevitClosed
        }
    }

    foreach ($year in $Years) {
        $yearTwo = "{0:D2}" -f ($year - 2000)
        $addinFile = "RvtMcp.R$yearTwo.addin"
        $addinsRoot = Get-AddinsRoot $year
        $pluginDir = Join-Path $addinsRoot 'RvtMcp'
        $addinPath = Join-Path $addinsRoot $addinFile
        $duplicates = @(Get-DuplicateAddinItems -Year $year -AddinsRoot $addinsRoot -KeepManifest $addinPath)

        if ($Uninstall) {
            $targets = @(@($pluginDir, $addinPath) + $duplicates | Where-Object { Test-Path -LiteralPath $_ })
            foreach ($item in $targets) {
                if ($PSCmdlet.ShouldProcess($item, 'Remove RvtMcp add-in')) { Remove-Item -LiteralPath $item -Recurse -Force }
            }
            foreach ($item in $duplicates) { $removedDuplicates += ("{0}\{1}" -f $year, (Split-Path -Leaf $item)) }
            if ($targets.Count) {
                if ($WhatIfPreference) {
                    Write-Host ("[R{0}] preview uninstall from {1}" -f $yearTwo, $addinsRoot)
                    $previewed += "R$yearTwo"
                } else {
                    Write-Host ("[R{0}] uninstalled from {1}" -f $yearTwo, $addinsRoot)
                    $handled += "R$yearTwo"
                }
            } else {
                Write-Host ("[R{0}] nothing to remove at {1}" -f $yearTwo, $addinsRoot)
                $skipped += "R$yearTwo"
            }
            continue
        }

        foreach ($item in $duplicates) {
            if ($PSCmdlet.ShouldProcess($item, 'Remove duplicate RvtMcp add-in (same AddInId)')) { Move-ToRollback $item }
            $removedDuplicates += ("{0}\{1}" -f $year, (Split-Path -Leaf $item))
        }

        if ($PSCmdlet.ShouldProcess($pluginDir, 'Install staged plugin and addin manifest with rollback')) {
            $stagedPlugin = Join-Path $script:installStage "$year"
            Set-InstallPath -Source (Join-Path $stagedPlugin $addinFile) -Destination $addinPath
            Set-InstallPath -Source $stagedPlugin -Destination $pluginDir
        }

        if ($WhatIfPreference) {
            Write-Host ("[R{0}] preview install -> {1}" -f $yearTwo, $pluginDir)
            $previewed += "R$yearTwo"
        } else {
            Write-Host ("[R{0}] installed -> {1}" -f $yearTwo, $pluginDir)
            $handled += "R$yearTwo"
        }
    }

    if (-not $Uninstall) {
        $serverCommand = Install-RvtMcpServer -ServerDir $serverSourceDir -InstallRoot $ServerInstallRoot
        if ($serverCommand -and $ownServerRoot) {
            if ($PruneOldServers) {
                Remove-StaleServerVersions -InstallRoot $ServerInstallRoot
            } else {
                $legacyServers = @(Get-OtherServerVersions -InstallRoot $ServerInstallRoot)
            }
        }
        if ($serverCommand -and -not $WhatIfPreference) {
            # A browser-downloaded ZIP extracted by Explorer marks every file as
            # coming from the Internet; Copy-Item/Move-Item keep that mark.
            Get-ChildItem -LiteralPath $ServerInstallRoot -Recurse -File | Unblock-File
            Test-ServerExecutable -Path $serverCommand
            $serverCheck = 'OK'
        } elseif ($serverCommand) {
            $serverCheck = 'skipped (WhatIf)'
        }
        $configDefault = Set-DefaultToolsetsConfig -ConfigPath (Join-Path $env:LOCALAPPDATA 'RvtMcp\rvtmcp.config.json')
        if (@($Client | Where-Object { $_ -ne 'none' }).Count) {
            if ($serverCommand) {
                $wiredClients = @(Invoke-McpClientWiring -Clients $Client -Exe $serverCommand -Mode Add)
            } else {
                $wiredClients = @('-Client requested but this package has no server to point at')
            }
        } elseif (-not $WhatIfPreference) {
            $detected = @(Get-McpClientSpecs | Where-Object { Test-McpClientDetected $_ } | ForEach-Object { $_.Name })
            if ($detected.Count) {
                $wiredClients = @("detected: $($detected -join ', ') - wire with -Client <names> or -Client auto")
            }
        }
        if (-not $WhatIfPreference) {
            foreach ($year in $Years) {
                $yearTwo = "{0:D2}" -f ($year - 2000)
                $addinsRoot = Get-AddinsRoot $year
                Assert-InstalledPlugin -Year $year -Zip (Join-Path $pluginSourceDir "RvtMcp.Plugin.R$yearTwo.zip") -AddinPath (Join-Path $addinsRoot "RvtMcp.R$yearTwo.addin") -PluginDir (Join-Path $addinsRoot 'RvtMcp')
                $verified += "R$yearTwo"
            }
        }
    }
    if ($Uninstall -and @($Client | Where-Object { $_ -ne 'none' }).Count) {
        $wiredClients = @(Invoke-McpClientWiring -Clients $Client -Mode Remove)
    }
} catch {
    $installFailure = $_
    Undo-InstallChanges
    throw $installFailure
} finally {
    if ($script:installStage) {
        try { Remove-InstallPath $script:installStage } catch { Write-Warning "Staging cleanup failed: $_" }
    }
}
# Keep backups until every add-in and server change has succeeded.
foreach ($change in $script:installChanges) {
    if (-not $change.Backup -or -not (Test-Path -LiteralPath $change.Backup)) { continue }
    if (Test-ServerCopy $change.Backup) {
        if (-not (Remove-ServerCopy $change.Backup)) { $inUse += $change.Backup }
    } else {
        try { Remove-InstallPath $change.Backup } catch { Write-Warning "Backup retained at $($change.Backup): $_" }
    }
}
# Previous runs may have left copies that a client was still running.
if (-not $Uninstall -and -not $WhatIfPreference -and $serverCommand -and $ownServerRoot) {
    foreach ($dir in Get-LeftoverServerCopies (Split-Path -Parent ([IO.Path]::GetFullPath($ServerInstallRoot)))) {
        if ($inUse -contains $dir.FullName) { continue }
        if (-not (Remove-ServerCopy $dir.FullName)) { $inUse += $dir.FullName }
    }
}
$script:installChanges = $null

Write-Host ""
Write-Host "=== install.ps1 summary ==="
Write-Host ("Mode    : {0}" -f $(if ($Uninstall) { 'Uninstall' } else { 'Install' }))
if (-not $Uninstall) { Write-Host ("Version : {0}" -f $setupVersion) }
Write-Host ("Source  : {0}" -f $SourceDir)
Write-Host ("Years   : {0}" -f ($Years -join ', '))
Write-Host ("Handled : {0}" -f $(if ($handled.Count) { $handled -join ', ' } else { 'none' }))
if ($previewed.Count) { Write-Host ("Previewed: {0}" -f ($previewed -join ', ')) }
if ($skipped.Count) { Write-Host ("Skipped : {0}" -f ($skipped -join ', ')) }
if ($removedDuplicates.Count) { Write-Host ("Removed : {0}{1}" -f ($removedDuplicates -join ', '), $(if ($WhatIfPreference) { ' (preview)' } else { '' })) }
if ($verified.Count) { Write-Host ("Verified: {0} match the package" -f ($verified -join ', ')) }
if (-not $Uninstall) {
    if ($serverCommand) { Write-Host ("Server  : {0} (check: {1})" -f $serverCommand, $serverCheck) }
    else { Write-Host 'Server  : not in this package' }
    switch ($configDefault) {
        'seeded'    { Write-Host ("Config  : seeded toolsets=all -> {0}\RvtMcp\rvtmcp.config.json" -f $env:LOCALAPPDATA) }
        'merged'    { Write-Host ("Config  : added toolsets=all to {0}\RvtMcp\rvtmcp.config.json" -f $env:LOCALAPPDATA) }
        'kept'      { Write-Host 'Config  : kept existing toolsets setting' }
        'skipped'   { Write-Host 'Config  : unreadable config - unchanged' }
        'previewed' { Write-Host ("Config  : preview seed toolsets=all -> {0}\RvtMcp\rvtmcp.config.json" -f $env:LOCALAPPDATA) }
    }
}
foreach ($w in $wiredClients) { Write-Host ("Client  : {0}" -f $w) }
if ($Uninstall -and -not @($Client | Where-Object { $_ -ne 'none' }).Count) {
    Write-Host 'Client  : entries left in place - re-run with -Client <names> to unwire'
}
if ($inUse.Count) { Write-Host ("In use  : {0} - restart MCP clients; removed at next install" -f ($inUse -join ', ')) }
if ($legacyServers.Count) { Write-Host ("Legacy  : {0} - repoint clients to the Server path, then run install.ps1 -PruneOldServers" -f (($legacyServers | ForEach-Object { $_.Name }) -join ', ')) }
if (-not $Uninstall) { Write-Host ("Next    : connect MCP clients - see {0}" -f $agentsGuide) }
