<#
.SYNOPSIS
    Builds, tests and installs codegraph-index and codegraph-mcp, then sets up repos to use them.

.DESCRIPTION
    Runs `dotnet build -warnaserror` and `dotnet test`, publishes both executables ReadyToRun for this machine's
    platform into one folder, and runs `codegraph-index init` for each repo given (asked for when none is): that
    writes the codegraph server into the repo's .mcp.json, adds .codegraph/ to .gitignore and builds the graph.
    Re-run to upgrade: a running codegraph-mcp holds its files open, so close Claude Code sessions first or pass
    -Force to stop them. Works in Windows PowerShell 5.1 and PowerShell 7 on Windows, macOS and Linux.

.PARAMETER Repo
    Repo folders or .sln / .slnx / .csproj files to set up. Asked for when none is given.

.PARAMETER InstallDir
    Where the executables go. Default: C:\tools\codegraph on Windows, ~/.codegraph/bin elsewhere.

.PARAMETER AddToPath
    Add InstallDir to the user PATH (Windows) or to ~/.bashrc and ~/.zshrc, so `codegraph-index` works anywhere.

.PARAMETER Live
    The MCP server starts the live daemon itself on its first call (CODEGRAPH_DAEMON=auto in .mcp.json).

.PARAMETER SkipTests
    Publish without running the test suite.

.PARAMETER Force
    Stop running codegraph-mcp / codegraph-index processes before overwriting them.

.EXAMPLE
    .\install.ps1 C:\dev\App -AddToPath
    .\install.ps1 -InstallDir D:\tools\codegraph -Repo C:\dev\App\App.sln, C:\dev\Other -Live
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]] $Repo = @(),
    [string] $InstallDir,
    [switch] $AddToPath,
    [switch] $Live,
    [switch] $SkipTests,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
# $IsWindows does not exist in Windows PowerShell 5.1, which only runs on Windows.
$onWindows = $PSVersionTable.PSEdition -eq 'Desktop' -or $IsWindows
$exe = if ($onWindows) { '.exe' } else { '' }
if (-not $InstallDir) {
    $InstallDir = if ($onWindows) { 'C:\tools\codegraph' } else { Join-Path $HOME '.codegraph/bin' }
}
$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)
$indexExe = Join-Path $InstallDir "codegraph-index$exe"
$mcpExe = Join-Path $InstallDir "codegraph-mcp$exe"

function Step([string] $message) { Write-Host "`n==> $message" -ForegroundColor Cyan }

# Runs an executable with DOTNET_ROOT set to $dotnetRoot (or unset), restoring the caller's value after.
function Invoke-WithRoot([string] $dotnetRoot, [string] $file, [string[]] $arguments) {
    $saved = $env:DOTNET_ROOT
    $env:DOTNET_ROOT = $dotnetRoot
    try { & $file @arguments } finally { $env:DOTNET_ROOT = $saved }
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw 'dotnet SDK not found on PATH. Install .NET 10 from https://dotnet.microsoft.com/download'
}

Step 'Building (warnings are errors)'
dotnet build (Join-Path $root 'CodeGraph.slnx') -c Release -warnaserror --nologo
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

if (-not $SkipTests) {
    Step 'Running tests'
    dotnet test (Join-Path $root 'CodeGraph.slnx') -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'tests failed; nothing installed' }
}

# A running server keeps codegraph-mcp and its DLLs locked (Windows) or open (elsewhere); overwriting is unsafe.
$running = Get-Process -Name 'codegraph-mcp', 'codegraph-index' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [System.StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    if ($Force) {
        Step "Stopping $(@($running).Count) running codegraph process(es)"
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    } else {
        $list = ($running | ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" }) -join ', '
        throw "codegraph is running from $InstallDir ($list). Close the Claude Code sessions using it, or re-run with -Force."
    }
}

# ReadyToRun precompiles the assemblies for this platform: indexing runs about a quarter faster and the server
# starts sooner (BENCHMARKS.md, round 8), and only this platform's DuckDB library is copied.
$rid = (dotnet --info | Select-String -Pattern '^\s*RID:\s*(\S+)' | Select-Object -First 1).Matches.Groups[1].Value
if (-not $rid) { throw 'could not read the runtime identifier from dotnet --info' }
Step "Publishing to $InstallDir ($rid, ReadyToRun)"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
foreach ($project in 'src/CodeGraph.Indexer', 'src/CodeGraph.Mcp') {
    dotnet publish (Join-Path $root $project) -c Release -r $rid --self-contained false -p:PublishReadyToRun=true -o $InstallDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish of $project failed" }
}

# The executables find .NET through DOTNET_ROOT or the machine-wide install, not through PATH. With a user-local
# or version-managed dotnet the machine-wide one can be missing or lack ASP.NET Core. init writes DOTNET_ROOT
# into .mcp.json when the server needs it; here the indexer gets it for this script's own runs.
Step 'Checking the executables start'
$source = if ($dotnet.Source) { (Get-Item $dotnet.Source) } else { $null }
if ($source -and $source.LinkType -and $source.Target) {
    $source = Get-Item ([System.IO.Path]::GetFullPath((Join-Path $source.DirectoryName @($source.Target)[0])))
}
$dotnetRoot = if ($source) { $source.DirectoryName } else { $null }
$rootFor = $null
Invoke-WithRoot $null $indexExe @('--version') *> $null
$indexOk = $LASTEXITCODE -eq 0
Invoke-WithRoot $null $mcpExe @('--version') *> $null
if ($indexOk -and $LASTEXITCODE -eq 0) {
    Write-Host "ok: codegraph $(& $mcpExe --version)"
} else {
    Invoke-WithRoot $dotnetRoot $mcpExe @('--version') *> $null
    if ($LASTEXITCODE -eq 0) {
        $rootFor = $dotnetRoot
        Write-Host "ok with DOTNET_ROOT=$dotnetRoot (the default .NET location lacks a runtime codegraph needs)"
    } else {
        Write-Warning "codegraph-mcp does not start, even with DOTNET_ROOT=$dotnetRoot. Run it by hand to see why."
    }
}

if ($AddToPath) {
    if ($onWindows) {
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        $parts = @($userPath -split ';' | Where-Object { $_ })
        if ($parts -notcontains $InstallDir) {
            Step 'Adding to user PATH'
            [Environment]::SetEnvironmentVariable('Path', (($parts + $InstallDir) -join ';'), 'User')
            $env:Path = "$env:Path;$InstallDir"
            Write-Host 'Open a new shell for the PATH change to apply elsewhere.'
        }
    } elseif (($env:PATH -split ':') -notcontains $InstallDir) {
        # The same lines install.sh writes, so either uninstall script removes them.
        Step 'Adding to PATH'
        foreach ($rc in (Join-Path $HOME '.bashrc'), (Join-Path $HOME '.zshrc')) {
            if (Test-Path $rc) { Add-Content -Path $rc -Value "`n# codegraph`nexport PATH=`"`$PATH:$InstallDir`"" }
        }
        Write-Host 'Open a new shell for the PATH change to apply.'
    }
}

if ($Repo.Count -eq 0 -and [Environment]::UserInteractive -and -not [Console]::IsInputRedirected) {
    $answer = Read-Host "`nSet up a repo now? Path to a repo folder or solution (Enter to skip)"
    if ($answer.Trim()) { $Repo = @($answer.Trim().Trim('"')) }
}

$failed = 0
foreach ($target in $Repo) {
    Step "Setting up $target"
    $initArgs = @('init', $target)
    if ($Live) { $initArgs += '--live' }
    Invoke-WithRoot $(if ($rootFor) { $rootFor } else { $env:DOTNET_ROOT }) $indexExe $initArgs
    if ($LASTEXITCODE -ne 0) { $failed++ }
}

Step 'Installed'
Write-Host "  $indexExe"
Write-Host "  $mcpExe"
$call = if ($onWindows) { "& '$indexExe'" } else { $indexExe }
Write-Host ''
if ($rootFor) {
    Write-Host "Run by hand, the executables need DOTNET_ROOT=$rootFor in the environment (.mcp.json entries carry it already)."
}
Write-Host 'Set up a repo (writes .mcp.json and .gitignore at its root, then builds the graph):'
Write-Host "  $call init <repo-or-solution> [--live]"
Write-Host 'Keep a graph fresh while you work:'
Write-Host "  $call <App.sln> --watch"
if ($failed -gt 0) { throw "$failed repo setup(s) failed; see above" }
