#Requires -Version 5.1
<#
.SYNOPSIS
  Install or uninstall RvtMcp Revit client components.

.DESCRIPTION
  In a client setup ZIP, this script installs:
    - the self-contained MCP server from server/
    - matching per-Revit plugin ZIPs from plugins/
    - optional MCP client config entries using an absolute server path

  It also remains compatible with the older plugin-only release layout where
  per-Revit plugin ZIPs sit beside this script and the server is installed as a
  .NET global tool by the user.

.PARAMETER SourceDir
  Setup root or plugin ZIP source directory. Defaults to the current setup root
  when server/ or plugins/ exists beside this script; otherwise defaults to
  build/plugin-zip/ relative to the repo root.

.PARAMETER Client
  MCP client wiring mode. Auto wires every known installed config it can find.
  Explicit values wire only that client. none installs files without config edits.

.PARAMETER WireClient
  Backward-compatible alias for older scripts. Overrides -Client when set.

.EXAMPLE
  pwsh .\install.ps1 -WhatIf
  pwsh .\install.ps1
  pwsh .\install.ps1 -Client codex
  pwsh .\install.ps1 -Years 2024 -Client none
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SourceDir,
    [switch]$Uninstall,
    [ValidateRange(2022, 2027)][int[]]$Years,
    [ValidateSet('Auto', 'codex', 'opencode', 'kilo', 'claude', 'none')]
    [string]$Client = 'Auto',
    [ValidateSet('opencode', 'codex', 'kilo')]
    [string]$WireClient,
    [string]$ServerInstallRoot
)

$ErrorActionPreference = 'Stop'

if ($WireClient) {
    $Client = $WireClient
}

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

if (-not $ServerInstallRoot) {
    $ServerInstallRoot = Join-Path $env:LOCALAPPDATA ("RvtMcp\rvt\server\{0}" -f $setupVersion)
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

function Find-ServerSourceExe {
    param([string]$ServerDir)
    if (-not $ServerDir) { return $null }
    $preferred = Join-Path $ServerDir 'rvt-mcp.exe'
    if (Test-Path $preferred) { return $preferred }
    $fallback = Join-Path $ServerDir 'RvtMcp.Server.exe'
    if (Test-Path $fallback) { return $fallback }
    return $null
}

function Get-RvtMcpClientTargets {
    param(
        [int[]]$years,
        [string]$serverCommand
    )
    $supportedYears = @($years | Where-Object { $_ -ge 2022 -and $_ -le 2027 })
    if ($supportedYears.Count -eq 0) {
        return @()
    }

    return ,([pscustomobject]@{
        Name      = 'rvt-mcp'
        ServerCmd = $serverCommand
        Args      = @()
        Years     = $supportedYears
    })
}

function Write-ConfigAtomic {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )
    $bak = "$Path.rvtmcp.bak"
    if (Test-Path -LiteralPath $Path) { Copy-Item -LiteralPath $Path -Destination $bak -Force }
    Save-InstallConfig -Path $Path
    $temp = "$Path.rvtmcp.tmp"
    try {
        Set-Content -Path $temp -Value $Content -Encoding UTF8 -NoNewline
        if (Test-Path -LiteralPath $Path) {
            [System.IO.File]::Replace($temp, $Path, [NullString]::Value)
        } else {
            [System.IO.File]::Move($temp, $Path)
        }
    } catch {
        if (Test-Path $temp) { Remove-Item $temp -Force -ErrorAction SilentlyContinue }
        throw
    }
    return $bak
}

function ConvertTo-Hashtable {
    param([Parameter(ValueFromPipeline = $true)][object]$InputObject)

    process {
        if ($null -eq $InputObject) { return $null }

        if ($InputObject -is [System.Collections.IDictionary]) {
            $hash = @{}
            foreach ($key in $InputObject.Keys) {
                $hash[$key] = ConvertTo-Hashtable $InputObject[$key]
            }
            return $hash
        }

        if ($InputObject -is [pscustomobject]) {
            $hash = @{}
            foreach ($property in $InputObject.PSObject.Properties) {
                $hash[$property.Name] = ConvertTo-Hashtable $property.Value
            }
            return $hash
        }

        if (($InputObject -is [System.Collections.IEnumerable]) -and -not ($InputObject -is [string])) {
            $items = @()
            foreach ($item in $InputObject) {
                $items += ConvertTo-Hashtable $item
            }
            return ,$items
        }

        return $InputObject
    }
}

function Read-JsonHashtable {
    param([string]$Path)
    $raw = Get-Content -Raw -Path $Path
    if ([string]::IsNullOrWhiteSpace($raw)) { return @{} }
    return ConvertTo-Hashtable ($raw | ConvertFrom-Json)
}

function ConvertTo-TomlString {
    param([string]$Value)
    return '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"'
}

function ConvertTo-TomlStringArray {
    param([object[]]$Values)
    $items = @()
    foreach ($v in @($Values)) {
        if ($null -ne $v) {
            $items += (ConvertTo-TomlString -Value ([string]$v))
        }
    }
    return '[' + ($items -join ', ') + ']'
}

function Assert-ManagedServerCommand([string]$Command) {
    if (($Command -split '[\\/]')[-1] -notin @('rvt-mcp', 'rvt-mcp.exe', 'RvtMcp.Server.exe')) {
        throw "Custom rvt-mcp launcher '$Command' cannot be upgraded automatically. Use -Client none and update its server path manually."
    }
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

function Save-InstallConfig([string]$Path) {
    if ($null -eq $script:installChanges) { return }
    $full = [IO.Path]::GetFullPath($Path)
    if (@($script:installChanges | Where-Object { $_.Path -eq $full }).Count) { return }
    $backup = $null
    if (Test-Path -LiteralPath $full) {
        $backup = $full + '.rvtmcp-rollback-' + [guid]::NewGuid().ToString('N')
        Copy-Item -LiteralPath $full -Destination $backup
    }
    $script:installChanges.Add([pscustomobject]@{Path=$full;Backup=$backup;Config=$true})
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
    $script:installChanges.Add([pscustomobject]@{Path=$full;Backup=$backup;Config=$false})
    Move-Item -LiteralPath $Source -Destination $full
}

function Undo-InstallChanges {
    $failures = @()
    for ($i = $script:installChanges.Count - 1; $i -ge 0; $i--) {
        $change = $script:installChanges[$i]
        try {
            if ($change.Config -and $change.Backup -and (Test-Path -LiteralPath $change.Path)) {
                [IO.File]::Replace($change.Backup, $change.Path, [NullString]::Value)
            } else {
                Remove-InstallPath $change.Path
                if ($change.Backup) { Move-Item -LiteralPath $change.Backup -Destination $change.Path }
            }
        } catch { $failures += "$($change.Path): $_ (backup: $($change.Backup))" }
    }
    if ($failures.Count) { throw ("Rollback incomplete; retain backup files and restore manually:`n" + ($failures -join "`n")) }
}

function Assert-RevitClosed {
    if (@(Get-Process -Name Revit -ErrorAction SilentlyContinue).Count) {
        throw 'Revit running. Close every Revit window before installing or uninstalling plugins; no files have been replaced.'
    }
}

function Assert-PluginArchive([string]$Zip, [string]$AddinFile) {
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

function Add-OpencodeEntry {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)][string]$ConfigPath,
        [Parameter(Mandatory = $true)][object[]]$Targets,
        [switch]$RequireExisting
    )

    if (-not (Test-Path $ConfigPath)) {
        $msg = "[opencode] config not found at $ConfigPath"
        if ($RequireExisting) { Write-Warning "$msg - skipping wire" } else { Write-Host "$msg - skipping" }
        return $false
    }

    try {
        $cfg = Read-JsonHashtable -Path $ConfigPath
    } catch {
        throw ("[opencode] parse failed at {0}: {1}" -f $ConfigPath, $_.Exception.Message)
    }

    if (-not $cfg.ContainsKey('mcp')) { $cfg['mcp'] = @{} }

    $desired = @{}
    foreach ($t in $Targets) {
        $name = $t.Name
        $entry = [ordered]@{
            type    = 'local'
            command = @($t.ServerCmd) + @($t.Args)
            enabled = $true
        }
        if ($t.PSObject.Properties.Name -contains 'Env' -and $t.Env -and $t.Env.Count -gt 0) {
            $entry['environment'] = $t.Env
        }
        if ($cfg['mcp'].ContainsKey($name)) {
            $entry = $cfg['mcp'][$name].Clone()
            if ($entry.type -ne 'local' -or $entry.command -is [string] -or @($entry.command).Count -eq 0) { throw "Unsupported OpenCode entry: $name. Use -Client none and wire manually." }
            Assert-ManagedServerCommand $entry.command[0]
            $entry.command = @($t.ServerCmd) + @($entry.command | Select-Object -Skip 1)
        }
        $desired[$name] = $entry
    }

    $changed = $false
    foreach ($k in $desired.Keys) {
        $existingJson = if ($cfg['mcp'].ContainsKey($k)) { ConvertTo-Json -InputObject $cfg['mcp'][$k].command -Compress } else { $null }
        $newJson = ConvertTo-Json -InputObject $desired[$k].command -Compress
        if ($existingJson -ne $newJson) {
            $cfg['mcp'][$k] = $desired[$k]
            $changed = $true
        }
    }

    if (-not $changed) {
        Write-Host ("[opencode] no changes needed at {0}" -f $ConfigPath)
        return $true
    }

    if ($PSCmdlet.ShouldProcess($ConfigPath, 'Upsert rvt-mcp entry')) {
        $content = $cfg | ConvertTo-Json -Depth 50
        $bak = Write-ConfigAtomic -Path $ConfigPath -Content $content
        Write-Host ("[opencode] wired {0} entry -> {1} (backup: {2})" -f $desired.Count, $ConfigPath, $bak)
    }
    return $true
}

function Add-KiloEntry {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)][string]$ConfigPath,
        [Parameter(Mandatory = $true)][object[]]$Targets,
        [switch]$RequireExisting
    )

    if (-not (Test-Path $ConfigPath)) {
        # Kilo will create the config dir on first run; create the file if user opted in
        # explicitly (-Client kilo). Auto mode still requires the file to exist.
        if ($RequireExisting) {
            # Defer creation until ShouldProcess approves the complete write.
        } else {
            Write-Host "[kilo] config not found at $ConfigPath - skipping"
            return $false
        }
    }

    try {
        $cfg = if (Test-Path -LiteralPath $ConfigPath) { Read-JsonHashtable -Path $ConfigPath } else { @{} }
    } catch {
        throw ("[kilo] parse failed at {0}: {1}" -f $ConfigPath, $_.Exception.Message)
    }

    if (-not $cfg.ContainsKey('mcp')) { $cfg['mcp'] = @{} }

    $desired = @{}
    foreach ($t in $Targets) {
        $name = $t.Name
        $entry = [ordered]@{
            type    = 'local'
            command = @($t.ServerCmd) + @($t.Args)
            enabled = $true
            timeout = 30000
        }
        if ($t.PSObject.Properties.Name -contains 'Env' -and $t.Env -and $t.Env.Count -gt 0) {
            $entry['environment'] = $t.Env
        }
        if ($cfg['mcp'].ContainsKey($name)) {
            $entry = $cfg['mcp'][$name].Clone()
            if ($entry.type -ne 'local' -or $entry.command -is [string] -or @($entry.command).Count -eq 0) { throw "Unsupported Kilo entry: $name. Use -Client none and wire manually." }
            Assert-ManagedServerCommand $entry.command[0]
            $entry.command = @($t.ServerCmd) + @($entry.command | Select-Object -Skip 1)
        }
        $desired[$name] = $entry
    }

    $changed = $false
    foreach ($k in $desired.Keys) {
        $existingJson = if ($cfg['mcp'].ContainsKey($k)) { ConvertTo-Json -InputObject $cfg['mcp'][$k].command -Compress } else { $null }
        $newJson = ConvertTo-Json -InputObject $desired[$k].command -Compress
        if ($existingJson -ne $newJson) {
            $cfg['mcp'][$k] = $desired[$k]
            $changed = $true
        }
    }

    if (-not $changed) {
        Write-Host ("[kilo] no changes needed at {0}" -f $ConfigPath)
        return $true
    }

    if ($PSCmdlet.ShouldProcess($ConfigPath, 'Upsert rvt-mcp entry')) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $ConfigPath) -Force | Out-Null
        $content = $cfg | ConvertTo-Json -Depth 50
        $bak = Write-ConfigAtomic -Path $ConfigPath -Content $content
        Write-Host ("[kilo] wired {0} entry -> {1} (backup: {2})" -f $desired.Count, $ConfigPath, $bak)
    }
    return $true
}

function Add-CodexEntry {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)][string]$ConfigPath,
        [Parameter(Mandatory = $true)][object[]]$Targets,
        [switch]$RequireExisting
    )

    if (-not (Test-Path $ConfigPath)) {
        $msg = "[codex] config not found at $ConfigPath"
        if ($RequireExisting) { Write-Warning "$msg - skipping wire" } else { Write-Host "$msg - skipping" }
        return $false
    }

    $raw = Get-Content -Raw -Path $ConfigPath -Encoding UTF8
    if ($null -eq $raw) { $raw = '' }

    $changed = $false
    # A line-based edit deliberately refuses multiline strings rather than
    # mistaking their contents for TOML headers/keys. Custom layouts stay intact.
    if ($raw.Contains('"""') -or $raw.Contains("'''")) {
        throw "Multiline TOML in $ConfigPath requires manual wiring. Use -Client none."
    }

    foreach ($t in $Targets) {
        $name = $t.Name
        $headerLiteral = "[mcp_servers.$name]"
        $commandValue = ConvertTo-TomlString -Value $t.ServerCmd
        $argsValue = ConvertTo-TomlStringArray -Values $t.Args
        $envLines = ''
        if ($t.PSObject.Properties.Name -contains 'Env' -and $t.Env -and $t.Env.Count -gt 0) {
            $envHeader = "[mcp_servers.$name.env]"
            $envBody = @()
            foreach ($k in $t.Env.Keys) { $envBody += ("{0} = {1}" -f $k, (ConvertTo-TomlString -Value ([string]$t.Env[$k]))) }
            $envLines = "`n`n" + $envHeader + "`n" + ($envBody -join "`n")
        }
        $desiredBlock = @"
$headerLiteral
command = $commandValue
args = $argsValue
enabled = true$envLines
"@

        $key = '(?:' + [regex]::Escape($name) + '|"' + [regex]::Escape($name) + '"|''' + [regex]::Escape($name) + ''')'
        $pattern = '(?ms)^[ \t]*\[[ \t]*(?:mcp_servers|"mcp_servers"|''mcp_servers'')[ \t]*\.[ \t]*' + $key + '[ \t]*\][^\r\n]*\r?\n.*?(?=^[ \t]*\[|\z)'
        $existingMatch = [regex]::Match($raw, $pattern)
        if ($existingMatch.Success) {
            if ([regex]::Matches($raw, $pattern).Count -ne 1) { throw 'Duplicate rvt-mcp TOML tables; wire manually with -Client none.' }
            $commandPattern = '(?m)^([ \t]*command[ \t]*=[ \t]*)("(?:[^"\\\r\n]|\\.)*"|''[^''\r\n]*'')([ \t]*(?:#[^\r\n]*)?)(?=\r?$)'
            $commandMatch = [regex]::Match($existingMatch.Value, $commandPattern)
            if ([regex]::Matches($existingMatch.Value, $commandPattern).Count -ne 1) { throw 'Custom or duplicate rvt-mcp TOML command; wire manually with -Client none.' }
            $oldValue = $commandMatch.Groups[2].Value
            $oldCommand = if ($oldValue.StartsWith("'")) { $oldValue.Substring(1, $oldValue.Length - 2) } else { $oldValue | ConvertFrom-Json }
            Assert-ManagedServerCommand $oldCommand
            $replacement = $commandMatch.Groups[1].Value + $commandValue + $commandMatch.Groups[3].Value
            $block = $existingMatch.Value.Remove($commandMatch.Index, $commandMatch.Length).Insert($commandMatch.Index, $replacement)
            if ($block -ne $existingMatch.Value) {
                $raw = $raw.Remove($existingMatch.Index, $existingMatch.Length).Insert($existingMatch.Index, $block)
                $changed = $true
            }
        } else {
            if ($raw -match '(?m)^[^#\r\n]*rvt-mcp') { throw 'Custom rvt-mcp TOML layout; wire manually with -Client none.' }
            $sep = if ($raw.EndsWith("`n")) { "`n" } else { "`n`n" }
            $raw = $raw + $sep + $desiredBlock + "`n"
            $changed = $true
        }
    }

    if (-not $changed) {
        Write-Host ("[codex] no changes needed at {0}" -f $ConfigPath)
        return $true
    }

    if ($PSCmdlet.ShouldProcess($ConfigPath, 'Upsert [mcp_servers.rvt-mcp] block')) {
        $bak = Write-ConfigAtomic -Path $ConfigPath -Content $raw
        Write-Host ("[codex] wired rvt-mcp block -> {0} (backup: {1})" -f $ConfigPath, $bak)
    }
    return $true
}

function Add-ClaudeEntry {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)][string]$ConfigPath,
        [Parameter(Mandatory = $true)][object[]]$Targets,
        [switch]$RequireExisting
    )

    if (-not (Test-Path $ConfigPath)) {
        $msg = "[claude] config not found at $ConfigPath"
        if ($RequireExisting) { Write-Warning "$msg - skipping wire" } else { Write-Host "$msg - skipping" }
        return $false
    }

    try {
        $cfg = Read-JsonHashtable -Path $ConfigPath
    } catch {
        throw ("[claude] parse failed at {0}: {1}" -f $ConfigPath, $_.Exception.Message)
    }

    if (-not $cfg.ContainsKey('mcpServers')) { $cfg['mcpServers'] = @{} }

    $desired = @{}
    foreach ($t in $Targets) {
        $name = $t.Name
        $entry = [ordered]@{
            command = $t.ServerCmd
            args = @($t.Args)
        }
        if ($t.PSObject.Properties.Name -contains 'Env' -and $t.Env -and $t.Env.Count -gt 0) {
            $entry['env'] = $t.Env
        }
        if ($cfg['mcpServers'].ContainsKey($name)) {
            $entry = $cfg['mcpServers'][$name].Clone()
            Assert-ManagedServerCommand $entry.command
            $entry.command = $t.ServerCmd
        }
        $desired[$name] = $entry
    }

    $changed = $false
    foreach ($k in $desired.Keys) {
        $existingJson = if ($cfg['mcpServers'].ContainsKey($k)) { $cfg['mcpServers'][$k].command } else { $null }
        $newJson = $desired[$k].command
        if ($existingJson -ne $newJson) {
            $cfg['mcpServers'][$k] = $desired[$k]
            $changed = $true
        }
    }

    if (-not $changed) {
        Write-Host ("[claude] no changes needed at {0}" -f $ConfigPath)
        return $true
    }

    if ($PSCmdlet.ShouldProcess($ConfigPath, 'Upsert mcpServers.rvt-mcp entry')) {
        $content = $cfg | ConvertTo-Json -Depth 50
        $bak = Write-ConfigAtomic -Path $ConfigPath -Content $content
        Write-Host ("[claude] wired {0} entry -> {1} (backup: {2})" -f $desired.Count, $ConfigPath, $bak)
    }
    return $true
}

function Add-ClaudeEntries {
    param(
        [Parameter(Mandatory = $true)][object[]]$Targets,
        [switch]$RequireExisting
    )
    $paths = @(
        (Join-Path $env:USERPROFILE '.claude.json'),
        (Join-Path $env:USERPROFILE '.claude\mcp.json'),
        (Join-Path $env:APPDATA 'Claude\claude_desktop_config.json')
    )
    $handled = $false
    foreach ($path in $paths) {
        if (Add-ClaudeEntry -ConfigPath $path -Targets $Targets -RequireExisting:$RequireExisting) {
            $handled = $true
        }
    }
    return $handled
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
$previewed = @()
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
            Assert-PluginArchive -Zip $zip -AddinFile "RvtMcp.R$yearTwo.addin"
        }
        if ($serverSourceDir -and -not (Find-ServerSourceExe $serverSourceDir)) { throw 'Setup server executable is missing.' }
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

$serverCommand = $null
if (-not $Uninstall) {
    $serverCommand = Install-RvtMcpServer -ServerDir $serverSourceDir -InstallRoot $ServerInstallRoot
    if (-not $serverCommand) {
        if (Get-Command rvt-mcp -ErrorAction SilentlyContinue) {
            $serverCommand = 'rvt-mcp'
        }
    }
}

$wireStatus = @()
if (-not $Uninstall -and $Client -ne 'none') {
    $clientWasDefaultAuto = ($Client -eq 'Auto' -and -not $WireClient)
    if (-not $serverCommand) {
        if ($clientWasDefaultAuto) {
            Write-Host "[wire] no setup server found and rvt-mcp is not on PATH - skipping Auto wire"
        } else {
            Write-Warning "[wire] no server command available - install from setup ZIP or install RvtMcp.Server first"
        }
    } else {
        $targets = Get-RvtMcpClientTargets -years $Years -serverCommand $serverCommand
        if ($targets.Count -eq 0) {
            Write-Warning "[wire] no plugin-supported Revit years (2022-2027) detected - skipping wire"
        } else {
            $requireExisting = -not ($Client -eq 'Auto')
            if ($Client -eq 'Auto' -or $Client -eq 'opencode') {
                $ok = Add-OpencodeEntry -ConfigPath (Join-Path $env:USERPROFILE '.config\opencode\opencode.json') -Targets $targets -RequireExisting:$requireExisting
                if ($ok) { $wireStatus += 'opencode' }
            }
            if ($Client -eq 'Auto' -or $Client -eq 'kilo') {
                $ok = Add-KiloEntry -ConfigPath (Join-Path $env:USERPROFILE '.config\kilo\kilo.json') -Targets $targets -RequireExisting:($Client -eq 'kilo')
                if ($ok) { $wireStatus += 'kilo' }
            }
            if ($Client -eq 'Auto' -or $Client -eq 'codex') {
                $ok = Add-CodexEntry -ConfigPath (Join-Path $env:USERPROFILE '.codex\config.toml') -Targets $targets -RequireExisting:$requireExisting
                if ($ok) { $wireStatus += 'codex' }
            }
            if ($Client -eq 'Auto' -or $Client -eq 'claude') {
                $ok = Add-ClaudeEntries -Targets $targets -RequireExisting:$requireExisting
                if ($ok) { $wireStatus += 'claude' }
            }
        }
    }
}

Write-Host ""
Write-Host "=== install.ps1 summary ==="
Write-Host ("Mode   : {0}" -f ($(if ($Uninstall) { 'Uninstall' } else { 'Install' })))
Write-Host ("Source : {0}" -f $SourceDir)
Write-Host ("Years  : {0}" -f ($Years -join ', '))
Write-Host ("Handled: {0}" -f ($(if ($handled.Count -gt 0) { $handled -join ', ' } else { 'none' })))
if ($previewed.Count -gt 0) {
    Write-Host ("Previewed: {0}" -f ($previewed -join ', '))
}
if ($skipped.Count -gt 0) {
    Write-Host ("Skipped: {0}" -f ($skipped -join ', '))
}
if ($serverCommand) {
    Write-Host ("Server : {0}" -f $serverCommand)
}
if ($Client -ne 'none') {
    Write-Host ("Client : {0}" -f $Client)
    Write-Host ("Wired  : {0}" -f ($(if ($wireStatus.Count -gt 0) { $wireStatus -join ', ' } else { 'none' })))
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
# Keep backups until every plugin, server and config write has succeeded.
foreach ($change in $script:installChanges) {
    if ($change.Backup) {
        try { Remove-InstallPath $change.Backup } catch { Write-Warning "Backup retained at $($change.Backup): $_" }
    }
}
$script:installChanges = $null
