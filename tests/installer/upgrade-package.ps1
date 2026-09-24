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
    [Parameter(Mandatory=$true)][string]$ResultPath
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
# Releases up to 0.6.2 installed the server into a versioned folder.
$oldServer=Join-Path $Sandbox "server/$($oldManifest.version)"
New-Item -ItemType Directory -Path (Split-Path -Parent $oldServer) | Out-Null
Copy-Item -LiteralPath (Join-Path $OldPackage 'server') -Destination $oldServer -Recurse
foreach ($year in 2022..2027) {
    $root=Get-AddinsRoot $year
    Expand-Archive -LiteralPath (Join-Path $OldPackage "plugins/RvtMcp.Plugin.R$($year-2000).zip") -DestinationPath "$root/RvtMcp"
    Move-Item -LiteralPath "$root/RvtMcp/RvtMcp.R$($year-2000).addin" -Destination $root
}
$oldServerHash=(Get-FileHash (Join-Path $oldServer 'rvt-mcp.exe')).Hash

$SourceDir=$NewPackage; $pluginSourceDir=Join-Path $NewPackage 'plugins'; $serverSourceDir=Join-Path $NewPackage 'server'
$manifest=Get-Content (Join-Path $NewPackage 'manifest.json') -Raw | ConvertFrom-Json
$setupVersion=[string]$manifest.version
$ServerInstallRoot=Join-Path $Sandbox 'server\current'
$Years=@(2022..2027); $Client='none'; $Uninstall=$false; $WireClient=$null
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
if ((Get-FileHash (Join-Path $oldServer 'rvt-mcp.exe')).Hash -ne $oldServerHash) { throw 'Legacy server version changed' }
if ((Get-FileHash (Join-Path $ServerInstallRoot 'rvt-mcp.exe')).Hash -ne (Get-FileHash (Join-Path $NewPackage 'server/rvt-mcp.exe')).Hash) { throw 'New server mismatch' }
$report=[ordered]@{testedAtUtc=(Get-Date).ToUniversalTime().ToString('o');fromVersion=$oldManifest.version;toVersion=$manifest.version;
    installerSha256=(Get-FileHash $installer).Hash;isolation='Real payloads, real installer control flow and real server smoke check (--help); sandbox paths and simulated closed host; no real deployment.';
    pluginResults=$pluginResults;serverVerified=$true;legacyServerKept=$true;smokeCheck='passed';passed=$true}
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
$report | ConvertTo-Json -Depth 10
} finally {
    $env:USERPROFILE=$savedUserProfile
    $env:APPDATA=$savedAppData
    $env:LOCALAPPDATA=$savedLocalAppData
}
