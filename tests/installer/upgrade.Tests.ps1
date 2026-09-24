#Requires -Version 5.1
# No Pester dependency. Execute production functions against isolated temporary files.
[CmdletBinding()]
param([string]$ResultPath, [string]$TestRootParent = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot '../../scripts/install.ps1'
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path $installer), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
foreach ($fn in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    . ([scriptblock]::Create($fn.Extent.Text))
}
$testRoot = Join-Path $TestRootParent ('rvt-installer-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
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
function New-SetupFixture {
    $root = Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
    $source = Join-Path $root 'source'
    New-Item -ItemType Directory -Path "$source/plugins", "$source/server", "$root/server/0.6.1", "$root/server/0.6.2", "$root/server/dev" -Force | Out-Null
    Set-Content "$source/server/rvt-mcp.exe" 'new-server'
    Set-Content "$root/server/0.6.1/rvt-mcp.exe" 'old-server-061'
    Set-Content "$root/server/0.6.2/rvt-mcp.exe" 'old-server-062'
    Set-Content "$root/server/dev/rvt-mcp.exe" 'dev-build'
    foreach ($year in @(2026,2027)) {
        $payload = Join-Path $root "payload-$year"
        $addins = Join-Path $root "addins/$year"
        New-Item -ItemType Directory -Path $payload, "$addins/RvtMcp" -Force | Out-Null
        Set-Content "$payload/RvtMcp.Plugin.dll" "new-plugin-$year"
        Set-Content "$payload/RvtMcp.R$($year-2000).addin" "new-addin-$year"
        Set-Content "$addins/RvtMcp/RvtMcp.Plugin.dll" "old-plugin-$year"
        Set-Content "$addins/RvtMcp/old-only.dll" 'old dependency'
        Set-Content "$addins/RvtMcp.R$($year-2000).addin" "old-addin-$year"
        Compress-Archive -Path "$payload/*" -DestinationPath "$source/plugins/RvtMcp.Plugin.R$($year-2000).zip"
    }
    $config = Join-Path $root 'claude.json'
    Set-Content $config '{"mcpServers":{"rvt-mcp":{"command":"rvt-mcp","args":["--read-only"]}}}'
    $manifest = [pscustomobject]@{files=@(Get-ChildItem $source -File -Recurse | ForEach-Object {
        [pscustomobject]@{path=$_.FullName.Substring($source.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })}
    return [pscustomobject]@{Root=$root;Source=$source;Config=$config;Running=$false;SecondConfig=$null;Manifest=$manifest}
}
function Invoke-FixtureSetup {
    [CmdletBinding(SupportsShouldProcess=$true)]
    param($Fixture, [string]$Client='none')
    # Only OS boundaries are redirected. Main control flow, ZIP extraction,
    # directory replacement, config writing and rollback are production code.
    function Get-AddinsRoot([int]$year) { Join-Path $Fixture.Root "addins/$year" }
    function Get-Process { param($Name, $ErrorAction) if ($Fixture.Running) { [pscustomobject]@{Name='Revit';Id=1234} } }
    function Add-ClaudeEntries {
        param($Targets, [switch]$RequireExisting)
        Add-ClaudeEntry -ConfigPath $Fixture.Config -Targets $Targets
        if ($Fixture.SecondConfig) { Add-ClaudeEntry -ConfigPath $Fixture.SecondConfig -Targets $Targets }
    }
    $SourceDir=$Fixture.Source; $pluginSourceDir=Join-Path $SourceDir 'plugins'; $serverSourceDir=Join-Path $SourceDir 'server'
    $ServerInstallRoot=Join-Path $Fixture.Root 'server/0.6.2'; $Years=@(2026,2027); $manifest=$Fixture.Manifest; $Uninstall=$false; $WireClient=$null
    & $mainBody
}
function Assert-OldInstall($Fixture) {
    foreach ($year in @(2026,2027)) {
        Assert ((Get-Content "$($Fixture.Root)/addins/$year/RvtMcp/RvtMcp.Plugin.dll" -Raw).Trim() -eq "old-plugin-$year") "Old $year plugin not restored"
        Assert ((Get-Content "$($Fixture.Root)/addins/$year/RvtMcp.R$($year-2000).addin" -Raw).Trim() -eq "old-addin-$year") "Old $year manifest not restored"
        Assert (Test-Path "$($Fixture.Root)/addins/$year/RvtMcp/old-only.dll") 'Old dependency not restored'
    }
    Assert ((Get-Content "$($Fixture.Root)/server/0.6.1/rvt-mcp.exe" -Raw).Trim() -eq 'old-server-061') 'Previous server version changed'
    Assert ((Get-Content "$($Fixture.Root)/server/0.6.2/rvt-mcp.exe" -Raw).Trim() -eq 'old-server-062') 'Same-version server not restored'
}
$targets = @([pscustomobject]@{Name='rvt-mcp';ServerCmd='C:\fixture\0.6.2\rvt-mcp.exe';Args=@()})
try {
    Test 'Codex preserves options, nested env and unrelated sections' {
        $path = Join-Path $testRoot 'config.toml'
        $before = @'
model = "keep-top"
[mcp_servers.rvt-mcp]
command = 'C:\fixture\0.6.1\rvt-mcp.exe' # previous install
args = ["--read-only", "--toolsets", "all"]
enabled = false
startup_timeout_sec = 120
[mcp_servers.rvt-mcp.env]
FIXTURE_OPTION = "keep"
[profiles.work]
model = "keep-profile"
[mcp_servers.other]
command = "keep-other"
'@
        Set-Content -LiteralPath $path -Value $before -Encoding UTF8 -NoNewline
        Add-CodexEntry -ConfigPath $path -Targets $targets | Out-Null
        $after = Get-Content -LiteralPath $path -Raw
        Assert ($after.Contains('0.6.2')) 'Server path was not upgraded'
        foreach ($value in @('--read-only','enabled = false','FIXTURE_OPTION','[profiles.work]','keep-other','startup_timeout_sec = 120')) {
            Assert ($after.Contains($value)) "Lost setting: $value"
        }
        Assert ((Get-Content -LiteralPath "$path.rvtmcp.bak" -Raw) -eq $before) 'Backup differs from original'
        Add-CodexEntry -ConfigPath $path -Targets $targets | Out-Null
        Assert ((Get-Content -LiteralPath $path -Raw) -eq $after) 'Second install is not idempotent'
        Assert ((Get-Content -LiteralPath "$path.rvtmcp.bak" -Raw) -eq $before) 'Second install overwrote backup'
    }
    foreach ($client in @('Claude','Opencode','Kilo')) {
        Test "$client preserves options and other servers" {
            $path = Join-Path $testRoot "$client.json"
            $mapKey = if ($client -eq 'Claude') { 'mcpServers' } else { 'mcp' }
            $entry = @{command='C:\fixture\0.6.1\rvt-mcp.exe';args=@('--read-only');env=@{FIXTURE_OPTION='keep'};disabled=$true}
            if ($client -ne 'Claude') {
                $entry = @{type='local';command=@('C:\fixture\0.6.1\rvt-mcp.exe','--read-only');enabled=$false;environment=@{FIXTURE_OPTION='keep'};timeout=77777}
            }
            $before = @{$mapKey=@{'rvt-mcp'=$entry;other=@{command='keep-other'};'bimwright-rvt-r27'=@{command='keep-legacy'}};theme='keep-theme'} | ConvertTo-Json -Depth 20
            Set-Content -LiteralPath $path -Value $before -Encoding UTF8 -NoNewline
            & "Add-${client}Entry" -ConfigPath $path -Targets $targets | Out-Null
            $cfg = Read-JsonHashtable $path
            $updated = $cfg[$mapKey]['rvt-mcp']
            Assert ($cfg.theme -eq 'keep-theme' -and $cfg[$mapKey].other.command -eq 'keep-other') 'Lost unrelated settings'
            Assert ($cfg[$mapKey]['bimwright-rvt-r27'].command -eq 'keep-legacy') 'Legacy entry was deleted'
            if ($client -eq 'Claude') {
                Assert ($updated.command -eq $targets[0].ServerCmd) 'Server path was not upgraded'
                Assert ($updated.args -contains '--read-only') 'Lost read-only flag'
                Assert ($updated.env.FIXTURE_OPTION -eq 'keep' -and $updated.disabled) 'Lost env or disabled option'
            } else {
                Assert ($updated.command[0] -eq $targets[0].ServerCmd) 'Server path was not upgraded'
                Assert ($updated.command[1] -eq '--read-only') 'Lost read-only flag'
                Assert ($updated.environment.FIXTURE_OPTION -eq 'keep' -and -not $updated.enabled -and $updated.timeout -eq 77777) 'Lost env, disabled or timeout option'
            }
            $after = Get-Content -LiteralPath $path -Raw
            & "Add-${client}Entry" -ConfigPath $path -Targets $targets | Out-Null
            Assert ((Get-Content -LiteralPath $path -Raw) -eq $after) 'Second install is not idempotent'
            Assert ((Get-Content -LiteralPath "$path.rvtmcp.bak" -Raw) -eq $before) 'Second install overwrote original backup'
        }
    }
    Test 'Codex quoted table preserves literal dollar signs and adjacent table' {
        $path = Join-Path $testRoot 'quoted.toml'
        Set-Content $path "  [mcp_servers.'rvt-mcp']`ncommand = 'rvt-mcp'`nargs = []`n  [profiles.work]`nmodel = 'keep'"
        $special = @([pscustomobject]@{Name='rvt-mcp';ServerCmd='C:\$1\rvt-mcp.exe';Args=@()})
        Add-CodexEntry -ConfigPath $path -Targets $special | Out-Null
        $raw = Get-Content $path -Raw
        Assert ($raw.Contains('$1') -and $raw.Contains('[profiles.work]')) 'TOML replacement corrupted content'
    }
    Test 'Custom launchers fail without config changes' {
        foreach ($client in @('Claude','Opencode','Kilo','Codex')) {
            $path = Join-Path $testRoot "wrapper-$client.config"
            $raw = switch ($client) {
                'Claude' { '{"mcpServers":{"rvt-mcp":{"command":"powershell","args":["custom.ps1"]}}}' }
                'Codex' { "[mcp_servers.rvt-mcp]`ncommand = 'powershell'`nargs = ['custom.ps1']`n" }
                default { '{"mcp":{"rvt-mcp":{"type":"local","command":["powershell","custom.ps1"]}}}' }
            }
            Set-Content $path $raw -NoNewline
            Assert-Throws { & "Add-${client}Entry" -ConfigPath $path -Targets $targets } 'Custom.*launcher'
            Assert ((Get-Content $path -Raw) -eq $raw) "$client wrapper config changed"
        }
    }
    Test 'Multiline TOML fails without interpreting embedded command text' {
        $path = Join-Path $testRoot 'multiline.toml'
        $raw = @'
instructions = '''
[mcp_servers.rvt-mcp]
command = "rvt-mcp"
'''
'@
        Set-Content $path $raw -NoNewline
        Assert-Throws { Add-CodexEntry -ConfigPath $path -Targets $targets } 'Multiline TOML'
        Assert ((Get-Content $path -Raw) -eq $raw) 'Multiline content changed'
    }
    Test 'Fresh config wiring and WhatIf leave no Kilo files' {
        $path = Join-Path $testRoot 'missing-kilo/config.json'
        Add-KiloEntry -ConfigPath $path -Targets $targets -RequireExisting -WhatIf | Out-Null
        Assert (-not (Test-Path (Split-Path -Parent $path))) 'WhatIf created files or directories'
        Add-KiloEntry -ConfigPath $path -Targets $targets -RequireExisting | Out-Null
        Assert ((Read-JsonHashtable $path).mcp.'rvt-mcp'.command[0] -eq $targets[0].ServerCmd) 'Fresh Kilo entry missing'
    }
    Test 'Two-year upgrade replaces plugin and server and preserves config flags' {
        $fixture = New-SetupFixture
        Invoke-FixtureSetup $fixture -Client claude
        foreach ($year in @(2026,2027)) {
            Assert ((Get-Content "$($fixture.Root)/addins/$year/RvtMcp/RvtMcp.Plugin.dll" -Raw).Trim() -eq "new-plugin-$year") 'Plugin not upgraded'
            Assert (-not (Test-Path "$($fixture.Root)/addins/$year/RvtMcp/old-only.dll")) 'Stale dependency survived upgrade'
        }
        Assert ((Get-Content "$($fixture.Root)/server/0.6.2/rvt-mcp.exe" -Raw).Trim() -eq 'new-server') 'Server not upgraded'
        Assert (-not (Test-Path "$($fixture.Root)/server/0.6.1")) 'Previous version not removed'
        Assert ((Get-Content "$($fixture.Root)/server/dev/rvt-mcp.exe" -Raw).Trim() -eq 'dev-build') 'Non-version server dir was cleaned'
        Assert ((Read-JsonHashtable $fixture.Config).mcpServers.'rvt-mcp'.args -contains '--read-only') 'Flag lost during whole install'
        Assert (@(Get-ChildItem $fixture.Root -Recurse -Filter '*.rvtmcp-rollback-*').Count -eq 0) 'Transaction backups leaked after success'
    }
    Test 'Revit running blocks the upgrade before any replacement' {
        $fixture = New-SetupFixture; $fixture.Running=$true
        Assert-Throws { Invoke-FixtureSetup $fixture } 'Revit running'
        Assert-OldInstall $fixture
    }
    Test 'WhatIf preserves installation and does not stage files' {
        $fixture = New-SetupFixture
        $before = (Get-FileHash $fixture.Config).Hash
        Invoke-FixtureSetup $fixture -Client claude -WhatIf
        Assert-OldInstall $fixture
        Assert ((Get-FileHash $fixture.Config).Hash -eq $before) 'WhatIf wrote config'
        Assert ($null -eq $script:installStage) 'WhatIf staged payload'
    }
    foreach ($kind in @('missing','corrupt','missing-manifest')) {
        Test "$kind ZIP blocks all replacements" {
            $fixture = New-SetupFixture
            $fixture.Manifest = $null # Exercise ZIP validation in the legacy no-manifest layout.
            $zip = Join-Path $fixture.Source 'plugins/RvtMcp.Plugin.R27.zip'
            if ($kind -eq 'missing') { Remove-Item -LiteralPath $zip }
            elseif ($kind -eq 'corrupt') { Set-Content $zip 'not a ZIP' }
            else { Compress-Archive -LiteralPath "$($fixture.Root)/payload-2027/RvtMcp.Plugin.dll" -DestinationPath $zip -Force }
            Assert-Throws { Invoke-FixtureSetup $fixture } '.'
            Assert-OldInstall $fixture
        }
    }
    Test 'Later config failure restores both years, server and earlier config bytes' {
        $fixture = New-SetupFixture
        $fixture.SecondConfig = Join-Path $fixture.Root 'invalid.json'
        Set-Content $fixture.SecondConfig 'invalid JSON'
        $before = (Get-FileHash $fixture.Config).Hash
        Assert-Throws { Invoke-FixtureSetup $fixture -Client claude } 'parse failed'
        Assert-OldInstall $fixture
        Assert ((Get-FileHash $fixture.Config).Hash -eq $before) 'Earlier config not restored byte-for-byte'
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
    Test 'Rollback removes newly created server and config' {
        $fixture = New-SetupFixture
        $newServer = Join-Path $fixture.Root 'server/new-version'
        $newConfig = Join-Path $fixture.Root 'new-config.json'
        $script:installChanges = New-Object System.Collections.Generic.List[object]
        Set-InstallPath -Source (Join-Path $fixture.Source 'server') -Destination $newServer
        Write-ConfigAtomic -Path $newConfig -Content '{}' | Out-Null
        Undo-InstallChanges
        Assert (-not (Test-Path $newServer) -and -not (Test-Path $newConfig)) 'New files survived rollback'
        $script:installChanges=$null
    }
    Test 'Incomplete rollback reports retained backup and continues restoring other paths' {
        $fixture = New-SetupFixture
        $path1 = Join-Path $fixture.Root 'rollback-one.json'
        $path2 = Join-Path $fixture.Root 'rollback-two.json'
        Set-Content $path1 'old-one'; Set-Content $path2 'old-two'
        $script:installChanges = New-Object System.Collections.Generic.List[object]
        Write-ConfigAtomic -Path $path1 -Content 'new-one' | Out-Null
        Write-ConfigAtomic -Path $path2 -Content 'new-two' | Out-Null
        $locked = [IO.File]::Open($path2, 'Open', 'Read', 'None')
        try { Assert-Throws { Undo-InstallChanges } 'Rollback incomplete.*|Rollback incomplete' }
        finally { $locked.Dispose() }
        Assert ((Get-Content $path1 -Raw).Trim() -eq 'old-one') 'Rollback stopped before earlier path'
        Assert (Test-Path $script:installChanges[1].Backup) 'Failed rollback deleted its recovery backup'
        $script:installChanges=$null
    }
} finally {
    $report = [ordered]@{powershell=$PSVersionTable.PSVersion.ToString();installerSha256=(Get-FileHash $installer -Algorithm SHA256).Hash;results=@($results.ToArray())}
    if ($ResultPath) { $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ResultPath -Encoding UTF8 }
    # Only remove the uniquely created test directory under the resolved temp root.
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath($TestRootParent).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
if (@($results | Where-Object { -not $_.passed }).Count) { throw 'Installer regression tests failed' }
