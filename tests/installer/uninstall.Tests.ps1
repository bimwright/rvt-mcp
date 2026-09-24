#Requires -Version 5.1
# No Pester dependency. Execute production functions against isolated temporary files.
# Profile env vars (USERPROFILE, APPDATA, LOCALAPPDATA) are redirected to a
# sandbox under the test root for the whole run, so a function that ignores its
# fixture path cannot reach real user data.
[CmdletBinding()]
param([string]$ResultPath, [string]$TestRootParent = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path $TestRootParent ('rvt-uninstaller-tests-' + [guid]::NewGuid().ToString('N'))
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
$uninstaller = Join-Path $PSScriptRoot '../../scripts/uninstall-all.ps1'
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path $uninstaller), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
# Dot-source only function definitions; the script body prompts and calls exit.
foreach ($fn in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    . ([scriptblock]::Create($fn.Extent.Text))
}
# Step4 must take the fixture root explicitly; without it the function falls
# back to the real profile and the tests would delete user data.
if (-not (Get-Command Invoke-Step4-Discovery).Parameters.ContainsKey('Root')) {
    throw 'Invoke-Step4-Discovery does not accept -Root; refusing to run'
}
$results = New-Object System.Collections.Generic.List[object]
$script:handled = @()
$script:skipped = @()
$script:failed  = @()
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Test([string]$Name, [scriptblock]$Body) {
    try { & $Body; $results.Add([pscustomobject]@{name=$Name;passed=$true}); Write-Host "PASS $Name" }
    catch { $results.Add([pscustomobject]@{name=$Name;passed=$false;error=$_.Exception.Message}); Write-Host "FAIL $Name : $_" }
}
$removableNames = @('rvt','spill','revit-2027.json')
$keptNames = @('rvtmcp.config.json','locales','bake.db','bake.db-wal','baked','usage.jsonl','bake-audit.jsonl','firm-profiles','shared-parameters.txt','.migrated-from-bimwright','logs','revit-mcp.log','mcp-calls.jsonl','send-code-journal.jsonl','journal','captures','mcp-calls.version','notes.txt','custom')
function New-RootFixture {
    $root = Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path "$root/locales", "$root/baked", "$root/firm-profiles", "$root/logs", "$root/rvt/server/0.6.2", "$root/spill", "$root/journal", "$root/captures", "$root/custom" -Force | Out-Null
    Set-Content "$root/rvtmcp.config.json" '{}'
    Set-Content "$root/locales/strings.vi.json" '{}'
    Set-Content "$root/bake.db" 'db'
    Set-Content "$root/bake.db-wal" 'wal'
    Set-Content "$root/baked/tool.cs" 'cs'
    Set-Content "$root/usage.jsonl" '{}'
    Set-Content "$root/bake-audit.jsonl" '{}'
    Set-Content "$root/firm-profiles/f.json" '{}'
    Set-Content "$root/shared-parameters.txt" 'p'
    Set-Content "$root/.migrated-from-bimwright" 'm'
    Set-Content "$root/revit-mcp.log" 'l'
    Set-Content "$root/mcp-calls.jsonl" 'l'
    Set-Content "$root/send-code-journal.jsonl" 'l'
    Set-Content "$root/logs/a.log" 'l'
    Set-Content "$root/rvt/server/0.6.2/rvt-mcp.exe" 'exe'
    Set-Content "$root/revit-2027.json" '{}'
    Set-Content "$root/spill/x.json" '{}'
    Set-Content "$root/journal/j.json" '{}'
    Set-Content "$root/captures/c.png" 'png'
    Set-Content "$root/mcp-calls.version" 'v'
    Set-Content "$root/notes.txt" 'n'
    Set-Content "$root/custom/x.txt" 'x'
    return $root
}
function Invoke-Step4 {
    [CmdletBinding(SupportsShouldProcess=$true)]
    param([string]$Root, [switch]$KeepLogs, [switch]$Purge)
    Invoke-Step4-Discovery -Root $Root
}
function Assert-RootContents($Root, [string[]]$Kept) {
    $actual = @((Get-ChildItem -LiteralPath $Root -Force | Select-Object -ExpandProperty Name) | Sort-Object)
    $expected = @($Kept | Sort-Object)
    Assert (($actual -join ',') -eq ($expected -join ',')) ("Root contents differ. Expected [{0}] got [{1}]" -f ($expected -join ','), ($actual -join ','))
}
try {
    Test 'Uninstall default removes only server copies, discovery and spill' {
        $root = New-RootFixture
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root
        Assert-RootContents $root $keptNames
        Assert ($script:handled -contains 'step4-discovery') 'step4 not reported'
        Assert ($script:handled -notcontains 'step5-toolbaker (contained)') 'Kept ToolBaker data reported as removed'
    }
    Test 'Uninstall -KeepLogs matches default (logs already kept)' {
        $root = New-RootFixture
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root -KeepLogs
        Assert-RootContents $root $keptNames
    }
    Test 'Uninstall -Purge removes the whole root including ToolBaker data' {
        $root = New-RootFixture
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root -Purge
        Assert (-not (Test-Path $root)) 'Root still present after -Purge'
        Assert ($script:handled -contains 'step5-toolbaker (contained)') 'Removed ToolBaker data not reported'
    }
    Test 'Uninstall -Purge -KeepLogs keeps only logs' {
        $root = New-RootFixture
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root -Purge -KeepLogs
        Assert-RootContents $root (@('logs','revit-mcp.log','mcp-calls.jsonl','send-code-journal.jsonl','usage.jsonl','bake-audit.jsonl'))
        Assert ($script:handled -contains 'step5-toolbaker (contained)') 'Removed ToolBaker data not reported'
    }
    Test 'Uninstall -WhatIf removes nothing' {
        $root = New-RootFixture
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root -WhatIf
        Assert-RootContents $root ($keptNames + $removableNames)
    }
    Test 'Uninstall missing root skips step4' {
        $root = Join-Path $testRoot 'missing-root'
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root
        Assert ($script:skipped -contains 'step4-discovery') 'Missing root not reported as skipped'
        Assert (-not (Test-Path $root)) 'Missing root was created'
    }
    Test 'Uninstall root holding only removable items is removed' {
        $root = Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path "$root/rvt/server/0.6.2", "$root/spill" -Force | Out-Null
        Set-Content "$root/rvt/server/0.6.2/rvt-mcp.exe" 'exe'
        Set-Content "$root/spill/x.json" '{}'
        Set-Content "$root/revit-2027.json" '{}'
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root
        Assert (-not (Test-Path $root)) 'Empty root still present'
    }
    Test 'Uninstall -Purge -KeepLogs deletes exactly the named entry' {
        $root = Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($root) | Out-Null
        [IO.File]::WriteAllText((Join-Path $root 'a.log'), 'l')
        [IO.File]::WriteAllText((Join-Path $root 'a[.]log'), 'x')
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root -Purge -KeepLogs
        Assert (Test-Path -LiteralPath (Join-Path $root 'a.log')) 'a.log was deleted'
        Assert (-not (Test-Path -LiteralPath (Join-Path $root 'a[.]log'))) 'a[.]log survived'
    }
    Test 'Uninstall handles a root path containing brackets' {
        $root = Join-Path $testRoot 'root[1]'
        [IO.Directory]::CreateDirectory((Join-Path $root 'rvt\server\0.6.2')) | Out-Null
        [IO.File]::WriteAllText((Join-Path $root 'rvt\server\0.6.2\rvt-mcp.exe'), 'exe')
        [IO.File]::WriteAllText((Join-Path $root 'revit-2027.json'), '{}')
        [IO.File]::WriteAllText((Join-Path $root 'bake.db'), 'db')
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root
        Assert (-not (Test-Path -LiteralPath (Join-Path $root 'rvt'))) 'rvt not removed'
        Assert (-not (Test-Path -LiteralPath (Join-Path $root 'revit-2027.json'))) 'discovery file not removed'
        Assert (Test-Path -LiteralPath (Join-Path $root 'bake.db')) 'bake.db not kept'
    }
    Test 'Uninstall keeps a server copy that is still running and removes the rest' {
        $root = New-RootFixture
        Copy-Item -LiteralPath "$env:WINDIR\System32\PING.EXE" -Destination "$root/rvt/server/0.6.2/rvt-mcp.exe" -Force
        $proc = Start-Process -FilePath "$root\rvt\server\0.6.2\rvt-mcp.exe" -ArgumentList '-n','30','127.0.0.1' -WindowStyle Hidden -PassThru
        try {
            Start-Sleep -Milliseconds 500
            $script:handled=@(); $script:skipped=@(); $script:failed=@()
            Invoke-Step4 -Root $root 3>$null 6>$null
            Assert (Test-Path -LiteralPath "$root/rvt/server/0.6.2/rvt-mcp.exe") 'Running server copy was deleted'
            Assert ($script:failed -contains 'step4-discovery') 'In-use copy not reported as failure'
            Assert (-not (Test-Path -LiteralPath "$root/spill") -and -not (Test-Path -LiteralPath "$root/revit-2027.json")) 'Other removables were not removed'
            Assert (Test-Path -LiteralPath "$root/rvtmcp.config.json") 'Personal data removed'
        } finally { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; $null = $proc.WaitForExit(5000) }
        Start-Sleep -Milliseconds 300
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root 6>$null
        Assert (-not (Test-Path -LiteralPath "$root/rvt")) 'Server copy not removed after the client exited'
    }
    Test 'Uninstall -Purge keeps the root while a server copy is still running' {
        $root = New-RootFixture
        Copy-Item -LiteralPath "$env:WINDIR\System32\PING.EXE" -Destination "$root/rvt/server/0.6.2/rvt-mcp.exe" -Force
        $proc = Start-Process -FilePath "$root\rvt\server\0.6.2\rvt-mcp.exe" -ArgumentList '-n','30','127.0.0.1' -WindowStyle Hidden -PassThru
        try {
            Start-Sleep -Milliseconds 500
            $script:handled=@(); $script:skipped=@(); $script:failed=@()
            Invoke-Step4 -Root $root -Purge 3>$null 6>$null
            Assert (Test-Path -LiteralPath "$root/rvt/server/0.6.2/rvt-mcp.exe") 'Running server copy was deleted'
            Assert (-not (Test-Path -LiteralPath "$root/rvtmcp.config.json") -and -not (Test-Path -LiteralPath "$root/logs")) '-Purge did not remove the rest'
            Assert ($script:failed -contains 'step4-discovery') 'In-use copy not reported as failure'
        } finally { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; $null = $proc.WaitForExit(5000) }
    }
} finally {
    $env:USERPROFILE = $savedUserProfile
    $env:APPDATA = $savedAppData
    $env:LOCALAPPDATA = $savedLocalAppData
    $report = [ordered]@{powershell=$PSVersionTable.PSVersion.ToString();uninstallerSha256=(Get-FileHash $uninstaller -Algorithm SHA256).Hash;results=@($results.ToArray())}
    if ($ResultPath) { $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ResultPath -Encoding UTF8 }
    # Only remove the uniquely created test directory under the resolved temp root.
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath($TestRootParent).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
if (@($results | Where-Object { -not $_.passed }).Count) { throw 'Uninstaller regression tests failed' }
