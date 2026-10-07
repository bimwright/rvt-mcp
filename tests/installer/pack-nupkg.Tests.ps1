#Requires -Version 5.1
# Exercise the production pack script with a fake dotnet command and disposable files.
# Never install a global tool, invoke a real pack/push, or read a NuGet API key.
[CmdletBinding()]
param([string]$ResultPath)
$ErrorActionPreference = 'Stop'
$packer = (Resolve-Path (Join-Path $PSScriptRoot '../../scripts/publish-nupkg.ps1')).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('rvt-pack-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$results = New-Object System.Collections.Generic.List[object]
$savedExitCode = $global:LASTEXITCODE
$state = @{ mode = 'success'; calls = @(); payload = 'first-build' }
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Test([string]$Name, [scriptblock]$Body) {
    try { & $Body; $results.Add([pscustomobject]@{name=$Name;passed=$true}); Write-Host "PASS $Name" }
    catch { $results.Add([pscustomobject]@{name=$Name;passed=$false;error=$_.Exception.Message}); Write-Host "FAIL $Name : $_" }
}
function New-PackFixture {
    $root = Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path "$root/src/server", "$root/artifacts" -Force | Out-Null
    Set-Content -LiteralPath "$root/src/server/RvtMcp.Server.csproj" '<Project><PropertyGroup><PackageId>RvtMcp.Server</PackageId><Version>1.1.0</Version></PropertyGroup></Project>'
    Set-Content -LiteralPath "$root/artifacts/RvtMcp.Server.9.9.9.nupkg" 'unrelated-build'
    # A stale, newer-looking artifact must never win over the version just packed.
    (Get-Item -LiteralPath "$root/artifacts/RvtMcp.Server.9.9.9.nupkg").LastWriteTimeUtc = [DateTime]::UtcNow.AddHours(1)
    return $root
}
function dotnet {
    $state.calls += ,@($args)
    if ($args[0] -ne 'pack') { throw 'Unexpected dotnet invocation: publishing is not allowed in this fixture' }
    if ($state.mode -eq 'fail') { $global:LASTEXITCODE = 12; return }
    $outIndex = [Array]::IndexOf($args, '--output')
    Assert ($outIndex -ge 0) 'Pack did not supply an isolated output directory'
    if ($state.mode -eq 'success') {
        Set-Content -LiteralPath (Join-Path $args[$outIndex + 1] 'RvtMcp.Server.1.1.0.nupkg') $state.payload
    }
    $global:LASTEXITCODE = 0
}
function Assert-Checksum([string]$Root) {
    $package = Join-Path $Root 'artifacts/RvtMcp.Server.1.1.0.nupkg'
    Assert (Test-Path -LiteralPath "$package.sha256") 'NuGet checksum sidecar is missing'
    $expected = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() + '  RvtMcp.Server.1.1.0.nupkg'
    Assert ((Get-Content -LiteralPath "$package.sha256" -Raw).Trim() -ceq $expected) 'Checksum does not match the exact versioned package'
}
try {
    Test 'Dry pack hashes the exact csproj version, not the newest leftover artifact' {
        $root = New-PackFixture
        $state.mode='success'; $state.calls=@(); $state.payload='first-build'
        & $packer -RepoRoot $root -ApiKey ''
        Assert-Checksum $root
        Assert (-not (Test-Path -LiteralPath "$root/artifacts/RvtMcp.Server.9.9.9.nupkg.sha256")) 'Hashed the unrelated artifact'
        Assert ($state.calls.Count -eq 1 -and $state.calls[0][0] -eq 'pack') 'Dry pack invoked something other than pack'
    }
    Test 'Repacking refreshes the package checksum' {
        $root = New-PackFixture
        $state.mode='success'; $state.payload='first-build'
        & $packer -RepoRoot $root -ApiKey ''
        Assert-Checksum $root
        $first = Get-Content -LiteralPath "$root/artifacts/RvtMcp.Server.1.1.0.nupkg.sha256" -Raw
        $state.payload='second-build'
        & $packer -RepoRoot $root -ApiKey ''
        Assert-Checksum $root
        Assert ((Get-Content -LiteralPath "$root/artifacts/RvtMcp.Server.1.1.0.nupkg.sha256" -Raw) -ne $first) 'Checksum was not refreshed'
    }
    Test 'Pack failure does not create a misleading checksum' {
        $root = New-PackFixture
        $state.mode='fail'
        $message = $null
        try { & $packer -RepoRoot $root -ApiKey '' } catch { $message=$_.Exception.Message }
        Assert ($message -like 'dotnet pack failed:*') 'Pack failure did not propagate'
        Assert (@(Get-ChildItem -LiteralPath "$root/artifacts" -Filter '*.sha256').Count -eq 0) 'Pack failure emitted a checksum'
    }
    Test 'Missing expected version refuses a stale package even after pack exit zero' {
        $root = New-PackFixture
        $state.mode='omit'
        $message = $null
        try { & $packer -RepoRoot $root -ApiKey '' } catch { $message=$_.Exception.Message }
        Assert ($message -like '*RvtMcp.Server.1.1.0.nupkg*') 'Missing expected package was not rejected'
        Assert (@(Get-ChildItem -LiteralPath "$root/artifacts" -Filter '*.sha256').Count -eq 0) 'Hashed a stale package'
    }
} finally {
    $global:LASTEXITCODE = $savedExitCode
    $report = [ordered]@{powershell=$PSVersionTable.PSVersion.ToString();packerSha256=(Get-FileHash -LiteralPath $packer).Hash;results=@($results.ToArray())}
    if ($ResultPath) { $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ResultPath -Encoding UTF8 }
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
if (@($results | Where-Object { -not $_.passed }).Count) { throw 'NuGet packaging regression tests failed' }
