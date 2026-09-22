#Requires -Version 5.1
# Use real extracted release payloads in an isolated installation directory.
[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [Parameter(Mandatory=$true)][string]$OldPackage,
    [Parameter(Mandatory=$true)][string]$NewPackage,
    [Parameter(Mandatory=$true)][string]$Sandbox,
    [Parameter(Mandatory=$true)][string]$ResultPath
)
$ErrorActionPreference='Stop'
$OldPackage=(Resolve-Path -LiteralPath $OldPackage).Path
$NewPackage=(Resolve-Path -LiteralPath $NewPackage).Path
$Sandbox=[IO.Path]::GetFullPath($Sandbox)
if (Test-Path -LiteralPath $Sandbox) { throw 'Use a new sandbox directory.' }
New-Item -ItemType Directory -Path $Sandbox | Out-Null
$installer=Join-Path $NewPackage 'install.ps1'
$tokens=$null; $parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($installer,[ref]$tokens,[ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
foreach ($fn in $ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)) {
    . ([scriptblock]::Create($fn.Extent.Text))
}
$start=$ast.EndBlock.Statements | Where-Object { $_.Extent.Text.StartsWith('if (-not $Years -or') } | Select-Object -First 1
$main=[scriptblock]::Create((Get-Content $installer -Raw).Substring($start.Extent.StartOffset))

# Only these OS boundaries differ from a real user installation. The real
# Revit process and real MCP client profiles are never touched by this test.
function Get-AddinsRoot([int]$year) { Join-Path $Sandbox "addins/$year" }
function Get-Process { param($Name,$ErrorAction) } # Isolated host has no Revit process.
function Add-ClaudeEntries {
    param($Targets,[switch]$RequireExisting)
    Add-ClaudeEntry -ConfigPath (Join-Path $Sandbox 'claude.json') -Targets $Targets
}
$oldManifest=Get-Content (Join-Path $OldPackage 'manifest.json') -Raw | ConvertFrom-Json
Assert-SetupManifest -Root $OldPackage -Manifest $oldManifest
$oldServer=Join-Path $Sandbox "server/$($oldManifest.version)"
New-Item -ItemType Directory -Path (Split-Path -Parent $oldServer) | Out-Null
Copy-Item -LiteralPath (Join-Path $OldPackage 'server') -Destination $oldServer -Recurse
foreach ($year in 2022..2027) {
    $root=Get-AddinsRoot $year
    Expand-Archive -LiteralPath (Join-Path $OldPackage "plugins/RvtMcp.Plugin.R$($year-2000).zip") -DestinationPath "$root/RvtMcp"
    Move-Item -LiteralPath "$root/RvtMcp/RvtMcp.R$($year-2000).addin" -Destination $root
}
$configPath=Join-Path $Sandbox 'claude.json'
@{mcpServers=@{'rvt-mcp'=@{command=(Join-Path $oldServer 'rvt-mcp.exe');args=@('--read-only','--toolsets','all');env=@{FIXTURE_SETTING='keep'};disabled=$true};other=@{command='keep-other'}}} |
    ConvertTo-Json -Depth 10 | Set-Content $configPath -Encoding UTF8
$oldServerHash=(Get-FileHash (Join-Path $oldServer 'rvt-mcp.exe')).Hash
$oldConfigHash=(Get-FileHash $configPath).Hash

$SourceDir=$NewPackage; $pluginSourceDir=Join-Path $NewPackage 'plugins'; $serverSourceDir=Join-Path $NewPackage 'server'
$manifest=Get-Content (Join-Path $NewPackage 'manifest.json') -Raw | ConvertFrom-Json
$ServerInstallRoot=Join-Path $Sandbox "server/$($manifest.version)"
$Years=@(2022..2027); $Client='claude'; $Uninstall=$false; $WireClient=$null
& $main

$pluginResults=@()
foreach ($year in $Years) {
    $root=Get-AddinsRoot $year
    $reference=Join-Path $Sandbox "reference/$year"
    Expand-Archive -LiteralPath (Join-Path $NewPackage "plugins/RvtMcp.Plugin.R$($year-2000).zip") -DestinationPath $reference
    $files=@(Get-ChildItem $reference -File -Recurse)
    foreach ($file in $files) {
        $relative=$file.FullName.Substring($reference.Length+1)
        $target=if ($relative.EndsWith('.addin')) { Join-Path $root $relative } else { Join-Path "$root/RvtMcp" $relative }
        if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "Installed payload differs: $target" }
    }
    $pluginResults += @{year=$year;filesVerified=$files.Count;passed=$true}
}
$cfg=Read-JsonHashtable $configPath
$entry=$cfg.mcpServers.'rvt-mcp'
if ($entry.command -ne (Join-Path $ServerInstallRoot 'rvt-mcp.exe') -or
    ($entry.args -join ',') -ne '--read-only,--toolsets,all' -or
    $entry.env.FIXTURE_SETTING -ne 'keep' -or -not $entry.disabled -or $cfg.mcpServers.other.command -ne 'keep-other') { throw 'Config preservation failed' }
if ((Get-FileHash "$configPath.rvtmcp.bak").Hash -ne $oldConfigHash) { throw 'Config backup mismatch' }
if ((Get-FileHash (Join-Path $oldServer 'rvt-mcp.exe')).Hash -ne $oldServerHash) { throw 'Older server version changed' }
if ((Get-FileHash (Join-Path $ServerInstallRoot 'rvt-mcp.exe')).Hash -ne (Get-FileHash (Join-Path $NewPackage 'server/rvt-mcp.exe')).Hash) { throw 'New server mismatch' }
$report=[ordered]@{testedAtUtc=(Get-Date).ToUniversalTime().ToString('o');fromVersion=$oldManifest.version;toVersion=$manifest.version;
    installerSha256=(Get-FileHash $installer).Hash;isolation='Real payloads and installer control flow; sandbox paths and simulated closed host; no real deployment.';
    pluginResults=$pluginResults;serverVerified=$true;oldServerPreserved=$true;configPreserved=$true;configBackupVerified=$true;passed=$true}
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
$report | ConvertTo-Json -Depth 10
