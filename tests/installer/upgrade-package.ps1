#Requires -Version 5.1
# Upgrade a sandboxed installation from a real release package to a new one:
# real payloads, the real installer control flow and the real server smoke
# check (rvt-mcp.exe --help). Profile env vars (USERPROFILE, APPDATA,
# LOCALAPPDATA) are redirected to a sandbox under $Sandbox\profile for the whole
# run, so a function that ignores its fixture path cannot reach real user data.
[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [Parameter(Mandatory=$true)][string]$OldPackage,
    [Parameter(Mandatory=$true)][string]$NewPackage,
    [Parameter(Mandatory=$true)][string]$Sandbox,
    [Parameter(Mandatory=$true)][string]$ResultPath,
    [ValidateSet('Legacy','Current')][string]$OldLayout = 'Legacy'
)
$ErrorActionPreference='Stop'
$OldPackage=(Resolve-Path -LiteralPath $OldPackage).Path
$NewPackage=(Resolve-Path -LiteralPath $NewPackage).Path
$Sandbox=[IO.Path]::GetFullPath($Sandbox)
if (Test-Path -LiteralPath $Sandbox) { throw 'Use a new sandbox directory.' }
$sandboxUserProfile=Join-Path $Sandbox 'profile\userprofile'
$sandboxAppData=Join-Path $Sandbox 'profile\appdata'
$sandboxLocalAppData=Join-Path $Sandbox 'profile\localappdata'
New-Item -ItemType Directory -Path $sandboxUserProfile,$sandboxAppData,$sandboxLocalAppData -Force | Out-Null
$savedUserProfile=$env:USERPROFILE
$savedAppData=$env:APPDATA
$savedLocalAppData=$env:LOCALAPPDATA
$env:USERPROFILE=$sandboxUserProfile
$env:APPDATA=$sandboxAppData
$env:LOCALAPPDATA=$sandboxLocalAppData
if ($env:USERPROFILE -ne $sandboxUserProfile -or $env:APPDATA -ne $sandboxAppData -or $env:LOCALAPPDATA -ne $sandboxLocalAppData) {
    throw 'Profile env redirection failed'
}
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
# Revit process and the machine-wide add-in folders are never touched.
function Get-AddinsRoot([int]$year) { Join-Path $Sandbox "addins/$year" }
function Get-MachineAddinsRoot([int]$year) { Join-Path $Sandbox "machine/$year" }
function Get-Process { param($Name,$ErrorAction) } # Isolated host has no Revit process.
try {
$oldManifest=Get-Content (Join-Path $OldPackage 'manifest.json') -Raw | ConvertFrom-Json
Assert-SetupManifest -Root $OldPackage -Manifest $oldManifest
# Seed the pre-migration layout, including a retained version and the current
# server used by recent releases. All files are inside the sandbox profile.
$oldRoot=if ($OldLayout -eq 'Legacy') { Join-Path $sandboxLocalAppData 'RvtMcp' } else { Join-Path $sandboxLocalAppData 'Bimwright/rvt-mcp' }
$oldServerFolder=if ($OldLayout -eq 'Legacy') { 'rvt/server' } else { 'server' }
$oldServer=Join-Path $oldRoot "$oldServerFolder/$($oldManifest.version)"
New-Item -ItemType Directory -Path (Split-Path -Parent $oldServer) | Out-Null
Copy-Item -LiteralPath (Join-Path $OldPackage 'server') -Destination $oldServer -Recurse
Copy-Item -LiteralPath (Join-Path $OldPackage 'server') -Destination (Join-Path $oldRoot "$oldServerFolder/current") -Recurse
Set-Content -LiteralPath (Join-Path $oldRoot 'rvtmcp.config.json') '{"toolsets":["query"],"custom":true}'
New-Item -ItemType Directory -Path (Join-Path $oldRoot 'baked/personal') | Out-Null
Set-Content -LiteralPath (Join-Path $oldRoot 'baked/personal/code.cs') 'return "preserved personal source";'
Set-Content -LiteralPath (Join-Path $oldRoot 'baked/registry.json') '{"personal":{"source":"personal/code.cs"}}'
Set-Content -LiteralPath (Join-Path $oldRoot 'bake-audit.jsonl') '{"event":"fixture-preserved"}'
# Installer preservation is byte-level: these are fixture bytes, not a runnable
# SQLite database or a claim that a legacy baked tool executes in Revit.
foreach ($name in 'bake.db', 'bake.db-wal', 'bake.db-shm') {
    [IO.File]::WriteAllBytes((Join-Path $oldRoot $name), [byte[]](0, 1, 128, 255))
}
$dataHashes=@(Get-ChildItem -LiteralPath $oldRoot -File -Recurse | Where-Object { $_.FullName -notlike '*\server\*' } | ForEach-Object {
    @{path=$_.FullName.Substring($oldRoot.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$clientConfig=Join-Path $sandboxUserProfile '.cursor/mcp.json'
New-Item -ItemType Directory -Path (Split-Path -Parent $clientConfig) | Out-Null
@{mcpServers=@{'rvt-mcp'=@{command=(Join-Path $oldServer 'rvt-mcp.exe');args=@('--read-only','--toolsets','query');env=@{BIMWRIGHT_TARGET='2024'}};other=@{command='other.exe'}}} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $clientConfig
$clientHash=(Get-FileHash -LiteralPath $clientConfig).Hash
foreach ($year in 2022..2027) {
    $root=Get-AddinsRoot $year
    Expand-Archive -LiteralPath (Join-Path $OldPackage "plugins/RvtMcp.Plugin.R$($year-2000).zip") -DestinationPath "$root/RvtMcp"
    Move-Item -LiteralPath "$root/RvtMcp/RvtMcp.R$($year-2000).addin" -Destination $root
}
$oldServerHash=(Get-FileHash (Join-Path $oldServer 'rvt-mcp.exe')).Hash

$SourceDir=$NewPackage; $pluginSourceDir=Join-Path $NewPackage 'plugins'; $serverSourceDir=Join-Path $NewPackage 'server'
$manifest=Get-Content (Join-Path $NewPackage 'manifest.json') -Raw | ConvertFrom-Json
$setupVersion=[string]$manifest.version
$newRoot=Join-Path $sandboxLocalAppData 'Bimwright\rvt-mcp'
$ServerInstallRoot=Join-Path $newRoot 'server\current'
$Years=@(2022..2027); $Client='cursor'; $Uninstall=$false; $WireClient=$null
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
$relocatedServer=Join-Path $newRoot "server/$($oldManifest.version)/rvt-mcp.exe"
if ((Get-FileHash -LiteralPath $relocatedServer).Hash -ne $oldServerHash) { throw 'Legacy server version changed' }
if ($OldLayout -eq 'Legacy' -and (Test-Path -LiteralPath $oldRoot)) { throw 'Old product root survived relocation' }
foreach ($file in $dataHashes) {
    if ((Get-FileHash -LiteralPath (Join-Path $newRoot $file.path)).Hash -ne $file.sha256) { throw "Personal data changed: $($file.path)" }
}
if ((Get-FileHash (Join-Path $ServerInstallRoot 'rvt-mcp.exe')).Hash -ne (Get-FileHash (Join-Path $NewPackage 'server/rvt-mcp.exe')).Hash) { throw 'New server mismatch' }
$client=Get-Content -LiteralPath $clientConfig -Raw | ConvertFrom-Json
$entry=$client.mcpServers.'rvt-mcp'
if ([IO.Path]::GetFullPath($entry.command) -ne [IO.Path]::GetFullPath((Join-Path $ServerInstallRoot 'rvt-mcp.exe'))) { throw 'Client was not repointed to current server' }
if (($entry.args -join ',') -ne '--read-only,--toolsets,query' -or $entry.env.BIMWRIGHT_TARGET -ne '2024') { throw 'Client args/env changed' }
if ($client.mcpServers.other.command -ne 'other.exe') { throw 'Unrelated client entry changed' }
if ((Get-FileHash -LiteralPath "$clientConfig.bak").Hash -ne $clientHash) { throw 'Client config backup differs' }
$report=[ordered]@{testedAtUtc=(Get-Date).ToUniversalTime().ToString('o');fromVersion=$oldManifest.version;toVersion=$manifest.version;
    installerSha256=(Get-FileHash $installer).Hash;oldPackageCommit=$oldManifest.commit;candidateCommit=$manifest.commit;candidateDirty=$manifest.dirty;
    isolation='Real payloads, real installer control flow and real server smoke check (--help); sandbox paths, a fixture Cursor config and simulated closed host; no real deployment or client/native acceptance.';
    oldLayout=$OldLayout;pluginResults=$pluginResults;serverVerified=$true;legacyServerKept=$true;dataRootRelocated=($OldLayout -eq 'Legacy');customConfigPreserved=$true;personalDataPreserved=$true;
    preservedFiles=$dataHashes;clientPathRepointed=$true;clientArgsEnvPreserved=$true;clientBackupPreserved=$true;smokeCheck='passed';passed=$true}
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
$report | ConvertTo-Json -Depth 10
} finally {
    $env:USERPROFILE=$savedUserProfile
    $env:APPDATA=$savedAppData
    $env:LOCALAPPDATA=$savedLocalAppData
}
