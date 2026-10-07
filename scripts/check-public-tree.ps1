#Requires -Version 5.1
# Path-only publication guard; content/secret review remains a separate requirement.
[CmdletBinding()]
param([string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot '.git'))) { throw 'RepoRoot must be a Git checkout root.' }
$publicDocs = @(
    'AGENTS.md', 'ARCHITECTURE.md', 'CHANGELOG.md', 'CLAUDE.md',
    'CODE_OF_CONDUCT.md', 'CONTRIBUTING.md', 'SECURITY.md', 'THIRD_PARTY_NOTICES.md',
    'README.md', 'README.vi.md', 'README.ja.md', 'README.zh-CN.md',
    'docs/bake.md', 'docs/change-survey.md', 'docs/change-tracking.md',
    'docs/firm-profiles/README.md', 'docs/install.md', 'docs/localization.md',
    'docs/mcp-client-wiring.md', 'docs/mcp-config-claude-clients.md',
    'docs/mcp-config-codex.md', 'docs/mcp-config-opencode-kilo.md',
    'docs/placement-and-mep-contracts.md', 'docs/send-code.md', 'docs/stairs-workflow.md',
    'src/server/Prompts/change.md', 'src/server/Prompts/drawing_layout.md',
    'src/server/Prompts/getting_started.md', 'src/server/Prompts/model_audit.md',
    'src/server/Prompts/pre_issue_check.md', 'src/server/Prompts/stairs.md',
    'tests/RvtMcp.Tests/README.md', 'tests/RvtMcp.Settings.Tests/README.md',
    'tests/RvtMcp.Toast.Tests/README.md', 'tests/RvtMcp.Tests/Golden/response-size-scoped-commands.txt'
)
$privatePath = '(^|/)(\.(artifacts[^/]*|worktrees|claude|cursor|kilo|agents|openclaude)|internal-docs|analysis|artifacts|runs|spikes)(/|$)|^docs/(superpowers|design|reviews|research-archive|archive)/|(^|/)codemap\.md$'
$docExtension = '\.(md|mdx|markdown|rst|txt|canvas|docx?|pdf)$'
$paths = @(& git -c core.quotepath=false -C $RepoRoot ls-files --cached --others --exclude-standard --deduplicate)
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate the public Git tree.' }
$violations = @(
    foreach ($path in $paths) {
        # Pending deletions remain indexed until staged; check files that would actually ship.
        if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot $path))) { continue }
        # Sanitized product test/benchmark records are public, not maintainer raw evidence.
        $publicRecord = $path -match '^docs/(benchmarks|testing)/[^/]+\.(md|json)$'
        $approvedDoc = $path -in $publicDocs -or $publicRecord
        if ($path -match $privatePath -or
            ($path -match $docExtension -and -not $approvedDoc) -or
            ($path.StartsWith('docs/', [StringComparison]::OrdinalIgnoreCase) -and -not $approvedDoc)) {
            $path
        }
    }
)
if ($violations.Count) {
    throw "Files need public-content review or relocation to private documentation:`n$($violations -join "`n")"
}
'Public tree check passed. This path check is not a secret scanner, content approval or history audit.'
