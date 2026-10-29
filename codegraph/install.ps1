<#
.SYNOPSIS
    Builds, tests, publishes and installs codegraph-index and codegraph-mcp.

.DESCRIPTION
    Runs `dotnet build -warnaserror` and `dotnet test`, publishes both executables (framework-dependent,
    Release) into one folder, and prints the .mcp.json to put in each repo. Re-run it to upgrade: a running
    codegraph-mcp holds its files open, so close Claude Code sessions first or pass -Force to stop them.

.PARAMETER InstallDir
    Where the executables go. Default: C:\tools\codegraph (the path the README uses).

.PARAMETER SkipTests
    Publish without running the test suite.

.PARAMETER AddToPath
    Append InstallDir to the user PATH so `codegraph-index` works from any shell.

.PARAMETER Force
    Stop running codegraph-mcp / codegraph-index processes before overwriting them.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -InstallDir D:\tools\codegraph -AddToPath
#>
[CmdletBinding()]
param(
    [string] $InstallDir = 'C:\tools\codegraph',
    [switch] $SkipTests,
    [switch] $AddToPath,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)

function Step([string] $message) { Write-Host "`n==> $message" -ForegroundColor Cyan }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet SDK not found on PATH. Install .NET 10 from https://dotnet.microsoft.com/download'
}

Step 'Building (warnings are errors)'
dotnet build "$root\CodeGraph.slnx" -c Release -warnaserror --nologo
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

if (-not $SkipTests) {
    Step 'Running tests'
    dotnet test "$root\CodeGraph.slnx" -c Release --no-build --nologo
    if ($LASTEXITCODE -ne 0) { throw 'tests failed; nothing installed' }
}

# A running server keeps codegraph-mcp.exe and its DLLs locked, and the copy would fail half way.
$running = Get-Process -Name 'codegraph-mcp', 'codegraph-index' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [System.StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    if ($Force) {
        Step "Stopping $($running.Count) running codegraph process(es)"
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    } else {
        $list = ($running | ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" }) -join ', '
        throw "codegraph is running from $InstallDir ($list). Close the Claude Code sessions using it, or re-run with -Force."
    }
}

Step "Publishing to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
foreach ($project in 'src\CodeGraph.Indexer', 'src\CodeGraph.Mcp') {
    dotnet publish "$root\$project" -c Release --no-build -o $InstallDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish of $project failed" }
}

if ($AddToPath) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $parts = $userPath -split ';' | Where-Object { $_ }
    if ($parts -notcontains $InstallDir) {
        Step 'Adding to user PATH'
        [Environment]::SetEnvironmentVariable('Path', (($parts + $InstallDir) -join ';'), 'User')
        $env:Path = "$env:Path;$InstallDir"
        Write-Host 'Open a new shell for the PATH change to apply elsewhere.'
    }
}

$mcpExe = (Join-Path $InstallDir 'codegraph-mcp.exe') -replace '\\', '/'
$indexExe = Join-Path $InstallDir 'codegraph-index.exe'

Step 'Installed'
Write-Host "  $indexExe"
Write-Host "  $mcpExe"
Write-Host ''
Write-Host 'Index a solution (writes <worktree>/.codegraph/graph.db; add .codegraph/ to .gitignore):'
Write-Host "  & '$indexExe' C:\path\to\App.sln          # once, then incremental"
Write-Host "  & '$indexExe' C:\path\to\App.sln --watch  # keep it fresh while you work"
Write-Host ''
Write-Host 'Put this .mcp.json in the repo root; the server finds the graph from its working directory:'
Write-Host @"
{
  "mcpServers": {
    "codegraph": {
      "command": "$mcpExe"
    }
  }
}
"@
