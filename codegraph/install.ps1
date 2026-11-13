<#
.SYNOPSIS
    Installs codegraph-index and codegraph-mcp from the latest GitHub release, then sets up repos to use them.

.DESCRIPTION
    Downloads the release archive for this platform (it carries its own .NET runtime, so nothing has to be built
    or installed first), unpacks it into one folder, and runs `codegraph-index init` for each repo given (asked
    for when none is): that writes the codegraph server into the repo's .mcp.json, adds .codegraph/ to .gitignore
    and builds the graph. With -FromSource, builds and publishes this checkout instead (needs the .NET 10 SDK).
    Re-run to upgrade: a running codegraph-mcp holds its files open, so close Claude Code sessions first or pass
    -Force to stop them. Works in Windows PowerShell 5.1 and PowerShell 7 on Windows, macOS and Linux.

    One line, without a checkout:
      & ([scriptblock]::Create((irm https://raw.githubusercontent.com/nerdcules/CodeGraph/main/codegraph/install.ps1))) C:\dev\App -AddToPath

.PARAMETER Repo
    Repo folders or .sln / .slnx / .csproj files to set up. Asked for when none is given.

.PARAMETER Version
    A release tag to install, such as v1.0.0. Default: the latest release.

.PARAMETER InstallDir
    Where the executables go. Default: C:\tools\codegraph on Windows, ~/.codegraph/bin elsewhere.

.PARAMETER AddToPath
    Add InstallDir to the user PATH (Windows) or to ~/.bashrc and ~/.zshrc, so `codegraph-index` works anywhere.

.PARAMETER Live
    The MCP server starts the live daemon itself on its first call (CODEGRAPH_DAEMON=auto in .mcp.json).

.PARAMETER ObsidianVault
    Also install the Obsidian plugin into this vault folder.

.PARAMETER FromSource
    Build this checkout and publish it, instead of downloading a release. Needs the .NET 10 SDK; Node.js for
    TypeScript.

.PARAMETER SkipTests
    With -FromSource, publish without running the test suite.

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
    [string] $Version = 'latest',
    [string] $InstallDir,
    [switch] $AddToPath,
    [switch] $Live,
    [string] $ObsidianVault,
    [switch] $FromSource,
    [switch] $SkipTests,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$repoSlug = 'nerdcules/CodeGraph'
# $PSScriptRoot is empty when the script runs from a downloaded string.
$root = if ($PSScriptRoot -and (Test-Path (Join-Path $PSScriptRoot 'CodeGraph.slnx'))) { $PSScriptRoot } else { $null }
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

function Get-ReleaseUrl([string] $asset) {
    if ($Version -eq 'latest') { "https://github.com/$repoSlug/releases/latest/download/$asset" }
    else { "https://github.com/$repoSlug/releases/download/$Version/$asset" }
}

function Get-Asset([string] $asset, [string] $to) {
    $url = Get-ReleaseUrl $asset
    try {
        Invoke-WebRequest -Uri $url -OutFile $to -UseBasicParsing
    } catch {
        throw "could not download $url. Check https://github.com/$repoSlug/releases, or build from a checkout with -FromSource. ($($_.Exception.Message))"
    }
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

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("codegraph-install-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
try {
    if ($FromSource) {
        if (-not $root) { throw '-FromSource needs this script run from a CodeGraph checkout' }
        $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
        if (-not $dotnet) { throw 'dotnet SDK not found on PATH. Install .NET 10 from https://dotnet.microsoft.com/download' }

        # The TypeScript and Angular extractor is a Node program; without Node, C# and the SCIP languages still work.
        $extractor = Join-Path $root 'extractors/typescript'
        $hasNode = (Get-Command node -ErrorAction SilentlyContinue) -and (Get-Command npm -ErrorAction SilentlyContinue)
        if ($hasNode) {
            Step "Installing the TypeScript extractor's packages (Node $(node --version))"
            Push-Location $extractor
            try { npm ci --omit=dev --no-audit --no-fund --loglevel=error } finally { Pop-Location }
            if ($LASTEXITCODE -ne 0) { throw 'npm ci failed for the TypeScript extractor' }
        } else {
            Write-Host 'note: node and npm are not on PATH; TypeScript and Angular indexing is skipped (install Node.js 22.22 or later to add it)'
        }

        Step 'Building (warnings are errors)'
        dotnet build (Join-Path $root 'CodeGraph.slnx') -c Release -warnaserror --nologo
        if ($LASTEXITCODE -ne 0) { throw 'build failed' }

        if (-not $SkipTests) {
            Step 'Running tests'
            dotnet test (Join-Path $root 'CodeGraph.slnx') -c Release --no-build
            if ($LASTEXITCODE -ne 0) { throw 'tests failed; nothing installed' }
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

        if ($hasNode) {
            # codegraph-index looks for extractors/typescript/cli.mjs beside itself.
            $installedExtractor = Join-Path $InstallDir 'extractors/typescript'
            if (Test-Path $installedExtractor) { Remove-Item -Recurse -Force $installedExtractor }
            New-Item -ItemType Directory -Force -Path $installedExtractor | Out-Null
            foreach ($item in 'cli.mjs', 'lib', 'package.json', 'package-lock.json') {
                Copy-Item -Recurse -Path (Join-Path $extractor $item) -Destination $installedExtractor
            }
            Push-Location $installedExtractor
            try { npm ci --omit=dev --no-audit --no-fund --loglevel=error } finally { Pop-Location }
            if ($LASTEXITCODE -ne 0) { throw 'npm ci failed for the installed TypeScript extractor' }
        }
    } else {
        $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        $arch = switch ($arch) { 'x64' { 'x64' } 'arm64' { 'arm64' } default { throw "unsupported CPU: $arch. Use -FromSource." } }
        $os = if ($onWindows) { 'win' } elseif ($IsMacOS) { 'osx' } elseif ($IsLinux) { 'linux' } else { throw 'unsupported OS. Use -FromSource.' }
        $rid = "$os-$arch"
        $asset = if ($os -eq 'win') { "codegraph-$rid.zip" } else { "codegraph-$rid.tar.gz" }

        Step "Downloading $asset ($Version)"
        $archive = Join-Path $tmp $asset
        Get-Asset $asset $archive
        $sums = Join-Path $tmp 'SHA256SUMS'
        Get-Asset 'SHA256SUMS' $sums
        $expected = (Get-Content $sums | Where-Object { $_ -match "\s$([regex]::Escape($asset))$" } | Select-Object -First 1) -split '\s+' | Select-Object -First 1
        if (-not $expected) { throw "no checksum for $asset in SHA256SUMS" }
        $actual = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
        if ($actual -ne $expected.ToLowerInvariant()) { throw "checksum mismatch for $asset" }
        Write-Host '  checksum ok'

        Step "Installing to $InstallDir"
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
        Get-ChildItem -Force $InstallDir | Remove-Item -Recurse -Force
        if ($os -eq 'win') {
            Expand-Archive -Path $archive -DestinationPath $InstallDir -Force
            Get-ChildItem -Recurse $InstallDir | Unblock-File -ErrorAction SilentlyContinue
        } else {
            tar -xzf $archive -C $InstallDir
            if ($LASTEXITCODE -ne 0) { throw 'tar failed' }
            chmod +x $indexExe $mcpExe
        }
        Write-Host "  codegraph $(Get-Content (Join-Path $InstallDir 'VERSION'))"
        if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
            Write-Host '  note: node is not on PATH; C# and the SCIP languages work, TypeScript and Angular need Node.js 22.22 or later'
        }
    }

    # A framework-dependent build finds .NET through DOTNET_ROOT or the machine-wide install, not through PATH. With a
    # user-local or version-managed dotnet the machine-wide one can be missing or lack ASP.NET Core. init writes
    # DOTNET_ROOT into .mcp.json when the server needs it; here the indexer gets it for this script's own runs.
    # A release build carries its runtime and never needs it.
    Step 'Checking the executables start'
    $dotnetRoot = $null
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $source = if ($dotnet -and $dotnet.Source) { (Get-Item $dotnet.Source) } else { $null }
    if ($source -and $source.LinkType -and $source.Target) {
        $source = Get-Item ([System.IO.Path]::GetFullPath((Join-Path $source.DirectoryName @($source.Target)[0])))
    }
    if ($source) { $dotnetRoot = $source.DirectoryName }
    $rootFor = $null
    Invoke-WithRoot $null $indexExe @('--version') *> $null
    $indexOk = $LASTEXITCODE -eq 0
    Invoke-WithRoot $null $mcpExe @('--version') *> $null
    if ($indexOk -and $LASTEXITCODE -eq 0) {
        Write-Host "ok: codegraph $(& $mcpExe --version)"
    } else {
        if ($dotnetRoot) { Invoke-WithRoot $dotnetRoot $mcpExe @('--version') *> $null }
        if ($dotnetRoot -and $LASTEXITCODE -eq 0) {
            $rootFor = $dotnetRoot
            Write-Host "ok with DOTNET_ROOT=$dotnetRoot (the default .NET location lacks a runtime codegraph needs)"
        } else {
            Write-Warning "codegraph-mcp does not start. Run $mcpExe --version by hand to see why."
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

    if ($ObsidianVault) {
        Step "Installing the Obsidian plugin into $ObsidianVault"
        if (-not (Test-Path (Join-Path $ObsidianVault '.obsidian'))) {
            Write-Warning "$ObsidianVault has no .obsidian folder: open it as a vault in Obsidian once first"
            $failed++
        } elseif ($FromSource -and (Get-Command node -ErrorAction SilentlyContinue)) {
            & node (Join-Path $root 'obsidian-plugin/install-plugin.mjs') $ObsidianVault
            if ($LASTEXITCODE -ne 0) { $failed++ }
        } else {
            $pluginDir = Join-Path $ObsidianVault '.obsidian/plugins/codegraph'
            $zip = Join-Path $tmp 'codegraph-obsidian.zip'
            Get-Asset 'codegraph-obsidian.zip' $zip
            New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
            Expand-Archive -Path $zip -DestinationPath $pluginDir -Force
            Write-Host "installed to $pluginDir; enable `"CodeGraph`" under Settings → Community plugins"
        }
    }
} finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
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
