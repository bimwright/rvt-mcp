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
    param($Fixture, [string[]]$Client=@(), [string]$WireClient, [switch]$PruneOldServers, [switch]$Uninstall, [int[]]$Years=@(2026,2027), [string]$ServerInstallRoot)
    # Only OS boundaries are redirected. Main control flow, ZIP extraction,
    # directory replacement and rollback are production code.
    function Get-AddinsRoot([int]$year) { Join-Path $Fixture.Root "addins/$year" }
    function Get-MachineAddinsRoot([int]$year) { Join-Path $Fixture.Root "machine/$year" }
    function Get-Process { param($Name, $ErrorAction)
        if ($Name -eq 'claude' -and $Fixture.PSObject.Properties['ClaudeRunning'] -and $Fixture.ClaudeRunning) {
            return [pscustomobject]@{Name='claude';Id=99;Path='C:\Program Files\WindowsApps\Claude_9.9_x64__testpfn\app\claude.exe'}
        }
        if ($Fixture.Running) { [pscustomobject]@{Name='Revit';Id=1234} }
    }
    function Test-ServerExecutable { param([string]$Path, [int]$TimeoutSeconds) if ($Fixture.SmokeFails) { throw 'Server executable could not start (stub). Antivirus or policy may have blocked it.' } }
    # Client CLIs never touch the real PATH here: detected only when the
    # fixture opts in with Add-Member FakeCli $true, then calls are recorded.
    $Fixture | Add-Member -NotePropertyName CliCalls -NotePropertyValue (New-Object 'System.Collections.Generic.List[string]') -Force
    function Get-Command { param($Name, $ErrorAction)
        if ($Name -in @('claude', 'codex', 'grok')) {
            if ($Fixture.PSObject.Properties['FakeCli'] -and $Fixture.FakeCli) { return [pscustomobject]@{Name=$Name;Source="C:\fake\$Name.exe"} }
            return
        }
        Microsoft.PowerShell.Core\Get-Command @PSBoundParameters
    }
    if ($Fixture.PSObject.Properties['FakeCli'] -and $Fixture.FakeCli) {
        # Optional CliOutput @{ 'mcp add' = '...' } replays the real CLI's text.
        function claude {
            $Fixture.CliCalls.Add('claude ' + ($args -join ' '))
            $key = "$($args[0]) $($args[1])"
            if ($Fixture.PSObject.Properties['CliOutput'] -and $Fixture.CliOutput.ContainsKey($key)) { $Fixture.CliOutput[$key] }
            # Optional CliStderr @{ 'mcp remove' = '...' } writes to the error stream like the real CLI's stderr.
            if ($Fixture.PSObject.Properties['CliStderr'] -and $Fixture.CliStderr.ContainsKey($key)) { Write-Error $Fixture.CliStderr[$key] }
        }
        function codex { $Fixture.CliCalls.Add('codex ' + ($args -join ' ')) }
        function grok { $Fixture.CliCalls.Add('grok ' + ($args -join ' ')) }
    }
    $SourceDir=$Fixture.Source; $pluginSourceDir=Join-Path $SourceDir 'plugins'; $serverSourceDir=Join-Path $SourceDir 'server'
    if (-not $ServerInstallRoot) { $ServerInstallRoot=Join-Path $Fixture.Root 'server\current' }
    $manifest=$Fixture.Manifest; $setupVersion='0.6.3'
    & $mainBody
}
function New-LegacyRootFixture {
    # The pre-rename %LOCALAPPDATA%\RvtMcp\ tree: config, ToolBaker data,
    # locales, and server copies nested as rvt\server\<name>.
    $old = Join-Path $sandboxLocalAppData 'RvtMcp'
    New-Item -ItemType Directory -Path "$old/baked/x", "$old/locales", "$old/rvt/server/current", "$old/rvt/server/0.6.2" -Force | Out-Null
    Set-Content -LiteralPath "$old/rvtmcp.config.json" '{"custom":true}'
    Set-Content -LiteralPath "$old/baked/x/tool.json" '{}'
    Set-Content -LiteralPath "$old/locales/strings.vi.json" '{"k":"v"}'
    Set-Content -LiteralPath "$old/rvt/server/current/rvt-mcp.exe" 'old-server-current'
    Set-Content -LiteralPath "$old/rvt/server/0.6.2/rvt-mcp.exe" 'old-server-062'
    return $old
}
function Clear-ProductRoots {
    # The sandbox profile is shared for the whole run; relocation tests must
    # not inherit each other's roots.
    Remove-Item -LiteralPath (Join-Path $sandboxLocalAppData 'RvtMcp') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $sandboxLocalAppData 'Bimwright') -Recurse -Force -ErrorAction SilentlyContinue
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
    Test 'Deprecated -WireClient warns and merges into -Client; none and default do not warn' {
        $fixture = New-SetupFixture
        $fixture | Add-Member -NotePropertyName FakeCli -NotePropertyValue $true
        $output = Invoke-FixtureSetup $fixture -WireClient claude 3>&1 6>&1 | Out-String -Width 4096
        Assert ($output -match 'WireClient is deprecated') 'Deprecation warning missing'
        Assert (@($fixture.CliCalls | Where-Object { $_ -like 'claude mcp add *' }).Count -eq 1) 'WireClient did not merge into -Client'
        foreach ($clientArgs in @(@{Client='none'}, @{})) {
            $f = New-SetupFixture
            $out = Invoke-FixtureSetup $f @clientArgs 3>&1 6>&1 | Out-String -Width 4096
            Assert (-not ($out -match 'deprecated')) 'Warned without an explicit client'
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
    Test 'Install seeds toolsets=all config and keeps an explicit toolsets choice' {
        $cfgPath = Join-Path $sandboxLocalAppData 'Bimwright\rvt-mcp\rvtmcp.config.json'
        Remove-Item -LiteralPath $cfgPath -Force -ErrorAction SilentlyContinue
        $fixture = New-SetupFixture
        $output = Invoke-FixtureSetup $fixture 3>&1 6>&1 | Out-String -Width 4096
        Assert (Test-Path -LiteralPath $cfgPath) 'rvtmcp.config.json not seeded'
        $cfg = Get-Content -LiteralPath $cfgPath -Raw | ConvertFrom-Json
        Assert (@($cfg.toolsets) -contains 'all') 'toolsets=all not seeded'
        Assert ($output -match 'Config\s*:\s*seeded toolsets=all') 'Config seed not reported'
        # An explicit toolsets choice survives the next install.
        Set-Content -LiteralPath $cfgPath '{"toolsets":["query","meta"],"enableToast":false}'
        $output = Invoke-FixtureSetup $fixture 3>&1 6>&1 | Out-String -Width 4096
        $cfg = Get-Content -LiteralPath $cfgPath -Raw | ConvertFrom-Json
        Assert (($cfg.toolsets -join ',') -eq 'query,meta') 'User toolsets clobbered'
        Assert ($cfg.enableToast -eq $false) 'Other config keys clobbered'
        Assert ($output -match 'Config\s*:\s*kept existing') 'Kept setting not reported'
    }
    Test 'Install merges toolsets=all into a config that lacks the key' {
        $cfgPath = Join-Path $sandboxLocalAppData 'Bimwright\rvt-mcp\rvtmcp.config.json'
        New-Item -ItemType Directory -Path (Split-Path -Parent $cfgPath) -Force | Out-Null
        Set-Content -LiteralPath $cfgPath '{"enableToast":false}'
        $fixture = New-SetupFixture
        Invoke-FixtureSetup $fixture | Out-Null
        $cfg = Get-Content -LiteralPath $cfgPath -Raw | ConvertFrom-Json
        Assert (@($cfg.toolsets) -contains 'all') 'toolsets=all not merged'
        Assert ($cfg.enableToast -eq $false) 'Existing key clobbered by merge'
        Assert (@(Get-ChildItem -LiteralPath $fixture.Root -Recurse -Filter '*.rvtmcp-rollback-*').Count -eq 0) 'Transaction backups leaked after success'
    }
    Test 'WhatIf reports the seed without writing the config' {
        $cfgPath = Join-Path $sandboxLocalAppData 'Bimwright\rvt-mcp\rvtmcp.config.json'
        Remove-Item -LiteralPath $cfgPath -Force -ErrorAction SilentlyContinue
        $fixture = New-SetupFixture
        $output = Invoke-FixtureSetup $fixture -WhatIf 3>&1 6>&1 | Out-String -Width 4096
        Assert (-not (Test-Path -LiteralPath $cfgPath)) 'WhatIf wrote the config'
        Assert ($output -match 'preview seed toolsets=all') 'Seed preview missing'
    }
    Test 'Client wiring: auto wires present file clients; JSONC comments and siblings survive' {
        $fixture = New-SetupFixture
        New-Item -ItemType Directory -Path "$sandboxUserProfile\.cursor", "$sandboxUserProfile\.config\kilo" -Force | Out-Null
        $cursor = "$sandboxUserProfile\.cursor\mcp.json"
        Set-Content -LiteralPath $cursor -Value '{ "mcpServers": { "other": { "command": "x" } } }'
        $kilo = "$sandboxUserProfile\.config\kilo\kilo.jsonc"
        Set-Content -LiteralPath $kilo -Value "// kilo config`n{`n  /* keep me */`n  `"mcp`": {}`n}"
        $output = Invoke-FixtureSetup $fixture -Client 'auto' | Out-Null
        $c = Get-Content -LiteralPath $cursor -Raw | ConvertFrom-Json
        Assert ($c.mcpServers.'rvt-mcp'.command -match 'server[\\/]+current[\\/]+rvt-mcp\.exe$') 'cursor entry missing or mispointed'
        Assert ($c.mcpServers.other.command -eq 'x') 'cursor sibling clobbered'
        Assert (Test-Path -LiteralPath "$cursor.bak") 'cursor backup missing'
        $kraw = Get-Content -LiteralPath $kilo -Raw
        Assert ($kraw.Contains('keep me') -and $kraw.Contains('// kilo config')) 'JSONC comments lost'
        $k = ConvertFrom-JsoncText $kraw
        Assert ($k.mcp.'rvt-mcp'.command[0] -match 'server[\\/]+current[\\/]+rvt-mcp\.exe$') 'kilo entry missing'
        Assert ($k.mcp.'rvt-mcp'.type -eq 'local') 'kilo entry shape wrong'
        # Second run must be idempotent, not a duplicate.
        $res = Invoke-McpClientWiring -Clients 'cursor' -Exe "$($fixture.Root)\server\current\rvt-mcp.exe" -Mode Add
        Assert ($res -match 'already') 're-wire not idempotent'
        Assert (@((ConvertFrom-Json (Get-Content -LiteralPath $cursor -Raw)).mcpServers.PSObject.Properties.Name).Count -eq 2) 'duplicate entry added'
    }
    Test 'Client wiring repoints versioned paths and preserves custom launchers' {
        $fixture = New-SetupFixture
        $exe = "$($fixture.Root)\server\current\rvt-mcp.exe"
        New-Item -ItemType Directory -Path "$sandboxUserProfile\.cursor" -Force | Out-Null
        $cursor = "$sandboxUserProfile\.cursor\mcp.json"
        # The whole command value is replaced, not just its rvt\server\<ver> tail
        # (a pre-0.6.3 path carries the same RvtMcp prefix as the new one).
        $legacy = 'C:\\Users\\Someone\\AppData\\Local\\RvtMcp\\rvt\\server\\0.6.2\\rvt-mcp.exe'
        Set-Content -LiteralPath $cursor -Value ('{ "mcpServers": { "rvt-mcp": { "command": "' + $legacy + '", "args": ["--read-only"] } } }')
        $res = Invoke-McpClientWiring -Clients 'cursor' -Exe $exe -Mode Add
        Assert ($res -match 'repointed') "expected repoint, got: $res"
        $c = Get-Content -LiteralPath $cursor -Raw | ConvertFrom-Json
        Assert ($c.mcpServers.'rvt-mcp'.command -eq $exe) "not repointed to exactly $exe, got: $($c.mcpServers.'rvt-mcp'.command)"
        Assert ($c.mcpServers.'rvt-mcp'.args[0] -eq '--read-only') 'existing args lost'
        # Same for the array-shaped command of opencode/kilo.
        New-Item -ItemType Directory -Path "$sandboxUserProfile\.config\kilo" -Force | Out-Null
        $kilo = "$sandboxUserProfile\.config\kilo\kilo.jsonc"
        Set-Content -LiteralPath $kilo -Value ('{ "mcp": { "rvt-mcp": { "type": "local", "command": ["' + $legacy + '"], "enabled": true } } }')
        $res = Invoke-McpClientWiring -Clients 'kilo' -Exe $exe -Mode Add
        Assert ($res -match 'repointed') "expected kilo repoint, got: $res"
        $k = Get-Content -LiteralPath $kilo -Raw | ConvertFrom-Json
        Assert ($k.mcp.'rvt-mcp'.command[0] -eq $exe) "kilo not repointed to exactly $exe, got: $($k.mcp.'rvt-mcp'.command[0])"
        # The old root's own current\ copy and versioned copies under the new
        # root repoint too; the new root's current\ is never repointed.
        foreach ($old in @('C:\\Users\\Someone\\AppData\\Local\\RvtMcp\\rvt\\server\\current\\rvt-mcp.exe',
                           'C:\\Users\\Someone\\AppData\\Local\\Bimwright\\rvt-mcp\\server\\0.7.0\\rvt-mcp.exe')) {
            Set-Content -LiteralPath $cursor -Value ('{ "mcpServers": { "rvt-mcp": { "command": "' + $old + '" } } }')
            $res = Invoke-McpClientWiring -Clients 'cursor' -Exe $exe -Mode Add
            Assert ($res -match 'repointed') "expected repoint for $old, got: $res"
            Assert ((Get-Content -LiteralPath $cursor -Raw | ConvertFrom-Json).mcpServers.'rvt-mcp'.command -eq $exe) "$old not repointed to $exe"
        }
        # A custom launcher is reported and left alone.
        Set-Content -LiteralPath $cursor -Value '{ "mcpServers": { "rvt-mcp": { "command": "C:\\tools\\my-wrapper.cmd" } } }'
        $res = Invoke-McpClientWiring -Clients 'cursor' -Exe $exe -Mode Add
        Assert ($res -match 'custom') "expected custom, got: $res"
        Assert ((Get-Content -LiteralPath $cursor -Raw).Contains('my-wrapper.cmd')) 'custom launcher clobbered'
    }
    Test 'Client wiring: explicit uninstalled client reports; uninstall removes only rvt-mcp' {
        $fixture = New-SetupFixture
        $res = Invoke-McpClientWiring -Clients 'zed' -Exe 'x' -Mode Add
        Assert ($res -match 'not detected') "expected not-detected report, got: $res"
        Assert (-not (Test-Path "$sandboxUserProfile\.config\zed")) 'undetected client dir created'
        New-Item -ItemType Directory -Path "$sandboxUserProfile\.cursor" -Force | Out-Null
        $cursor = "$sandboxUserProfile\.cursor\mcp.json"
        Set-Content -LiteralPath $cursor -Value '{ "mcpServers": { "other": { "command": "x" }, "rvt-mcp": { "command": "y" } } }'
        Invoke-FixtureSetup $fixture -Client 'cursor' -Uninstall | Out-Null
        $c = Get-Content -LiteralPath $cursor -Raw | ConvertFrom-Json
        Assert (-not $c.mcpServers.PSObject.Properties['rvt-mcp']) 'rvt-mcp entry not removed'
        Assert ($c.mcpServers.other.command -eq 'x') 'sibling removed'
        Assert (Test-Path -LiteralPath "$cursor.bak") 'removal backup missing'
    }
    Test 'Client wiring: malformed config warned not clobbered; WhatIf writes nothing' {
        $fixture = New-SetupFixture
        New-Item -ItemType Directory -Path "$sandboxUserProfile\.cursor" -Force | Out-Null
        $cursor = "$sandboxUserProfile\.cursor\mcp.json"
        Set-Content -LiteralPath $cursor -Value '{ "mcpServers": { oops'
        $res = Invoke-McpClientWiring -Clients 'cursor' -Exe 'x' -Mode Add 3>&1 | Out-String
        Assert ($res -match 'not valid JSON') "expected malformed report, got: $res"
        Assert ((Get-Content -LiteralPath $cursor -Raw).Contains('oops')) 'malformed file clobbered'
        Invoke-FixtureSetup $fixture -Client 'cursor' -WhatIf | Out-Null
        Assert ((Get-Content -LiteralPath $cursor -Raw).Contains('oops')) 'WhatIf wrote client config'
    }
    Test 'Client wiring: CLI clients detected only via PATH and invoked through their CLI' {
        $fixture = New-SetupFixture
        $fixture | Add-Member -NotePropertyName FakeCli -NotePropertyValue $true
        Invoke-FixtureSetup $fixture -Client 'claude' | Out-Null
        Assert (@($fixture.CliCalls | Where-Object { $_ -like 'claude mcp add *' }).Count -eq 1) "claude mcp add not invoked: $($fixture.CliCalls -join ';')"
        Assert (@($fixture.CliCalls | Where-Object { $_ -like '*rvt-mcp*' }).Count -ge 1) 'entry name missing from cli call'
    }
    Test 'Client wiring: claude stderr from the best-effort local remove does not fail a fresh install' {
        # Real `claude mcp remove -s local` on a fresh machine prints this to stderr;
        # under $ErrorActionPreference = 'Stop' Windows PowerShell 5.1 threw on it.
        $fixture = New-SetupFixture
        $fixture | Add-Member -NotePropertyName FakeCli -NotePropertyValue $true
        $fixture | Add-Member -NotePropertyName CliStderr -NotePropertyValue @{ 'mcp remove' = 'No MCP server named "rvt-mcp" in local scope' }
        $fixture | Add-Member -NotePropertyName CliOutput -NotePropertyValue @{ 'mcp get' = 'rvt-mcp:' }
        Remove-Item -LiteralPath "$sandboxUserProfile\.claude.json" -Force -ErrorAction SilentlyContinue
        $output = Invoke-FixtureSetup $fixture -Client 'claude' 6>&1 | Out-String -Width 4096
        Assert ($output -match 'Client\s*:\s*claude: wired') "fresh claude wiring not reported as wired: $output"
        Assert (@($fixture.CliCalls | Where-Object { $_ -like 'claude mcp add *' }).Count -eq 1) "claude mcp add not invoked after the remove: $($fixture.CliCalls -join ';')"
    }
    Test 'Client wiring: claude-desktop resolves the MSIX LocalCache config and preserves cowork keys' {
        $fixture = New-SetupFixture
        Remove-Item -LiteralPath "$sandboxLocalAppData\Packages" -Recurse -Force -ErrorAction SilentlyContinue
        $pkgDir = "$sandboxLocalAppData\Packages\Claude_testpfn\LocalCache\Roaming\Claude"
        New-Item -ItemType Directory -Path $pkgDir -Force | Out-Null
        $cfg = "$pkgDir\claude_desktop_config.json"
        Set-Content -LiteralPath $cfg -Value @'
{
  "mcpServers": { "other": { "command": "x" } },
  "coworkUserFilesPath": "C:\\Users\\Cowork\\Claude",
  "preferences": { "coworkBrowserToolsEnabled": true }
}
'@
        $exe = "$($fixture.Root)\server\current\rvt-mcp.exe"
        $res = Invoke-McpClientWiring -Clients 'claude-desktop' -Exe $exe -Mode Add
        Assert ($res -match 'wired|added') "expected wired, got: $res"
        Assert ($res -match [regex]::Escape($cfg)) 'resolved MSIX path not reported'
        $d = Get-Content -LiteralPath $cfg -Raw | ConvertFrom-Json
        Assert ($d.mcpServers.'rvt-mcp'.command -match 'server[\\/]+current[\\/]+rvt-mcp\.exe$') 'rvt-mcp entry missing at MSIX path'
        Assert ($d.mcpServers.other.command -eq 'x') 'sibling server lost'
        Assert ($d.coworkUserFilesPath -eq 'C:\Users\Cowork\Claude') 'cowork key lost'
        Assert ($d.preferences.coworkBrowserToolsEnabled -eq $true) 'cowork prefs lost'
        Assert (-not (Test-Path "$sandboxUserProfile\.claude.json")) 'claude code file touched by desktop wiring'
        # Uninstall pulls only the rvt-mcp member.
        Invoke-McpClientWiring -Clients 'claude-desktop' -Mode Remove | Out-Null
        $d = Get-Content -LiteralPath $cfg -Raw | ConvertFrom-Json
        Assert (-not $d.mcpServers.PSObject.Properties['rvt-mcp']) 'uninstall left the entry'
        Assert ($d.coworkUserFilesPath -eq 'C:\Users\Cowork\Claude') 'uninstall lost cowork keys'
    }
    Test 'Client wiring: claude-desktop prefers the MSIX package cache over a stray Roaming dir' {
        # Regression: %APPDATA%\Claude exists (native leftovers) but the app is
        # MSIX and only reads its package LocalCache - and the package family
        # name is arbitrary, not necessarily Claude_*.
        $fixture = New-SetupFixture
        Remove-Item -LiteralPath "$sandboxLocalAppData\Packages" -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Path "$sandboxAppData\Claude" -Force | Out-Null
        New-Item -ItemType Directory -Path "$sandboxLocalAppData\Packages\Anthropic.Claude_9zz\LocalCache\Roaming\Claude" -Force | Out-Null
        $res = Invoke-McpClientWiring -Clients 'auto' -Exe 'x' -Mode Add
        Assert ($res -match 'claude-desktop') "auto did not pick up the MSIX package: $res"
        $expected = "$sandboxLocalAppData\Packages\Anthropic.Claude_9zz\LocalCache\Roaming\Claude\claude_desktop_config.json"
        Assert (Test-Path -LiteralPath $expected) 'entry not created inside the MSIX cache'
        Assert (-not (Test-Path "$sandboxAppData\Claude\claude_desktop_config.json")) 'wrote to the Roaming dir the MSIX app ignores'
        Remove-Item -LiteralPath "$sandboxAppData\Claude" -Recurse -Force
    }
    Test 'Client wiring: claude-desktop warns when the app is running and reports not-detected cleanly' {
        $fixture = New-SetupFixture
        Remove-Item -LiteralPath "$sandboxLocalAppData\Packages" -Recurse -Force -ErrorAction SilentlyContinue
        $fixture | Add-Member -NotePropertyName ClaudeRunning -NotePropertyValue $true
        $pkgDir = "$sandboxLocalAppData\Packages\Claude_pfn2\LocalCache\Roaming\Claude"
        New-Item -ItemType Directory -Path $pkgDir -Force | Out-Null
        $cfg = "$pkgDir\claude_desktop_config.json"
        Set-Content -LiteralPath $cfg -Value '{ "mcpServers": {} }'
        $output = Invoke-FixtureSetup $fixture -Client 'claude-desktop' 6>&1 | Out-String -Width 4096
        Assert ($output -match 'claude-desktop:.*app is running') "running-app warning missing: $output"
        Assert ((Get-Content -LiteralPath $cfg -Raw | ConvertFrom-Json).mcpServers.'rvt-mcp') 'entry not written while app running'
        $f2 = New-SetupFixture
        Remove-Item -LiteralPath "$sandboxLocalAppData\Packages" -Recurse -Force -ErrorAction SilentlyContinue
        $out2 = Invoke-FixtureSetup $f2 -Client 'claude-desktop' 6>&1 | Out-String -Width 4096
        Assert ($out2 -match 'claude-desktop: not detected') 'not-detected report missing on a clean machine'
        Assert (-not (Test-Path "$sandboxAppData\Claude\claude_desktop_config.json")) 'created a config for an undetected client'
    }
    Test 'Client wiring: default wires detected clients; -Client none and a plain uninstall leave configs alone' {
        Remove-Item -LiteralPath "$sandboxUserProfile\.cursor" -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Path "$sandboxUserProfile\.cursor" -Force | Out-Null
        $cursor = "$sandboxUserProfile\.cursor\mcp.json"
        Set-Content -LiteralPath $cursor -Value '{ "mcpServers": {} }'
        $output = Invoke-FixtureSetup (New-SetupFixture) -Client 'none' 6>&1 | Out-String -Width 4096
        Assert ((Get-Content -LiteralPath $cursor -Raw).Trim() -eq '{ "mcpServers": {} }') '-Client none touched client config'
        Assert (-not (Test-Path -LiteralPath "$cursor.bak")) 'backup created on no-op'
        Assert ($output -match 'wire with -Client') 'detection hint missing'
        $fixture = New-SetupFixture
        $output = Invoke-FixtureSetup $fixture 6>&1 | Out-String -Width 4096
        $c = Get-Content -LiteralPath $cursor -Raw | ConvertFrom-Json
        Assert ($c.mcpServers.'rvt-mcp'.command -match 'server[\\/]+current[\\/]+rvt-mcp\.exe$') "default install did not wire cursor: $output"
        Invoke-FixtureSetup $fixture -Uninstall 6>&1 | Out-Null
        Assert ((Get-Content -LiteralPath $cursor -Raw | ConvertFrom-Json).mcpServers.'rvt-mcp') 'plain uninstall removed the client entry'
    }
    Test 'Client wiring: keys that differ only in case do not make a config look broken' {
        # Claude Code keeps per-project keys such as C:/a/Desktop and C:/a/desktop.
        New-Item -ItemType Directory -Path "$sandboxUserProfile\.cursor" -Force | Out-Null
        $cursor = "$sandboxUserProfile\.cursor\mcp.json"
        Set-Content -LiteralPath $cursor -Value '{ "projects": { "C:/a/Desktop": {}, "C:/a/desktop": {} }, "mcpServers": {} }'
        $res = Invoke-McpClientWiring -Clients 'cursor' -Exe 'C:\x\rvt-mcp.exe' -Mode Add 3>&1 | Out-String
        Assert ($res -match 'added') "expected added, got: $res"
        $raw = Get-Content -LiteralPath $cursor -Raw
        Assert ($raw.Contains('"rvt-mcp"')) 'entry not written'
        Assert ($raw.Contains('C:/a/Desktop') -and $raw.Contains('C:/a/desktop')) 'case-variant key lost'
    }
    Test 'Client wiring: claude repoints an existing versioned user entry that mcp add would refuse' {
        $fixture = New-SetupFixture
        $fixture | Add-Member -NotePropertyName FakeCli -NotePropertyValue $true
        # Real `claude mcp add` refuses to replace an entry and leaves the old command.
        $fixture | Add-Member -NotePropertyName CliOutput -NotePropertyValue @{ 'mcp add' = 'MCP server rvt-mcp already exists in user config'; 'mcp get' = 'rvt-mcp:' }
        $claudeJson = "$sandboxUserProfile\.claude.json"
        Set-Content -LiteralPath $claudeJson -Value '{ "numStartups": 3, "mcpServers": { "rvt-mcp": { "type": "stdio", "command": "C:\\Users\\Someone\\AppData\\Local\\RvtMcp\\rvt\\server\\0.6.2\\rvt-mcp.exe", "args": ["--read-only"], "env": {} } }, "projects": { "D:/x": { "mcpServers": {} } } }'
        $output = Invoke-FixtureSetup $fixture -Client 'claude' 6>&1 | Out-String -Width 4096
        $j = Get-Content -LiteralPath $claudeJson -Raw | ConvertFrom-Json
        Remove-Item -LiteralPath $claudeJson, "$claudeJson.bak" -Force -ErrorAction SilentlyContinue
        Assert ($j.mcpServers.'rvt-mcp'.command -eq "$($fixture.Root)\server\current\rvt-mcp.exe") "user entry not repointed, got: $($j.mcpServers.'rvt-mcp'.command)"
        Assert ($j.mcpServers.'rvt-mcp'.args[0] -eq '--read-only') 'existing args lost'
        Assert ($j.numStartups -eq 3) 'unrelated claude state lost'
        Assert ($output -match 'Client\s*:\s*claude: repointed') "repoint not reported: $output"
    }
    Test 'Client wiring: a refused claude mcp add is not reported as wired' {
        $fixture = New-SetupFixture
        $fixture | Add-Member -NotePropertyName FakeCli -NotePropertyValue $true
        # The entry lives where ~/.claude.json cannot show it (CLAUDE_CONFIG_DIR).
        Remove-Item -LiteralPath "$sandboxUserProfile\.claude.json" -Force -ErrorAction SilentlyContinue
        $fixture | Add-Member -NotePropertyName CliOutput -NotePropertyValue @{ 'mcp add' = 'MCP server rvt-mcp already exists in user config'; 'mcp get' = "rvt-mcp:`n  Command: C:\old\rvt\server\0.6.2\rvt-mcp.exe" }
        $output = Invoke-FixtureSetup $fixture -Client 'claude' 6>&1 | Out-String -Width 4096
        Assert (-not ($output -match 'claude: wired')) "refused add reported as wired: $output"
        Assert ($output -match 'claude: existing rvt-mcp entry left unchanged') "refusal not reported: $output"
    }
    # --- Legacy data-root relocation: %LOCALAPPDATA%\RvtMcp -> Bimwright\rvt-mcp ---
    Test 'Move-LegacyProductRoot: absent old root is a no-op' {
        Clear-ProductRoots
        $script:installChanges = New-Object System.Collections.Generic.List[object]
        Move-LegacyProductRoot -LocalAppData $sandboxLocalAppData
        Assert ($script:installChanges.Count -eq 0) 'recorded a change for nothing'
        Assert (-not (Test-Path -LiteralPath "$sandboxLocalAppData/Bimwright")) 'created the family root for nothing'
        $script:installChanges = $null
    }
    Test 'Move-LegacyProductRoot: relocates whole and flattens rvt\server; undo is byte-identical' {
        Clear-ProductRoots
        $old = New-LegacyRootFixture
        $new = Join-Path $sandboxLocalAppData 'Bimwright\rvt-mcp'
        $script:installChanges = New-Object System.Collections.Generic.List[object]
        Move-LegacyProductRoot -LocalAppData $sandboxLocalAppData
        Assert (-not (Test-Path -LiteralPath $old)) 'old root still present'
        Assert ((Get-Content -LiteralPath "$new/rvtmcp.config.json" -Raw).Contains('custom')) 'config not relocated'
        Assert ((Get-Content -LiteralPath "$new/baked/x/tool.json" -Raw).Trim() -eq '{}') 'baked data not relocated'
        Assert ((Get-Content -LiteralPath "$new/locales/strings.vi.json" -Raw).Contains('"k"')) 'locales not relocated'
        Assert ((Get-Content -LiteralPath "$new/server/current/rvt-mcp.exe" -Raw).Trim() -eq 'old-server-current') 'nested server not flattened'
        Assert ((Get-Content -LiteralPath "$new/server/0.6.2/rvt-mcp.exe" -Raw).Trim() -eq 'old-server-062') 'versioned server not relocated'
        Assert (@($script:installChanges | Where-Object { $_.Kind -eq 'Relocate' }).Count -eq 2) 'both renames not recorded'
        Undo-InstallChanges
        Assert (-not (Test-Path -LiteralPath $new)) 'undo left the new root behind'
        Assert ((Get-Content -LiteralPath "$old/rvt/server/current/rvt-mcp.exe" -Raw).Trim() -eq 'old-server-current') 'undo did not restore the nested server'
        Assert ((Get-Content -LiteralPath "$old/rvtmcp.config.json" -Raw).Contains('custom')) 'undo lost config data'
        $script:installChanges = $null
        Clear-ProductRoots
    }
    Test 'Move-LegacyProductRoot: both roots present throws before any move' {
        Clear-ProductRoots
        $old = New-LegacyRootFixture
        $new = Join-Path $sandboxLocalAppData 'Bimwright\rvt-mcp'
        New-Item -ItemType Directory -Path $new -Force | Out-Null
        $script:installChanges = New-Object System.Collections.Generic.List[object]
        Assert-Throws { Move-LegacyProductRoot -LocalAppData $sandboxLocalAppData } 'Move or remove'
        Assert (Test-Path -LiteralPath "$old/rvt/server/current") 'old root moved despite the conflict'
        Assert ($script:installChanges.Count -eq 0) 'a partial move was recorded'
        $script:installChanges = $null
        Clear-ProductRoots
    }
    Test 'Move-LegacyProductRoot -WhatIf prints planned moves and writes nothing' {
        Clear-ProductRoots
        $old = New-LegacyRootFixture
        $script:installChanges = New-Object System.Collections.Generic.List[object]
        $output = Move-LegacyProductRoot -LocalAppData $sandboxLocalAppData -WhatIf 6>&1 | Out-String
        Assert ($output -match 'preview move') 'planned moves not previewed'
        Assert (Test-Path -LiteralPath "$old/rvt/server/current/rvt-mcp.exe") 'WhatIf moved files'
        Assert (-not (Test-Path -LiteralPath "$sandboxLocalAppData/Bimwright")) 'WhatIf created directories'
        $script:installChanges = $null
        Clear-ProductRoots
    }
    Test 'Install relocates the old data root, upgrades in place and keeps siblings' {
        Clear-ProductRoots
        $fixture = New-SetupFixture
        $old = New-LegacyRootFixture
        $new = Join-Path $sandboxLocalAppData 'Bimwright\rvt-mcp'
        New-Item -ItemType Directory -Path "$sandboxLocalAppData/Bimwright/ipt-mcp", "$sandboxLocalAppData/Bimwright/Dwg" -Force | Out-Null
        Set-Content -LiteralPath "$sandboxLocalAppData/Bimwright/ipt-mcp/keep.txt" 'k'
        Set-Content -LiteralPath "$sandboxLocalAppData/Bimwright/Dwg/keep.txt" 'k'
        $output = Invoke-FixtureSetup $fixture -ServerInstallRoot "$new/server/current" 3>&1 6>&1 | Out-String -Width 4096
        Assert (-not (Test-Path -LiteralPath $old)) 'old root still present after install'
        Assert ((Get-Content -LiteralPath "$new/server/current/rvt-mcp.exe" -Raw).Trim() -eq 'new-server') 'new server not installed at the new root'
        Assert ((Get-Content -LiteralPath "$new/server/0.6.2/rvt-mcp.exe" -Raw).Trim() -eq 'old-server-062') 'relocated versioned server lost'
        Assert (-not (Test-Path -LiteralPath "$new/rvt")) 'emptied rvt\ was not removed'
        Assert (@(Get-OtherServerVersions -InstallRoot "$new/server/current" | Where-Object Name -eq '0.6.2').Count -eq 1) 'relocated 0.6.2 not seen as a legacy version'
        $cfg = Get-Content -LiteralPath "$new/rvtmcp.config.json" -Raw | ConvertFrom-Json
        Assert ($cfg.custom -eq $true -and @($cfg.toolsets) -contains 'all') 'relocated config not merged'
        Assert ((Get-Content -LiteralPath "$sandboxLocalAppData/Bimwright/ipt-mcp/keep.txt" -Raw).Trim() -eq 'k') 'ipt-mcp sibling touched'
        Assert ((Get-Content -LiteralPath "$sandboxLocalAppData/Bimwright/Dwg/keep.txt" -Raw).Trim() -eq 'k') 'Dwg sibling touched'
        foreach ($year in @(2026,2027)) {
            Assert ((Get-Content -LiteralPath "$($fixture.Root)/addins/$year/RvtMcp/RvtMcp.Plugin.dll" -Raw).Trim() -eq "new-plugin-$year") 'Plugin not upgraded'
        }
        Clear-ProductRoots
    }
    Test 'Both roots existing stops the install before any change' {
        Clear-ProductRoots
        $fixture = New-SetupFixture
        $old = New-LegacyRootFixture
        $new = Join-Path $sandboxLocalAppData 'Bimwright\rvt-mcp'
        New-Item -ItemType Directory -Path "$new/server/current" -Force | Out-Null
        Set-Content -LiteralPath "$new/server/current/rvt-mcp.exe" 'newer-server'
        Assert-Throws { Invoke-FixtureSetup $fixture -ServerInstallRoot "$new/server/current" } 'Move or remove'
        Assert (Test-Path -LiteralPath "$old/rvtmcp.config.json") 'old root touched'
        Assert (Test-Path -LiteralPath "$old/rvt/server/current/rvt-mcp.exe") 'old server moved'
        Assert ((Get-Content -LiteralPath "$new/server/current/rvt-mcp.exe" -Raw).Trim() -eq 'newer-server') 'new root touched'
        Assert-OldInstall $fixture
        Clear-ProductRoots
    }
    Test 'Failure after relocation restores the old root byte-identically' {
        Clear-ProductRoots
        $fixture = New-SetupFixture; $fixture.SmokeFails = $true
        $old = New-LegacyRootFixture
        $new = Join-Path $sandboxLocalAppData 'Bimwright\rvt-mcp'
        Assert-Throws { Invoke-FixtureSetup $fixture -ServerInstallRoot "$new/server/current" } 'could not start'
        Assert (-not (Test-Path -LiteralPath $new)) 'new root survived rollback'
        Assert (-not (Test-Path -LiteralPath "$sandboxLocalAppData/Bimwright")) 'family root left behind - not restored'
        Assert ((Get-Content -LiteralPath "$old/rvt/server/current/rvt-mcp.exe" -Raw).Trim() -eq 'old-server-current') 'nested current not restored'
        Assert ((Get-Content -LiteralPath "$old/rvt/server/0.6.2/rvt-mcp.exe" -Raw).Trim() -eq 'old-server-062') 'versioned copy not restored'
        Assert ((Get-Content -LiteralPath "$old/rvtmcp.config.json" -Raw).Contains('custom')) 'config not restored'
        Assert-OldInstall $fixture
        Assert (@(Get-ChildItem -LiteralPath $fixture.Root -Recurse -Filter '*.rvtmcp-rollback-*').Count -eq 0) 'backups left behind'
        Assert (@(Get-ChildItem -LiteralPath $sandboxLocalAppData -Recurse -Filter '*.rvtmcp-rollback-*' -ErrorAction SilentlyContinue).Count -eq 0) 'backups left under profile'
        Clear-ProductRoots
    }
    Test 'WhatIf previews relocation without touching the filesystem' {
        Clear-ProductRoots
        $fixture = New-SetupFixture
        $old = New-LegacyRootFixture
        $output = Invoke-FixtureSetup $fixture -WhatIf 3>&1 6>&1 | Out-String -Width 4096
        Assert ($output -match 'preview move') 'relocation not previewed'
        Assert (Test-Path -LiteralPath "$old/rvt/server/current/rvt-mcp.exe") 'WhatIf moved files'
        Assert (-not (Test-Path -LiteralPath "$sandboxLocalAppData/Bimwright")) 'WhatIf created the family root'
        Assert-OldInstall $fixture
        Clear-ProductRoots
    }
    Test 'Shipped scripts parse as Windows PowerShell 5.1 reads them on an ANSI code page' {
        # powershell.exe decodes a BOM-less script with the system ANSI code page
        # (1252 en-US, 1258 vi-VN); UTF-8 punctuation such as an em dash then
        # decodes to a smart quote that ends the string early.
        foreach ($name in 'install.ps1', 'uninstall-all.ps1') {
            $path = (Resolve-Path (Join-Path $PSScriptRoot "../../scripts/$name")).Path
            foreach ($cp in 1252, 1258) {
                $reader = New-Object IO.StreamReader($path, [Text.Encoding]::GetEncoding($cp), $true)
                try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
                $errs = $null
                $null = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$null, [ref]$errs)
                Assert ($errs.Count -eq 0) "$name has $($errs.Count) parse errors under code page $cp (first at line $(if ($errs.Count) { $errs[0].Extent.StartLineNumber }))"
            }
        }
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
