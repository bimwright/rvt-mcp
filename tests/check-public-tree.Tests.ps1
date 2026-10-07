#Requires -Version 5.1
# Publication-path fixtures only; no source checkout writes or network calls.
[CmdletBinding()]
param([string]$ResultPath)
$ErrorActionPreference = 'Stop'
$checker = (Resolve-Path (Join-Path $PSScriptRoot '../scripts/check-public-tree.ps1')).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('rvt-public-tree-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$results = New-Object System.Collections.Generic.List[object]
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Test([string]$Name, [scriptblock]$Body) {
    try { & $Body; $results.Add([pscustomobject]@{name=$Name;passed=$true}); Write-Host "PASS $Name" }
    catch { $results.Add([pscustomobject]@{name=$Name;passed=$false;error=$_.Exception.Message}); Write-Host "FAIL $Name : $_" }
}
function New-GitFixture {
    $repo = Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
    & git init --quiet $repo
    if ($LASTEXITCODE -ne 0) { throw 'Could not create Git fixture' }
    return $repo
}
function Add-FixtureFile([string]$Repo, [string]$Path, [string]$Content = 'fixture') {
    $file = Join-Path $Repo $Path
    New-Item -ItemType Directory -Path (Split-Path -Parent $file) -Force | Out-Null
    Set-Content -LiteralPath $file $Content
}
function Expect-Rejection([string]$Repo, [string]$Path) {
    $message = $null
    try { & $checker -RepoRoot $Repo | Out-Null } catch { $message=$_.Exception.Message }
    Assert ($message -and $message.Contains($Path)) "Did not reject $Path"
}
try {
    Test 'Source, prompt assets and approved product documentation are allowed' {
        $repo = New-GitFixture
        foreach ($path in @('README.md','docs/install.md','src/server/Program.cs','src/server/Prompts/change.md','tests/RvtMcp.Tests/Golden/response-size-scoped-commands.txt')) { Add-FixtureFile $repo $path }
        & $checker -RepoRoot $repo
    }
    Test 'Sanitized public benchmark and testing records are allowed' {
        $repo = New-GitFixture
        foreach ($path in @('docs/benchmarks/example.md','docs/benchmarks/example.json','docs/testing/acceptance.md')) { Add-FixtureFile $repo $path }
        & $checker -RepoRoot $repo
    }
    Test 'Private maintainer paths are rejected for any file type' {
        foreach ($path in @('internal-docs/notes.md','analysis/probe.cs','docs/design/plan.md','docs/reviews/result.json','.agents/settings.json','runs/live.jsonl','docs/benchmarks/runs/raw.jsonl')) {
            $repo = New-GitFixture
            Add-FixtureFile $repo $path
            Expect-Rejection $repo $path
        }
    }
    Test 'Unreviewed root notes and docs are rejected' {
        foreach ($path in @('release-plan.md','docs/handoff.md','docs/raw.json')) {
            $repo = New-GitFixture
            Add-FixtureFile $repo $path
            Expect-Rejection $repo $path
        }
    }
    Test 'Tracked private files are rejected, even when the path is ignored' {
        $repo = New-GitFixture
        Add-FixtureFile $repo '.gitignore' 'artifacts/'
        Add-FixtureFile $repo 'artifacts/raw.jsonl'
        & $checker -RepoRoot $repo
        & git -C $repo add -f artifacts/raw.jsonl
        if ($LASTEXITCODE -ne 0) { throw 'Could not stage fixture file' }
        Expect-Rejection $repo 'artifacts/raw.jsonl'
    }
    Test 'Pending deletion is not treated as a file that will ship' {
        $repo = New-GitFixture
        Add-FixtureFile $repo 'docs/handoff.md'
        & git -C $repo add docs/handoff.md
        if ($LASTEXITCODE -ne 0) { throw 'Could not stage fixture file' }
        Remove-Item -LiteralPath (Join-Path $repo 'docs/handoff.md')
        & $checker -RepoRoot $repo
    }
    Test 'A folder outside a Git checkout fails closed' {
        $root = Join-Path $testRoot 'no-git'
        New-Item -ItemType Directory -Path $root | Out-Null
        $message = $null
        try { & $checker -RepoRoot $root | Out-Null } catch { $message=$_.Exception.Message }
        Assert ($message -like '*Git checkout root*') 'Non-repository path was accepted'
    }
} finally {
    $report = [ordered]@{powershell=$PSVersionTable.PSVersion.ToString();checkerSha256=(Get-FileHash -LiteralPath $checker).Hash;results=@($results.ToArray())}
    if ($ResultPath) { $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ResultPath -Encoding UTF8 }
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
if (@($results | Where-Object { -not $_.passed }).Count) { throw 'Public tree regression tests failed' }
