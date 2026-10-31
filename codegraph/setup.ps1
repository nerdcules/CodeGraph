<#
.SYNOPSIS
    Source checkout setup: pull the latest CodeGraph, wire a repo's .mcp.json to `dotnet run`, build the graph.

.DESCRIPTION
    The no-publish alternative to install.ps1. Nothing is copied anywhere: the MCP server runs from this
    checkout with `dotnet run --no-build`, so a `git pull` plus a build is an upgrade. The script

      1. fetches origin and, if this checkout is behind, asks whether to pull (fast-forward only;
         a dirty working tree is never pulled over),
      2. builds Release with warnings as errors,
      3. writes (or updates) the .mcp.json at the target repo's root with a `codegraph` server entry,
      4. indexes the solution with `dotnet run --project src/CodeGraph.Indexer`.

.PARAMETER Solution
    The .sln / .slnx / .csproj to index. Prompted for when omitted.

.PARAMETER ConfigDir
    Where .mcp.json goes. Default: the git worktree root of the solution (the solution's folder outside a repo).

.PARAMETER Yes
    Answer every question with its default: pull if behind, write the config, index.

.PARAMETER NoPull
    Skip the fetch and pull.

.PARAMETER Full
    Rebuild the graph from scratch instead of incrementally.

.EXAMPLE
    .\setup.ps1 -Solution C:\dev\App\App.sln
    .\setup.ps1 -Solution C:\dev\App\App.sln -Yes -Full
#>
[CmdletBinding()]
param(
    [string] $Solution,
    [string] $ConfigDir,
    [switch] $Yes,
    [switch] $NoPull,
    [switch] $Full
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Step([string] $message) { Write-Host "`n==> $message" -ForegroundColor Cyan }

function Confirm-Step([string] $question, [bool] $default = $true) {
    if ($Yes) { return $default }
    $hint = if ($default) { '[Y/n]' } else { '[y/N]' }
    $answer = Read-Host "$question $hint"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $default }
    return $answer.Trim().ToLowerInvariant() -in @('y', 'yes')
}

foreach ($tool in 'dotnet', 'git') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool not found on PATH" }
}

# ---- 1. pull -------------------------------------------------------------------------------------------
if (-not $NoPull) {
    Step 'Checking origin for a newer CodeGraph'
    git -C $root fetch origin --quiet
    if ($LASTEXITCODE -ne 0) { throw 'git fetch failed' }

    $branch = (git -C $root rev-parse --abbrev-ref HEAD).Trim()
    $upstream = (git -C $root rev-parse --abbrev-ref --symbolic-full-name '@{upstream}' 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $upstream) { $upstream = 'origin/HEAD' }
    $behind = [int](git -C $root rev-list --count "HEAD..$upstream")
    $dirty = (git -C $root status --porcelain --untracked-files=no)

    if ($behind -eq 0) {
        Write-Host "$branch is up to date with $upstream"
    } elseif ($dirty) {
        Write-Warning "$branch is $behind commit(s) behind $upstream but has local changes; not pulling. Commit or stash, then re-run."
    } elseif (Confirm-Step "$branch is $behind commit(s) behind $upstream. Pull?") {
        git -C $root pull --ff-only --quiet
        if ($LASTEXITCODE -ne 0) { throw 'git pull --ff-only failed; the branch has diverged from origin' }
        Write-Host "now at $(git -C $root rev-parse --short HEAD)"
    }
}

# ---- 2. build ------------------------------------------------------------------------------------------
Step 'Building (warnings are errors)'
dotnet build "$root\CodeGraph.slnx" -c Release -warnaserror --nologo
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

# ---- 3. .mcp.json --------------------------------------------------------------------------------------
if (-not $Solution) {
    if ($Yes) { throw 'pass -Solution <path> when running with -Yes' }
    $Solution = Read-Host 'Solution or project to index (.sln / .slnx / .csproj)'
}
$Solution = [System.IO.Path]::GetFullPath($Solution.Trim('"'))
if (-not (Test-Path $Solution -PathType Leaf)) { throw "not found: $Solution" }

$solutionDir = Split-Path $Solution -Parent
if (-not $ConfigDir) {
    $ConfigDir = (git -C $solutionDir rev-parse --show-toplevel 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $ConfigDir) { $ConfigDir = $solutionDir }
}
$ConfigDir = [System.IO.Path]::GetFullPath($ConfigDir)
$configPath = Join-Path $ConfigDir '.mcp.json'
$mcpProject = (Join-Path $root 'src\CodeGraph.Mcp') -replace '\\', '/'

$entry = [ordered]@{
    command = 'dotnet'
    args    = @('run', '--project', $mcpProject, '-c', 'Release', '--no-build')
}

Step "MCP config: $configPath"
$config = [ordered]@{ mcpServers = [ordered]@{} }
if (Test-Path $configPath) {
    $existing = Get-Content $configPath -Raw | ConvertFrom-Json -AsHashtable
    if ($existing) { $config = $existing }
    if (-not $config.ContainsKey('mcpServers')) { $config['mcpServers'] = @{} }
}
$current = $config['mcpServers']['codegraph']
$changed = -not $current -or ($current | ConvertTo-Json -Compress) -ne ($entry | ConvertTo-Json -Compress)
if (-not $changed) {
    Write-Host 'codegraph server entry already up to date'
} elseif (Confirm-Step "$(if ($current) { 'Replace' } else { 'Add' }) the codegraph server entry in $configPath?") {
    $config['mcpServers']['codegraph'] = $entry
    $json = $config | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($configPath, $json + "`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host $json
}

# ---- 4. index ------------------------------------------------------------------------------------------
Step "Indexing $Solution"
$indexArgs = @('run', '--project', (Join-Path $root 'src\CodeGraph.Indexer'), '-c', 'Release', '--no-build', '--', $Solution)
if ($Full) { $indexArgs += '--full' }
& dotnet @indexArgs
if ($LASTEXITCODE -ne 0) { throw 'indexing failed' }

Write-Host @"

Done. Claude Code picks up $configPath on its next start in that repo.
Keep the graph fresh while you work:
  dotnet run --project "$($root -replace '\\', '/')/src/CodeGraph.Indexer" -c Release --no-build -- "$Solution" --watch
Upgrade later by re-running this script.
"@
