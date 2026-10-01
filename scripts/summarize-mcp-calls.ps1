# PowerShell script to summarize rvt-mcp local call usage from mcp-calls.jsonl.
# Usage:
#   powershell -File summarize-mcp-calls.ps1

$LogDir = Join-Path $env:LOCALAPPDATA "Bimwright\rvt-mcp"
$LogPath = Join-Path $LogDir "mcp-calls.jsonl"

if (-not (Test-Path $LogPath)) {
    Write-Warning "No mcp-calls.jsonl found at $LogPath"
    Exit
}

Write-Host "Analyzing local MCP calls from $LogPath..." -ForegroundColor Cyan

$Calls = @(Get-Content $LogPath | ForEach-Object {
    try {
        ConvertFrom-Json $_ -ErrorAction Stop
    } catch {
        # Skip malformed lines
    }
})

if ($Calls.Count -eq 0) {
    Write-Host "No valid calls found."
    Exit
}

$TotalCalls = $Calls.Count
$SuccessCalls = @($Calls | Where-Object { $_.success -eq $true }).Count
$FailedCalls = $TotalCalls - $SuccessCalls
$FailRate = ($FailedCalls / $TotalCalls) * 100

Write-Host ""
Write-Host "Summary Statistics:" -ForegroundColor Yellow
Write-Host "  Total Calls:      $TotalCalls"
Write-Host "  Success Calls:    $SuccessCalls"
Write-Host "  Failed Calls:     $FailedCalls"
Write-Host "  Overall Fail Rate: $($FailRate.ToString("F2"))%"

Write-Host ""
Write-Host "Top Tools by Call Count:" -ForegroundColor Yellow
$Calls | Group-Object tool | Sort-Object Count -Descending | Select-Object -First 10 | ForEach-Object {
    $ToolName = $_.Name
    $Count = $_.Count
    $Pct = ($Count / $TotalCalls) * 100
    $Failures = @($_.Group | Where-Object { $_.success -eq $false }).Count
    Write-Host "  - $($ToolName.PadRight(30)): $Count calls ($($Pct.ToString("F1"))%), $Failures failures"
}

Write-Host ""
Write-Host "Top Failed Tools:" -ForegroundColor Yellow
$Calls | Where-Object { $_.success -eq $false } | Group-Object tool | Sort-Object Count -Descending | Select-Object -First 5 | ForEach-Object {
    $ToolName = $_.Name
    $Count = $_.Count
    Write-Host "  - $($ToolName.PadRight(30)): $Count failures"
}

Write-Host ""
Write-Host "Session Counts:" -ForegroundColor Yellow
$Sessions = $Calls | Group-Object session_id
Write-Host "  - Total unique sessions: $($Sessions.Count)"
Write-Host "  - Average calls per session: $(($TotalCalls / $Sessions.Count).ToString("F1"))"
