#Requires -Version 5.1
# No Pester dependency. Execute production functions against isolated temporary files.
# Profile env vars (USERPROFILE, APPDATA, LOCALAPPDATA) are redirected to a
# sandbox under the test root for the whole run, so a function that ignores its
# fixture path cannot reach real user data.
[CmdletBinding()]
param([string]$ResultPath, [string]$TestRootParent = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path $TestRootParent ('rvt-installer-tests-' + [guid]::NewGuid().ToString('N'))
$sandboxUserProfile = Join-Path $testRoot 'profile\userprofile'
$sandboxAppData = Join-Path $testRoot 'profile\appdata'
$sandboxLocalAppData = Join-Path $testRoot 'profile\localappdata'
New-Item -ItemType Directory -Path $sandboxUserProfile, $sandboxAppData, $sandboxLocalAppData -Force | Out-Null
$savedUserProfile = $env:USERPROFILE
$savedAppData = $env:APPDATA
$savedLocalAppData = $env:LOCALAPPDATA
$env:USERPROFILE = $sandboxUserProfile
$env:APPDATA = $sandboxAppData
$env:LOCALAPPDATA = $sandboxLocalAppData
if ($env:USERPROFILE -ne $sandboxUserProfile -or $env:APPDATA -ne $sandboxAppData -or $env:LOCALAPPDATA -ne $sandboxLocalAppData) {
    throw 'Profile env redirection failed'
}
$installer = Join-Path $PSScriptRoot '../../scripts/install.ps1'
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path $installer), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
foreach ($fn in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    . ([scriptblock]::Create($fn.Extent.Text))
}
$results = New-Object System.Collections.Generic.List[object]
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Test([string]$Name, [scriptblock]$Body) {
    try { & $Body; $results.Add([pscustomobject]@{name=$Name;passed=$true}); Write-Host "PASS $Name" }
    catch { $results.Add([pscustomobject]@{name=$Name;passed=$false;error=$_.Exception.Message}); Write-Host "FAIL $Name : $_" }
}
function Assert-Throws([scriptblock]$Body, [string]$Pattern) {
    $message = $null
    try { & $Body | Out-Null } catch { $message = $_.Exception.Message }
    Assert ($null -ne $message -and $message -match $Pattern) "Expected error matching '$Pattern', got '$message'"
}
$mainStatement = $ast.EndBlock.Statements | Where-Object { $_.Extent.Text.StartsWith('if (-not $Years -or') } | Select-Object -First 1
$mainBody = [scriptblock]::Create((Get-Content -LiteralPath $installer -Raw).Substring($mainStatement.Extent.StartOffset))
function New-AddinXml([int]$Year, [string]$Marker, [string]$Assembly = 'RvtMcp\RvtMcp.Plugin.dll', [string]$Id) {
    if (-not $Id) { $Id = Get-RvtMcpAddinId $Year }
    return @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>$Marker</Name>
    <Assembly>$Assembly</Assembly>
    <FullClassName>RvtMcp.Plugin.App</FullClassName>
    <AddInId>{$Id}</AddInId>
    <VendorId>bimwright</VendorId>
  </AddIn>
</RevitAddIns>
"@
}
function New-SetupFixture([string]$Parent = $testRoot) {
    $root = Join-Path $Parent ([guid]::NewGuid().ToString('N'))
    $source = Join-Path $root 'source'
    New-Item -ItemType Directory -Path "$source/plugins", "$source/server", "$root/server/current", "$root/server/0.6.1", "$root/server/dev", "$root/machine" -Force | Out-Null
    Set-Content -LiteralPath "$source/server/rvt-mcp.exe" 'new-server'
    Set-Content -LiteralPath "$root/server/current/rvt-mcp.exe" 'old-server-current'
    Set-Content -LiteralPath "$root/server/0.6.1/rvt-mcp.exe" 'old-server-061'
    Set-Content -LiteralPath "$root/server/dev/rvt-mcp.exe" 'dev-build'
    foreach ($year in @(2026,2027)) {
        $yy = $year - 2000
        $payload = Join-Path $root "payload-$year"
        $addins = Join-Path $root "addins/$year"
        New-Item -ItemType Directory -Path $payload, "$addins/RvtMcp" -Force | Out-Null
        Set-Content -LiteralPath "$payload/RvtMcp.Plugin.dll" "new-plugin-$year"
        Set-Content -LiteralPath "$payload/RvtMcp.R$yy.addin" (New-AddinXml $year "new-addin-$year")
        Set-Content -LiteralPath "$addins/RvtMcp/RvtMcp.Plugin.dll" "old-plugin-$year"
        Set-Content -LiteralPath "$addins/RvtMcp/old-only.dll" 'old dependency'
        Set-Content -LiteralPath "$addins/RvtMcp.R$yy.addin" (New-AddinXml $year "old-addin-$year")
        Compress-Archive -Path (Join-Path ([Management.Automation.WildcardPattern]::Escape($payload)) '*') -DestinationPath "$source/plugins/RvtMcp.Plugin.R$yy.zip"
    }
    $manifest = [pscustomobject]@{files=@(Get-ChildItem -LiteralPath $source -File -Recurse | ForEach-Object {
        [pscustomobject]@{path=$_.FullName.Substring($source.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })}
    return [pscustomobject]@{Root=$root;Source=$source;Running=$false;SmokeFails=$false;Manifest=$manifest}
}
function Invoke-FixtureSetup {
    [CmdletBinding(SupportsShouldProcess=$true)]
    param($Fixture, [string]$Client='none', [switch]$PruneOldServers, [switch]$Uninstall, [int[]]$Years=@(2026,2027))
    # Only OS boundaries are redirected. Main control flow, ZIP extraction,
    # directory replacement and rollback are production code.
    function Get-AddinsRoot([int]$year) { Join-Path $Fixture.Root "addins/$year" }
    function Get-MachineAddinsRoot([int]$year) { Join-Path $Fixture.Root "machine/$year" }
    function Get-Process { param($Name, $ErrorAction) if ($Fixture.Running) { [pscustomobject]@{Name='Revit';Id=1234} } }
    function Test-ServerExecutable { param([string]$Path, [int]$TimeoutSeconds) if ($Fixture.SmokeFails) { throw 'Server executable could not start (stub). Antivirus or policy may have blocked it.' } }
    $SourceDir=$Fixture.Source; $pluginSourceDir=Join-Path $SourceDir 'plugins'; $serverSourceDir=Join-Path $SourceDir 'server'
    $ServerInstallRoot=Join-Path $Fixture.Root 'server\current'; $manifest=$Fixture.Manifest; $WireClient=$null; $setupVersion='0.6.3'
    & $mainBody
}
function Assert-OldInstall($Fixture) {
    foreach ($year in @(2026,2027)) {
        Assert ((Get-Content -LiteralPath "$($Fixture.Root)/addins/$year/RvtMcp/RvtMcp.Plugin.dll" -Raw).Trim() -eq "old-plugin-$year") "Old $year plugin not restored"
        Assert ((Get-Content -LiteralPath "$($Fixture.Root)/addins/$year/RvtMcp.R$($year-2000).addin" -Raw).Contains("old-addin-$year")) "Old $year manifest not restored"
        Assert (Test-Path -LiteralPath "$($Fixture.Root)/addins/$year/RvtMcp/old-only.dll") 'Old dependency not restored'
    }
    Assert ((Get-Content -LiteralPath "$($Fixture.Root)/server/0.6.1/rvt-mcp.exe" -Raw).Trim() -eq 'old-server-061') 'Legacy server version changed'
    Assert ((Get-Content -LiteralPath "$($Fixture.Root)/server/current/rvt-mcp.exe" -Raw).Trim() -eq 'old-server-current') 'Previous current server not restored'
}
try {
    Test 'Upgrade replaces add-ins and current server, keeps legacy versions, reports next steps' {
        $fixture = New-SetupFixture
        $output = Invoke-FixtureSetup $fixture 3>&1 6>&1 | Out-String -Width 4096
        foreach ($year in @(2026,2027)) {
            Assert ((Get-Content -LiteralPath "$($fixture.Root)/addins/$year/RvtMcp/RvtMcp.Plugin.dll" -Raw).Trim() -eq "new-plugin-$year") 'Plugin not upgraded'
            Assert (-not (Test-Path -LiteralPath "$($fixture.Root)/addins/$year/RvtMcp/old-only.dll")) 'Stale dependency survived upgrade'
            Assert ((Get-Content -LiteralPath "$($fixture.Root)/addins/$year/RvtMcp.R$($year-2000).addin" -Raw).Contains("new-addin-$year")) 'Manifest not upgraded'
        }
        Assert ((Get-Content -LiteralPath "$($fixture.Root)/server/current/rvt-mcp.exe" -Raw).Trim() -eq 'new-server') 'Server not upgraded'
        Assert ((Get-Content -LiteralPath "$($fixture.Root)/server/0.6.1/rvt-mcp.exe" -Raw).Trim() -eq 'old-server-061') 'Legacy version removed without -PruneOldServers'
        Assert ((Get-Content -LiteralPath "$($fixture.Root)/server/dev/rvt-mcp.exe" -Raw).Trim() -eq 'dev-build') 'dev copy touched'
        Assert ($output -match 'Server\s*:.*current\\rvt-mcp\.exe') 'Server path missing from summary'
        Assert ($output -match 'Legacy\s*:\s*0\.6\.1') 'Legacy version not reported'
        Assert ($output -match 'PruneOldServers') 'Prune guidance missing'
        Assert ($output -match 'Next\s*:.*AGENTS\.md') 'Next step missing'
        Assert ($output -match 'Verified:\s*R26, R27') 'Verification not reported'
        Assert (@(Get-ChildItem -LiteralPath $fixture.Root -Recurse -Filter '*.rvtmcp-rollback-*').Count -eq 0) 'Transaction backups leaked after success'
    }
    Test 'Install works when paths contain spaces' {
        $parent = Join-Path $testRoot 'with space'
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        $fixture = New-SetupFixture $parent
        Invoke-FixtureSetup $fixture | Out-Null
        Assert ((Get-Content -LiteralPath "$($fixture.Root)/server/current/rvt-mcp.exe" -Raw).Trim() -eq 'new-server') 'Server not installed under a path with spaces'
        Assert ((Get-Content -LiteralPath "$($fixture.Root)/addins/2027/RvtMcp/RvtMcp.Plugin.dll" -Raw).Trim() -eq 'new-plugin-2027') 'Add-in not installed under a path with spaces'
    }
    Test 'PruneOldServers removes older versions and keeps dev' {
        $fixture = New-SetupFixture
        Invoke-FixtureSetup $fixture -PruneOldServers | Out-Null
        Assert (-not (Test-Path -LiteralPath "$($fixture.Root)/server/0.6.1")) 'Previous version not pruned'
        Assert ((Get-Content -LiteralPath "$($fixture.Root)/server/current/rvt-mcp.exe" -Raw).Trim() -eq 'new-server') 'Server not upgraded'
        Assert ((Get-Content -LiteralPath "$($fixture.Root)/server/dev/rvt-mcp.exe" -Raw).Trim() -eq 'dev-build') 'Non-version server dir was cleaned'
        Assert (@(Get-ChildItem -LiteralPath $fixture.Root -Recurse -Filter '*.rvtmcp-rollback-*').Count -eq 0) 'Transaction backups leaked after success'
    }
    Test 'Server copy still running is kept whole and swept once free' {
        $fixture = New-SetupFixture
        Copy-Item -LiteralPath "$env:WINDIR\System32\PING.EXE" -Destination "$($fixture.Root)/server/current/rvt-mcp.exe" -Force
        $proc = Start-Process -FilePath "$($fixture.Root)\server\current\rvt-mcp.exe" -ArgumentList '-n','30','127.0.0.1' -WindowStyle Hidden -PassThru
        try {
            Start-Sleep -Milliseconds 500
            $output = Invoke-FixtureSetup $fixture 3>&1 6>&1 | Out-String -Width 4096
            Assert ((Get-Content -LiteralPath "$($fixture.Root)/server/current/rvt-mcp.exe" -Raw).Trim() -eq 'new-server') 'Server not upgraded'
            $kept = @(Get-ChildItem -LiteralPath "$($fixture.Root)/server" -Directory | Where-Object Name -like 'current.rvtmcp-rollback-*')
            Assert ($kept.Count -eq 1 -and (Test-Path -LiteralPath (Join-Path $kept[0].FullName 'rvt-mcp.exe'))) 'Running copy was not kept whole'
            Assert ($output -match 'In use\s*:') 'In-use copy not reported'
        } finally { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; $null = $proc.WaitForExit(5000) }
        Start-Sleep -Milliseconds 300
        Invoke-FixtureSetup $fixture | Out-Null
        Assert (@(Get-ChildItem -LiteralPath "$($fixture.Root)/server" -Directory | Where-Object Name -like 'current.rvtmcp-rollback-*').Count -eq 0) 'Leftover not swept after the process exited'
    }
    Test 'Leftover sweep only touches exact rollback names' {
        $fixture = New-SetupFixture
        $s = "$($fixture.Root)/server"
        $leftover = "$s/current.rvtmcp-rollback-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $leftover, "$s/current.backup", "$s/0.6.1.rvtmcp-rollback-xyz" -Force | Out-Null
        Set-Content -LiteralPath "$leftover/rvt-mcp.exe" 'old'
        Set-Content -LiteralPath "$s/current.backup/rvt-mcp.exe" 'keep'
        Set-Content -LiteralPath "$s/0.6.1.rvtmcp-rollback-xyz/rvt-mcp.exe" 'keep'
        Invoke-FixtureSetup $fixture | Out-Null
        Assert (-not (Test-Path -LiteralPath $leftover)) 'Exact leftover was not swept'
        Assert ((Test-Path -LiteralPath "$s/current.backup/rvt-mcp.exe") -and (Test-Path -LiteralPath "$s/0.6.1.rvtmcp-rollback-xyz/rvt-mcp.exe")) 'Sweep touched a non-matching name'
    }
    Test 'Deprecated -Client warns and still installs; none and default do not warn' {
        $fixture = New-SetupFixture
        $output = Invoke-FixtureSetup $fixture -Client codex 3>&1 6>&1 | Out-String -Width 4096
        Assert ($output -match 'no longer edits MCP client configs') 'Deprecation warning missing'
        Assert ((Get-Content -LiteralPath "$($fixture.Root)/server/current/rvt-mcp.exe" -Raw).Trim() -eq 'new-server') 'Install did not proceed'
        foreach ($clientArgs in @(@{Client='none'}, @{})) {
            $f = New-SetupFixture
            $out = Invoke-FixtureSetup $f @clientArgs 3>&1 6>&1 | Out-String -Width 4096
            Assert (-not ($out -match 'no longer edits')) 'Warned without an explicit client'
        }
    }
    Test 'Revit running blocks the upgrade before any replacement' {
        $fixture = New-SetupFixture; $fixture.Running=$true
        Assert-Throws { Invoke-FixtureSetup $fixture } 'Revit running'
        Assert-OldInstall $fixture
    }
    Test 'WhatIf preserves installation, stages nothing and runs no smoke check' {
        $fixture = New-SetupFixture; $fixture.SmokeFails = $true
        Invoke-FixtureSetup $fixture -WhatIf -PruneOldServers 3>&1 6>&1 | Out-Null
        Assert-OldInstall $fixture
        Assert ($null -eq $script:installStage) 'WhatIf staged payload'
    }
    Test 'Smoke-check failure restores add-ins, previous server, pruned versions and legacy manifests' {
        $fixture = New-SetupFixture; $fixture.SmokeFails = $true
        $a = "$($fixture.Root)/addins/2026"
        New-Item -ItemType Directory -Path "$a/Bimwright" -Force | Out-Null
        Set-Content -LiteralPath "$a/Bimwright/Bimwright.Rvt.Plugin.dll" 'legacy'
        Set-Content -LiteralPath "$a/Bimwright.R26.addin" (New-AddinXml 2026 'legacy' 'Bimwright\Bimwright.Rvt.Plugin.dll')
        Assert-Throws { Invoke-FixtureSetup $fixture -PruneOldServers } 'could not start'
        Assert-OldInstall $fixture
        Assert ((Test-Path -LiteralPath "$a/Bimwright.R26.addin") -and (Test-Path -LiteralPath "$a/Bimwright/Bimwright.Rvt.Plugin.dll")) 'Legacy add-in not restored'
        Assert (@(Get-ChildItem -LiteralPath $fixture.Root -Recurse -Filter '*.rvtmcp-rollback-*').Count -eq 0) 'Rollback left backups behind'
    }
    Test 'Machine-wide copy with our AddInId blocks the install before any change' {
        $fixture = New-SetupFixture
        New-Item -ItemType Directory -Path "$($fixture.Root)/machine/2027" -Force | Out-Null
        Set-Content -LiteralPath "$($fixture.Root)/machine/2027/RvtMcp.R27.addin" (New-AddinXml 2027 'machine-wide')
        Assert-Throws { Invoke-FixtureSetup $fixture } 'machine-wide'
        Assert-OldInstall $fixture
    }
    Test 'Duplicate and legacy add-ins with our AddInId are removed; others untouched' {
        $fixture = New-SetupFixture
        $a = "$($fixture.Root)/addins/2026"
        New-Item -ItemType Directory -Path "$a/Bimwright", "$a/Other", "$a/Shared", "$($fixture.Root)/external" -Force | Out-Null
        Set-Content -LiteralPath "$a/Bimwright/Bimwright.Rvt.Plugin.dll" 'legacy'
        Set-Content -LiteralPath "$a/Bimwright.R26.addin" (New-AddinXml 2026 'legacy' 'Bimwright\Bimwright.Rvt.Plugin.dll')
        Set-Content -LiteralPath "$a/Shared/Shared.dll" 'shared'
        Set-Content -LiteralPath "$a/Stray.R26.addin" (New-AddinXml 2026 'stray' 'Shared\Shared.dll')
        Set-Content -LiteralPath "$a/User.addin" (New-AddinXml 2026 'user' 'Shared\Shared.dll' '99999999-2222-3333-4444-555555555555')
        Set-Content -LiteralPath "$($fixture.Root)/external/RvtMcp.Plugin.dll" 'dev build'
        Set-Content -LiteralPath "$a/Dev.R26.addin" (New-AddinXml 2026 'dev' (Join-Path $fixture.Root 'external\RvtMcp.Plugin.dll'))
        Set-Content -LiteralPath "$a/Other/Other.dll" 'other'
        Set-Content -LiteralPath "$a/Other.addin" (New-AddinXml 2026 'other' 'Other\Other.dll' '11111111-2222-3333-4444-555555555555')
        Set-Content -LiteralPath "$a/Broken.addin" 'not xml'
        $output = Invoke-FixtureSetup $fixture 3>&1 6>&1 | Out-String -Width 4096
        Assert (-not (Test-Path -LiteralPath "$a/Bimwright.R26.addin") -and -not (Test-Path -LiteralPath "$a/Bimwright")) 'Legacy add-in survived'
        Assert (-not (Test-Path -LiteralPath "$a/Dev.R26.addin") -and -not (Test-Path -LiteralPath "$a/Stray.R26.addin")) 'Duplicate manifest survived'
        Assert (Test-Path -LiteralPath "$($fixture.Root)/external/RvtMcp.Plugin.dll") 'External assembly folder was touched'
        Assert (Test-Path -LiteralPath "$a/Shared/Shared.dll") 'Folder still used by another manifest was removed'
        foreach ($keep in 'Other.addin', 'Other/Other.dll', 'Broken.addin', 'User.addin') { Assert (Test-Path -LiteralPath "$a/$keep") "Unrelated $keep touched" }
        Assert ($output.Contains('2026\Bimwright.R26.addin') -and $output.Contains('2026\Dev.R26.addin')) 'Removed duplicates not reported'
        Assert (@(Get-ChildItem -LiteralPath $fixture.Root -Recurse -Filter '*.rvtmcp-rollback-*').Count -eq 0) 'Backups leaked'
    }
    Test 'WhatIf previews duplicate removal without removing' {
        $fixture = New-SetupFixture
        Set-Content -LiteralPath "$($fixture.Root)/addins/2026/Bimwright.R26.addin" (New-AddinXml 2026 'legacy' 'Bimwright\Bimwright.Rvt.Plugin.dll')
        $output = Invoke-FixtureSetup $fixture -WhatIf 3>&1 6>&1 | Out-String -Width 4096
        Assert (Test-Path -LiteralPath "$($fixture.Root)/addins/2026/Bimwright.R26.addin") 'WhatIf removed a duplicate'
        Assert ($output -match 'Bimwright\.R26\.addin.*preview') 'WhatIf did not preview the removal'
    }
    Test 'Installed server files are unblocked' {
        $fixture = New-SetupFixture
        Set-Content -LiteralPath "$($fixture.Source)/server/rvt-mcp.exe" -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
        Invoke-FixtureSetup $fixture | Out-Null
        $streams = @(Get-Item -LiteralPath "$($fixture.Root)/server/current/rvt-mcp.exe" -Stream * | Where-Object Stream -eq 'Zone.Identifier')
        Assert ($streams.Count -eq 0) 'Server exe still carries Mark-of-the-Web'
    }
    Test 'Test-ServerExecutable accepts exit 0 and rejects non-zero exit and missing files' {
        Test-ServerExecutable -Path "$env:WINDIR\System32\cmd.exe" -Arguments '/c exit 0'
        Assert-Throws { Test-ServerExecutable -Path "$env:WINDIR\System32\cmd.exe" -Arguments '/c exit 3' } 'exit code 3'
        Assert-Throws { Test-ServerExecutable -Path (Join-Path $testRoot 'missing.exe') } 'could not start'
    }
    foreach ($kind in @('missing','corrupt','missing-manifest')) {
        Test "$kind ZIP blocks all replacements" {
            $fixture = New-SetupFixture
            $fixture.Manifest = $null # Exercise ZIP validation in the legacy no-manifest layout.
            $zip = Join-Path $fixture.Source 'plugins/RvtMcp.Plugin.R27.zip'
            if ($kind -eq 'missing') { Remove-Item -LiteralPath $zip }
            elseif ($kind -eq 'corrupt') { Set-Content -LiteralPath $zip 'not a ZIP' }
            else { Compress-Archive -LiteralPath "$($fixture.Root)/payload-2027/RvtMcp.Plugin.dll" -DestinationPath $zip -Force }
            Assert-Throws { Invoke-FixtureSetup $fixture } '.'
            Assert-OldInstall $fixture
        }
    }
    Test 'Plugin ZIP with an unexpected AddInId or assembly fails before any change' {
        foreach ($case in 'id','assembly') {
            $fixture = New-SetupFixture
            $fixture.Manifest = $null
            $payload = "$($fixture.Root)/payload-2027"
            $xml = if ($case -eq 'id') { New-AddinXml 2027 'bad' 'RvtMcp\RvtMcp.Plugin.dll' '11111111-2222-3333-4444-555555555555' } else { New-AddinXml 2027 'bad' 'Other\RvtMcp.Plugin.dll' }
            Set-Content -LiteralPath "$payload/RvtMcp.R27.addin" $xml
            Compress-Archive -Path "$payload/*" -DestinationPath "$($fixture.Source)/plugins/RvtMcp.Plugin.R27.zip" -Force
            Assert-Throws { Invoke-FixtureSetup $fixture } 'unexpected add-in manifest'
            Assert-OldInstall $fixture
        }
    }
    Test 'Verification rejects a modified file or a second manifest with our AddInId' {
        $root = Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
        $payload = "$root/payload"; $addins = "$root/addins"
        New-Item -ItemType Directory -Path $payload, "$addins/RvtMcp", "$root/machine" -Force | Out-Null
        Set-Content -LiteralPath "$payload/RvtMcp.Plugin.dll" 'plugin'
        Set-Content -LiteralPath "$payload/RvtMcp.R27.addin" (New-AddinXml 2027 'x')
        Compress-Archive -Path "$payload/*" -DestinationPath "$root/p.zip"
        Copy-Item -LiteralPath "$payload/RvtMcp.Plugin.dll" -Destination "$addins/RvtMcp/"
        Copy-Item -LiteralPath "$payload/RvtMcp.R27.addin" -Destination "$addins/"
        function Get-MachineAddinsRoot([int]$year) { Join-Path $root 'machine' }
        $verify = { Assert-InstalledPlugin -Year 2027 -Zip "$root/p.zip" -AddinPath "$addins/RvtMcp.R27.addin" -PluginDir "$addins/RvtMcp" }
        & $verify
        Set-Content -LiteralPath "$addins/RvtMcp/RvtMcp.Plugin.dll" 'tampered'
        Assert-Throws $verify 'differs from the package'
        Copy-Item -LiteralPath "$payload/RvtMcp.Plugin.dll" -Destination "$addins/RvtMcp/" -Force
        Set-Content -LiteralPath "$root/machine/Copy.addin" (New-AddinXml 2027 'dup')
        Assert-Throws $verify 'exactly one manifest'
    }
    Test 'Revit years are detected only when Revit.exe exists' {
        $keyRoot = "HKCU:\Software\RvtMcpInstallerTest-$([guid]::NewGuid().ToString('N'))"
        $pf = Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
        try {
            $loc2024 = Join-Path $pf 'custom\Revit 2024'
            New-Item -ItemType Directory -Path $loc2024, (Join-Path $pf 'Autodesk\Revit 2027') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $loc2024 'Revit.exe') 'exe'
            Set-Content -LiteralPath (Join-Path $pf 'Autodesk\Revit 2027\Revit.exe') 'exe'
            New-Item -Path "$keyRoot\2024\REVIT-05" -Force | Out-Null
            New-ItemProperty -Path "$keyRoot\2024\REVIT-05" -Name InstallationLocation -Value ($loc2024 + '\') | Out-Null
            New-Item -Path "$keyRoot\2023\ObjectDBX" -Force | Out-Null
            New-Item -Path "$keyRoot\2025\REVIT-05" -Force | Out-Null
            New-ItemProperty -Path "$keyRoot\2025\REVIT-05" -Name InstallationLocation -Value (Join-Path $pf 'missing\') | Out-Null
            $years = @(Get-InstalledRevitYears -RegistryRoot $keyRoot -ProgramFilesRoot $pf)
            Assert (($years -join ',') -eq '2024,2027') "Detected: $($years -join ',')"
        } finally { Remove-Item -LiteralPath $keyRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Test 'Uninstall covers every year without detection and removes legacy duplicates' {
        $fixture = New-SetupFixture
        $a23 = "$($fixture.Root)/addins/2023"
        New-Item -ItemType Directory -Path "$a23/RvtMcp", "$a23/Bimwright", "$a23/Other" -Force | Out-Null
        Set-Content -LiteralPath "$a23/RvtMcp/RvtMcp.Plugin.dll" 'x'
        Set-Content -LiteralPath "$a23/RvtMcp.R23.addin" (New-AddinXml 2023 'ours')
        Set-Content -LiteralPath "$a23/Bimwright/Bimwright.Rvt.Plugin.dll" 'legacy'
        Set-Content -LiteralPath "$a23/Bimwright.R23.addin" (New-AddinXml 2023 'legacy' 'Bimwright\Bimwright.Rvt.Plugin.dll')
        Set-Content -LiteralPath "$a23/Other/Other.dll" 'o'
        Set-Content -LiteralPath "$a23/Other.addin" (New-AddinXml 2023 'other' 'Other\Other.dll' '11111111-2222-3333-4444-555555555555')
        Invoke-FixtureSetup $fixture -Uninstall -Years @() 3>&1 6>&1 | Out-Null
        foreach ($year in 2023, 2026, 2027) {
            $yy = $year - 2000
            Assert (-not (Test-Path -LiteralPath "$($fixture.Root)/addins/$year/RvtMcp") -and -not (Test-Path -LiteralPath "$($fixture.Root)/addins/$year/RvtMcp.R$yy.addin")) "RvtMcp $year not removed"
        }
        Assert (-not (Test-Path -LiteralPath "$a23/Bimwright.R23.addin") -and -not (Test-Path -LiteralPath "$a23/Bimwright")) 'Legacy add-in not removed'
        Assert ((Test-Path -LiteralPath "$a23/Other.addin") -and (Test-Path -LiteralPath "$a23/Other/Other.dll")) 'Unrelated add-in touched'
        Assert ((Get-Content -LiteralPath "$($fixture.Root)/server/current/rvt-mcp.exe" -Raw).Trim() -eq 'old-server-current') 'Uninstall touched the server'
    }
    Test 'Locked later addin restores the already-upgraded earlier year' {
        $fixture = New-SetupFixture
        $locked = [IO.File]::Open("$($fixture.Root)/addins/2027/RvtMcp.R27.addin", 'Open', 'Read', 'None')
        try { Assert-Throws { Invoke-FixtureSetup $fixture } 'used by another process|being used|access.*denied' }
        finally { $locked.Dispose() }
        Assert-OldInstall $fixture
    }
    Test 'Manifest checksum failure is detected before installation' {
        $fixture = New-SetupFixture
        $manifest = [pscustomobject]@{files=@([pscustomobject]@{path='server/rvt-mcp.exe';sha256=('0' * 64)})}
        Assert-Throws { Assert-SetupManifest -Root $fixture.Source -Manifest $manifest } 'checksum failed'
        Assert-OldInstall $fixture
    }
    Test 'Rollback removes a newly created server' {
        $fixture = New-SetupFixture
        $newServer = Join-Path $fixture.Root 'server/new-version'
        $script:installChanges = New-Object System.Collections.Generic.List[object]
        Set-InstallPath -Source (Join-Path $fixture.Source 'server') -Destination $newServer
        Undo-InstallChanges
        Assert (-not (Test-Path -LiteralPath $newServer)) 'New server survived rollback'
        $script:installChanges=$null
    }
    Test 'Incomplete rollback reports retained backup and continues restoring other paths' {
        $fixture = New-SetupFixture
        $dir = Join-Path $fixture.Root 'rollback'
        New-Item -ItemType Directory -Path "$dir/new" -Force | Out-Null
        $path1 = Join-Path $dir 'one.txt'; $path2 = Join-Path $dir 'two.txt'
        Set-Content -LiteralPath $path1 'old-one'; Set-Content -LiteralPath $path2 'old-two'
        Set-Content -LiteralPath "$dir/new/one.txt" 'new-one'; Set-Content -LiteralPath "$dir/new/two.txt" 'new-two'
        $script:installChanges = New-Object System.Collections.Generic.List[object]
        Set-InstallPath -Source "$dir/new/one.txt" -Destination $path1
        Set-InstallPath -Source "$dir/new/two.txt" -Destination $path2
        $locked = [IO.File]::Open($path2, 'Open', 'Read', 'None')
        try { Assert-Throws { Undo-InstallChanges } 'Rollback incomplete' }
        finally { $locked.Dispose() }
        Assert ((Get-Content -LiteralPath $path1 -Raw).Trim() -eq 'old-one') 'Rollback stopped before earlier path'
        Assert (Test-Path -LiteralPath $script:installChanges[1].Backup) 'Failed rollback deleted its recovery backup'
        $script:installChanges=$null
    }
} finally {
    $env:USERPROFILE = $savedUserProfile
    $env:APPDATA = $savedAppData
    $env:LOCALAPPDATA = $savedLocalAppData
    $report = [ordered]@{powershell=$PSVersionTable.PSVersion.ToString();installerSha256=(Get-FileHash $installer -Algorithm SHA256).Hash;results=@($results.ToArray())}
    if ($ResultPath) { $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ResultPath -Encoding UTF8 }
    # Only remove the uniquely created test directory under the resolved temp root.
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath($TestRootParent).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
if (@($results | Where-Object { -not $_.passed }).Count) { throw 'Installer regression tests failed' }
