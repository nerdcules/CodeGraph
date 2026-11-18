<#
.SYNOPSIS
    Installs codegraph-index and codegraph-mcp from the latest GitHub release, sets up repos to use them, and can
    add the VS Code extension.

.DESCRIPTION
    Downloads the release archive for this platform (it carries its own .NET runtime, so nothing has to be built
    or installed first), unpacks it into one folder, and runs `codegraph-index init` for each repo: that writes the
    codegraph server into the repo's .mcp.json, adds .codegraph/ to .gitignore and builds the graph. With
    -VSCode it also installs the VS Code extension (codegraph-vscode.vsix from the same release). With -FromSource,
    builds and publishes this checkout instead (needs the .NET 10 SDK).

    In a console the script asks for what the command line leaves out: the install folder, the repos, the
    assistants, the live daemon, PATH and the VS Code extension, then shows the plan before it changes anything.
    Use the arrow keys and space in the lists, Enter to accept. -Yes, CODEGRAPH_NONINTERACTIVE=1, or redirected
    input asks nothing.

    Re-run to upgrade: a running codegraph-mcp holds its files open, so close the assistant sessions first or pass
    -Force to stop them. Works in Windows PowerShell 5.1 and PowerShell 7 on Windows, macOS and Linux.
    CODEGRAPH_RELEASE_URL replaces https://github.com/nerdcules/CodeGraph/releases, for a mirror with the same layout.

    One line, without a checkout:
      & ([scriptblock]::Create((irm https://raw.githubusercontent.com/nerdcules/CodeGraph/main/codegraph/install.ps1)))

.PARAMETER Repo
    Repo folders or .sln / .slnx / .csproj files to set up.

.PARAMETER Version
    A release tag to install, such as v1.0.0. Default: the latest release.

.PARAMETER InstallDir
    Where the executables go. Default: C:\tools\codegraph on Windows, ~/.codegraph/bin elsewhere.

.PARAMETER AddToPath
    Add InstallDir to the user PATH (Windows) or to ~/.bashrc and ~/.zshrc, so `codegraph-index` works anywhere.

.PARAMETER Live
    The MCP server starts the live daemon itself on its first call (CODEGRAPH_DAEMON=auto in .mcp.json).

.PARAMETER Client
    The assistants to set each repo up for: claude (the default; also the Copilot CLI), copilot (CLI, VS Code,
    Visual Studio, JetBrains), vscode, visualstudio, jetbrains, cursor, gemini, codex, roo, kilo, continue, or all.
    Comma-separated or an array.

.PARAMETER VSCode
    Install the VS Code extension from the same release with `code --install-extension`.

.PARAMETER NoVSCode
    Do not install the VS Code extension, and do not ask.

.PARAMETER Yes
    Ask nothing: take the options given and the defaults for the rest.

.PARAMETER FromSource
    Build this checkout and publish it, instead of downloading a release. Needs the .NET 10 SDK; Node.js for
    TypeScript and for the VS Code extension.

.PARAMETER SkipTests
    With -FromSource, publish without running the test suite.

.PARAMETER Force
    Stop running codegraph-mcp / codegraph-index processes before overwriting them.

.EXAMPLE
    .\install.ps1
    .\install.ps1 C:\dev\App -AddToPath -VSCode
    .\install.ps1 -InstallDir D:\tools\codegraph -Repo C:\dev\App\App.sln, C:\dev\Other -Live -Yes
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]] $Repo = @(),
    [string] $Version = 'latest',
    [string] $InstallDir,
    [switch] $AddToPath,
    [switch] $Live,
    [string[]] $Client = @(),
    [switch] $VSCode,
    [switch] $NoVSCode,
    [switch] $Yes,
    [switch] $FromSource,
    [switch] $SkipTests,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
# A Windows PowerShell 5.1 started by a program that runs under PowerShell 7 (cmd, Python, an editor's terminal)
# inherits 7's PSModulePath, finds 7's modules first, and loads ones that do not work in 5.1: Get-FileHash was not
# found and Expand-Archive failed (windows-report-6 and -7). pwsh drops these folders when it starts powershell.exe
# itself; this does the same for this process, before any module is loaded.
if ($PSVersionTable.PSEdition -eq 'Desktop' -and $env:PSModulePath) {
    $env:PSModulePath = (@($env:PSModulePath -split ';' | Where-Object {
        $_ -and $_ -notmatch '\\PowerShell\\(7[^\\]*\\)?Modules\\?$' -and $_ -notmatch '\\WindowsApps\\Microsoft\.PowerShell_'
    })) -join ';'
}
# $Repo takes the arguments no parameter matched, so a mistyped or removed option (-ObsidianVault) would land there
# and the install would go ahead. Stop before downloading or writing anything, as install.sh does.
$unknown = @($Repo | Where-Object { $_ -like '-*' })
if ($unknown.Count -gt 0) {
    throw "unknown option: $($unknown -join ', '). See Get-Help $PSCommandPath -Detailed"
}
if ($VSCode -and $NoVSCode) { throw 'give -VSCode or -NoVSCode, not both' }
$repoSlug = 'nerdcules/CodeGraph'
$releaseBase = if ($env:CODEGRAPH_RELEASE_URL) { $env:CODEGRAPH_RELEASE_URL.TrimEnd('/') } else { "https://github.com/$repoSlug/releases" }
$extensionId = 'nerdcules.codegraph'
# $PSScriptRoot is empty when the script runs from a downloaded string.
$root = if ($PSScriptRoot -and (Test-Path (Join-Path $PSScriptRoot 'CodeGraph.slnx'))) { $PSScriptRoot } else { $null }
if ($FromSource -and -not $root) { throw '-FromSource needs this script run from a CodeGraph checkout' }
# $IsWindows does not exist in Windows PowerShell 5.1, which only runs on Windows.
$onWindows = $PSVersionTable.PSEdition -eq 'Desktop' -or $IsWindows
$exe = if ($onWindows) { '.exe' } else { '' }
$defaultInstallDir = if ($onWindows) { 'C:\tools\codegraph' } else { Join-Path $HOME '.codegraph/bin' }
$Repo = @($Repo)
$repos = New-Object System.Collections.Generic.List[string]
foreach ($r in $Repo) { $repos.Add($r) }
$clients = ($Client | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { $_.Trim() }) -join ','
# $null: not given, so the questions may ask. The switches only ever say yes.
$addPath = if ($PSBoundParameters.ContainsKey('AddToPath')) { [bool]$AddToPath } else { $null }
$liveDaemon = if ($PSBoundParameters.ContainsKey('Live')) { [bool]$Live } else { $null }
$installExtension = if ($VSCode) { $true } elseif ($NoVSCode) { $false } else { $null }

# ---- console ---------------------------------------------------------------------------------------------
# Questions need a real console: not the ISE, not redirected input or output (a CI job, the Claude Code plugin).
$interactive = -not $Yes -and -not $env:CODEGRAPH_NONINTERACTIVE -and $Host.Name -eq 'ConsoleHost' -and
    [Environment]::UserInteractive -and -not [Console]::IsInputRedirected -and -not [Console]::IsOutputRedirected
# The source stays ASCII, because Windows PowerShell 5.1 reads a file without a BOM in the ANSI code page; the
# symbols are made from their code points, and only when the console writes UTF-8.
if ([Console]::OutputEncoding.CodePage -eq 65001) {
    $gOn = [string][char]0x25C9; $gOff = [string][char]0x25CB; $gPtr = [string][char]0x276F
    $gOk = [string][char]0x2713; $gFail = [string][char]0x2717; $gSkip = [string][char]0x2013
    $gKeys = "$([char]0x2191)$([char]0x2193)"
} else {
    $gOn = '[x]'; $gOff = '[ ]'; $gPtr = '>'; $gOk = 'ok'; $gFail = '!!'; $gSkip = '--'; $gKeys = 'up/down'
}

function Step([string] $message) { Write-Host "`n==> " -ForegroundColor Cyan -NoNewline; Write-Host $message }
function Note([string] $message) { Write-Host "  $message" -ForegroundColor DarkGray }
function Pretty([string] $path) {
    if (-not $onWindows -and $path.StartsWith("$HOME/")) { return '~/' + $path.Substring($HOME.Length + 1) }
    $path
}

$results = New-Object System.Collections.Generic.List[object]
function Add-Result([string] $state, [string] $text) { $results.Add([pscustomobject]@{ State = $state; Text = $text }) }

function Write-Question([string] $question, [string] $hint) {
    Write-Host '? ' -ForegroundColor Cyan -NoNewline
    Write-Host "$question " -NoNewline
    if ($hint) { Write-Host "($hint) " -ForegroundColor DarkGray -NoNewline }
}

# One key; Enter takes the default.
function Ask-YesNo([string] $question, [bool] $default) {
    Write-Question $question $(if ($default) { 'Y/n' } else { 'y/N' })
    while ($true) {
        $key = [Console]::ReadKey($true)
        if ($key.Key -eq 'Enter') { $answer = $default; break }
        if ($key.KeyChar -eq 'y' -or $key.KeyChar -eq 'Y') { $answer = $true; break }
        if ($key.KeyChar -eq 'n' -or $key.KeyChar -eq 'N') { $answer = $false; break }
    }
    if ($answer) { Write-Host 'Yes' -ForegroundColor Green } else { Write-Host 'No' -ForegroundColor DarkGray }
    $answer
}

# A line; Enter takes the default. The spaces and quotes a paste or a drag from Explorer brings are trimmed.
function Ask-Text([string] $question, [string] $default) {
    Write-Question $question $default
    $line = [Console]::ReadLine()
    if ($null -eq $line) { $line = '' }
    $line = $line.Trim().Trim('"').Trim("'")
    if (-not $line) { $line = $default }
    if ($line -and ($line -eq '~' -or $line.StartsWith('~/') -or $line.StartsWith('~\'))) { $line = $HOME + $line.Substring(1) }
    $line
}

# Arrow keys (or j/k) move, space toggles, a toggles all, Enter accepts while one or more rows are on. Each item has
# Key, Label, Hint and On; returns the keys that are on, comma-separated.
# The list is redrawn in place. On Windows that is by row number, from the console API. Elsewhere .NET learns the row
# by asking the terminal, which not every terminal answers, so the list moves up by escape codes instead, as
# install.sh does.
function Ask-Checklist([string] $question, [object[]] $items) {
    $n = $items.Count
    $esc = [char]27
    $width = ($items | ForEach-Object { $_.Label.Length } | Measure-Object -Maximum).Maximum
    $lineWidth = [Math]::Max(20, [Console]::WindowWidth - 1)
    Write-Question $question "$gKeys move, space select, a all, enter accept"
    Write-Host ''
    if ($onWindows) {
        # Make room first: a list that scrolls the window while it is drawn would lose its top row.
        for ($i = 0; $i -lt $n; $i++) { Write-Host '' }
        $top = [Console]::CursorTop - $n
    }
    $cursor = 0
    $drawn = $false
    try { [Console]::CursorVisible = $false } catch { }
    try {
        while ($true) {
            if (-not $onWindows -and $drawn) { Write-Host "$esc[$($n)A" -NoNewline }
            $drawn = $true
            for ($i = 0; $i -lt $n; $i++) {
                if ($onWindows) { [Console]::SetCursorPosition(0, $top + $i) } else { Write-Host "$esc[2K" -NoNewline }
                $item = $items[$i]
                $ptr = if ($i -eq $cursor) { $gPtr } else { ' ' }
                $mark = if ($item.On) { $gOn } else { $gOff }
                $label = ' ' + $item.Label.PadRight($width) + '  '
                Write-Host '  ' -NoNewline
                Write-Host $ptr -ForegroundColor Cyan -NoNewline
                Write-Host ' ' -NoNewline
                Write-Host $mark -ForegroundColor $(if ($item.On) { 'Green' } else { 'Gray' }) -NoNewline
                Write-Host $label -NoNewline
                $room = [Math]::Max(0, $lineWidth - (4 + $ptr.Length + $mark.Length + $label.Length))
                $hint = $item.Hint
                if ($hint.Length -gt $room) { $hint = $hint.Substring(0, $room) }
                if ($onWindows) { Write-Host $hint.PadRight($room) -ForegroundColor DarkGray -NoNewline }
                else { Write-Host $hint -ForegroundColor DarkGray }
            }
            $key = [Console]::ReadKey($true)
            if ($key.Key -eq 'UpArrow' -or $key.KeyChar -eq 'k') { $cursor = ($cursor + $n - 1) % $n }
            elseif ($key.Key -eq 'DownArrow' -or $key.KeyChar -eq 'j') { $cursor = ($cursor + 1) % $n }
            elseif ($key.Key -eq 'Spacebar' -or $key.KeyChar -eq 'x') { $items[$cursor].On = -not $items[$cursor].On }
            elseif ($key.KeyChar -eq 'a' -or $key.KeyChar -eq 'A') {
                $all = @($items | Where-Object { -not $_.On }).Count -gt 0
                foreach ($item in $items) { $item.On = $all }
            }
            elseif ($key.Key -eq 'Enter' -and @($items | Where-Object { $_.On }).Count -gt 0) { break }
        }
    } finally {
        try { [Console]::CursorVisible = $true } catch { }
    }
    # Fold the list into one line that names what was picked.
    if ($onWindows) {
        for ($i = -1; $i -lt $n; $i++) {
            [Console]::SetCursorPosition(0, $top + $i)
            Write-Host (' ' * $lineWidth) -NoNewline
        }
        [Console]::SetCursorPosition(0, $top - 1)
    } else {
        Write-Host "$esc[$($n + 1)A$esc[J" -NoNewline
    }
    $on = @($items | Where-Object { $_.On })
    Write-Question $question ''
    Write-Host (($on | ForEach-Object { $_.Label }) -join ', ') -ForegroundColor Green
    ($on | ForEach-Object { $_.Key }) -join ','
}

# Runs an executable with DOTNET_ROOT set to $dotnetRoot (or unset), restoring the caller's value after.
function Invoke-WithRoot([string] $dotnetRoot, [string] $file, [string[]] $arguments) {
    $saved = $env:DOTNET_ROOT
    $env:DOTNET_ROOT = $dotnetRoot
    try { & $file @arguments } finally { $env:DOTNET_ROOT = $saved }
}

function Get-ReleaseUrl([string] $asset) {
    if ($Version -eq 'latest') { "$releaseBase/latest/download/$asset" }
    else { "$releaseBase/download/$Version/$asset" }
}

function Get-Asset([string] $asset, [string] $to) {
    $url = Get-ReleaseUrl $asset
    try {
        if ($url.StartsWith('file:')) { Copy-Item -LiteralPath ([uri]$url).LocalPath -Destination $to }
        else { Invoke-WebRequest -Uri $url -OutFile $to -UseBasicParsing }
    } catch {
        throw "could not download $url. Check https://github.com/$repoSlug/releases, or build from a checkout with -FromSource. ($($_.Exception.Message))"
    }
}

function Test-Checksum([string] $file, [string] $sums) {
    $name = Split-Path -Leaf $file
    $expected = (Get-Content $sums | Where-Object { $_ -match "\s$([regex]::Escape($name))$" } | Select-Object -First 1) -split '\s+' | Select-Object -First 1
    if (-not $expected) { throw "no checksum for $name in SHA256SUMS" }
    # .NET, not Get-FileHash. In Windows PowerShell 5.1, Get-FileHash is a function of the Microsoft.PowerShell.Utility
    # module folder, and a 5.1 that inherited PowerShell 7's PSModulePath (started by cmd or Python under pwsh)
    # finds 7's folder first and has no Get-FileHash (windows-report-6).
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($file)
    try { $actual = -join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) }
    finally { $stream.Dispose(); $sha.Dispose() }
    if ($actual -ne $expected.ToLowerInvariant()) { throw "checksum mismatch for $name" }
    Note 'checksum ok'
}

# Off Windows, the rc files a PATH line goes in: those that exist, and the login shell's even when it does not yet.
# These are the lines install.sh writes, so either uninstall script removes them.
function Get-PathRcs {
    $shellRc = if ($env:SHELL) { Join-Path $HOME ".$(Split-Path -Leaf $env:SHELL)rc" } else { $null }
    foreach ($rc in (Join-Path $HOME '.bashrc'), (Join-Path $HOME '.zshrc')) {
        if ((Test-Path $rc) -or $rc -eq $shellRc) { $rc }
    }
}

function Test-OnPath([string] $dir) {
    $sep = [System.IO.Path]::PathSeparator
    @($env:PATH -split [regex]::Escape($sep) | ForEach-Object { $_.TrimEnd('\', '/') }) -contains $dir.TrimEnd('\', '/')
}

# Runs a native command and returns its output when it exits 0, with stderr dropped. Windows PowerShell 5.1 turns
# redirected stderr lines into errors, and with $ErrorActionPreference = 'Stop' the first one would end the script.
function Get-NativeOutput([string] $file, [string[]] $arguments) {
    $ErrorActionPreference = 'Continue'
    try {
        $out = @(& $file @arguments 2>$null)
        if ($LASTEXITCODE -eq 0) { $out }
    } catch { }
}

# The installed version of the extension, or $null.
function Get-ExtensionVersion([string] $editor) {
    $line = Get-NativeOutput $editor @('--list-extensions', '--show-versions') | Where-Object { $_ -like "$extensionId@*" } | Select-Object -First 1
    if ($line) { $line.Substring($extensionId.Length + 1) } else { $null }
}

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("codegraph-install-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$failed = 0
try {
    $rid = $null
    $sums = Join-Path $tmp 'SHA256SUMS'
    if (-not $FromSource) {
        $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        $arch = switch ($arch) { 'x64' { 'x64' } 'arm64' { 'arm64' } default { throw "unsupported CPU: $arch. Use -FromSource." } }
        $os = if ($onWindows) { 'win' } elseif ($IsMacOS) { 'osx' } elseif ($IsLinux) { 'linux' } else { throw 'unsupported OS. Use -FromSource.' }
        $rid = "$os-$arch"
        # The checksums first: they are small, they show that the release exists before any question is asked, and
        # they say whether it carries the VS Code extension.
        Get-Asset 'SHA256SUMS' $sums
    }

    # VS Code's command line: on PATH, or where the user installer puts it on Windows and the app keeps it on macOS.
    $editors = @(foreach ($name in 'code', 'code-insiders') {
        $cmd = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cmd) { $cmd.Source }
    })
    if ($editors.Count -eq 0) {
        $candidates = if ($onWindows) { @(Join-Path $env:LOCALAPPDATA 'Programs\Microsoft VS Code\bin\code.cmd') }
                      else { @('/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code') }
        $editors = @($candidates | Where-Object { $_ -and (Test-Path $_) })
    }

    # Whether this install can offer the extension, and why not when it cannot.
    $vsixProblem = $null
    if ($FromSource) {
        if (-not ((Get-Command node -ErrorAction SilentlyContinue) -and (Get-Command npm -ErrorAction SilentlyContinue))) {
            $vsixProblem = 'building the VS Code extension needs node and npm on PATH'
        }
    } elseif (-not (Get-Content $sums | Where-Object { $_ -match '\scodegraph-vscode\.vsix$' })) {
        $vsixProblem = "release $Version has no VS Code extension (releases after 1.2.0 carry codegraph-vscode.vsix)"
    }
    if (-not $vsixProblem -and $editors.Count -eq 0) { $vsixProblem = 'the VS Code command line (code) is not on PATH' }
    if ($installExtension -eq $true -and $vsixProblem) { throw "-VSCode: $vsixProblem" }

    # ---- the questions ------------------------------------------------------------------------------------
    if ($interactive) {
        Write-Host ''
        Write-Host 'CodeGraph installer  ' -NoNewline
        Write-Host $(if ($FromSource) { "build of $root" } else { "release $Version, $rid" }) -ForegroundColor DarkGray
        Note "The compiler's answers for your AI coding assistant: who calls this, what breaks if I change it, which tests guard it."
        Write-Host ''

        if (-not $InstallDir) { $InstallDir = Ask-Text 'Install folder' (Pretty $defaultInstallDir) }

        if ($repos.Count -eq 0) {
            $here = $null
            if (Get-Command git -ErrorAction SilentlyContinue) {
                $here = Get-NativeOutput 'git' @('rev-parse', '--show-toplevel') | Select-Object -First 1
                if ($here) { $here = [System.IO.Path]::GetFullPath($here) }
            }
            if ($here -and (Ask-YesNo "Set up $(Pretty $here)?" $true)) { $repos.Add($here) }
            while ($true) {
                $question = if ($repos.Count -eq 0) { 'Repository to set up: a folder, or a .sln / .slnx / .csproj (Enter to skip)' }
                            else { 'Another repository? (Enter when done)' }
                $answer = Ask-Text $question ''
                if (-not $answer) { break }
                if (Test-Path -LiteralPath $answer) { $repos.Add($answer) } else { Write-Host "  not found: $answer" }
            }
        }

        if ($repos.Count -gt 0 -and -not $clients) {
            # Claude Code is on by default, as in `init`; the others are on when their program is found.
            function Found([string] $command, [string] $folder) {
                [bool](Get-Command $command -ErrorAction SilentlyContinue) -or [bool]($folder -and (Test-Path $folder))
            }
            $items = @(
                [pscustomobject]@{ Key = 'claude'; Label = 'Claude Code, Copilot CLI'; Hint = '.mcp.json'; On = $true }
                [pscustomobject]@{ Key = 'vscode'; Label = 'VS Code chat'; Hint = '.vscode/mcp.json (the extension offers the server without it)'; On = $false }
            )
            if ($onWindows) { $items += [pscustomobject]@{ Key = 'visualstudio'; Label = 'Visual Studio'; Hint = '.vs/mcp.json beside the solution'; On = $false } }
            $items += @(
                [pscustomobject]@{ Key = 'jetbrains'; Label = 'Copilot for JetBrains'; Hint = '.github/mcp.json'; On = $false }
                [pscustomobject]@{ Key = 'cursor'; Label = 'Cursor'; Hint = '.cursor/mcp.json'; On = (Found 'cursor' (Join-Path $HOME '.cursor')) }
                [pscustomobject]@{ Key = 'gemini'; Label = 'Gemini CLI'; Hint = '.gemini/settings.json'; On = (Found 'gemini' $null) }
                [pscustomobject]@{ Key = 'codex'; Label = 'Codex'; Hint = 'codex mcp add, in your Codex config'; On = (Found 'codex' $null) }
                [pscustomobject]@{ Key = 'roo'; Label = 'Roo Code'; Hint = '.roo/mcp.json'; On = $false }
                [pscustomobject]@{ Key = 'kilo'; Label = 'Kilo Code'; Hint = '.kilo/kilo.json'; On = $false }
                [pscustomobject]@{ Key = 'continue'; Label = 'Continue'; Hint = '.continue/mcpServers/codegraph.json'; On = $false }
            )
            $clients = Ask-Checklist 'Assistants to set each repository up for' $items
        }

        if ($repos.Count -gt 0 -and $null -eq $liveDaemon) {
            Note 'The live daemon answers from the compiler between indexes; it holds the loaded solution in memory.'
            $liveDaemon = Ask-YesNo 'Let the MCP server start the live daemon on its first call?' $false
        }
    }

    if (-not $InstallDir) { $InstallDir = $defaultInstallDir }
    $InstallDir = [System.IO.Path]::GetFullPath($InstallDir)
    $indexExe = Join-Path $InstallDir "codegraph-index$exe"
    $mcpExe = Join-Path $InstallDir "codegraph-mcp$exe"
    $userPath = if ($onWindows) { [Environment]::GetEnvironmentVariable('Path', 'User') } else { $null }
    $alreadyOnPath = if ($onWindows) { @($userPath -split ';' | Where-Object { $_ }) -contains $InstallDir } else { Test-OnPath $InstallDir }

    if ($interactive) {
        if ($null -eq $addPath) {
            $addPath = if ($alreadyOnPath) { $false } else {
                Ask-YesNo "Add $(Pretty $InstallDir) to PATH, so codegraph-index works in any shell?" $true
            }
        }
        if ($null -eq $installExtension) {
            if ($vsixProblem) {
                Note "VS Code extension: not offered; $vsixProblem."
                $installExtension = $false
            } else {
                $installed = Get-ExtensionVersion $editors[0]
                $question = if ($installed) { "Update the CodeGraph extension for VS Code (installed: $installed)?" }
                            else { 'Install the CodeGraph extension for VS Code?' }
                Note "The extension shows each graph's state, runs the live daemons, and offers the MCP server to VS Code's chat."
                $installExtension = Ask-YesNo $question $true
            }
        }

        # The plan, before anything changes.
        Write-Host ''
        Write-Host 'Plan'
        $row = { param($label, $text, $dim) Write-Host ('  ' + $label.PadRight(12) + ' ') -NoNewline; Write-Host $text -NoNewline; if ($dim) { Write-Host "  $dim" -ForegroundColor DarkGray } else { Write-Host '' } }
        if ($FromSource) { & $row 'Install' "build, $(if ($SkipTests) { 'no tests' } else { 'test' }), and publish $root to $(Pretty $InstallDir)" $null }
        else { & $row 'Install' "CodeGraph $Version ($rid) to $(Pretty $InstallDir)" $null }
        foreach ($target in $repos) {
            & $row 'Set up' (Pretty $target) ("$(if ($clients) { $clients } else { 'claude' })$(if ($liveDaemon) { ', live daemon' })")
        }
        if ($repos.Count -eq 0) { & $row 'Set up' 'no repository now' $null }
        if ($addPath) {
            $rcs = @(Get-PathRcs | ForEach-Object { Pretty $_ })
            & $row 'PATH' $(if ($onWindows) { 'add to the user PATH' } elseif ($rcs.Count -gt 0) { "add to $($rcs -join ' ')" } else { 'no ~/.bashrc or ~/.zshrc to add it to' }) $null
        }
        if ($installExtension) { & $row 'VS Code' "install the extension with $(($editors | ForEach-Object { Split-Path -Leaf $_ }) -join ', ')" $null }
        Write-Host ''
        if (-not (Ask-YesNo 'Go ahead?' $true)) { Write-Host 'Nothing was changed.'; return }
    }

    # ---- running processes --------------------------------------------------------------------------------
    # A running server keeps codegraph-mcp and its DLLs locked (Windows) or open (elsewhere); overwriting is unsafe.
    $running = Get-Process -Name 'codegraph-mcp', 'codegraph-index' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [System.StringComparison]::OrdinalIgnoreCase) }
    if ($running) {
        $list = ($running | ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" }) -join ', '
        if (-not $Force -and $interactive) {
            Write-Host "codegraph is running from $(Pretty $InstallDir): $list."
            $Force = Ask-YesNo 'Stop it? Assistant sessions using it lose the codegraph tools until they restart it.' $false
        }
        if ($Force) {
            Step "Stopping $(@($running).Count) running codegraph process(es)"
            $running | Stop-Process -Force
            Start-Sleep -Milliseconds 500
        } else {
            throw "codegraph is running from $InstallDir ($list). Close the assistant sessions using it, or re-run with -Force."
        }
    }

    # ---- the executables ----------------------------------------------------------------------------------
    if ($FromSource) {
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
            Note 'node and npm are not on PATH; TypeScript and Angular indexing is skipped (install Node.js 22.22.3+ or 24.15+ to add it)'
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
        $sourceRid = (dotnet --info | Select-String -Pattern '^\s*RID:\s*(\S+)' | Select-Object -First 1).Matches.Groups[1].Value
        if (-not $sourceRid) { throw 'could not read the runtime identifier from dotnet --info' }
        Step "Publishing to $InstallDir ($sourceRid, ReadyToRun)"
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
        foreach ($project in 'src/CodeGraph.Indexer', 'src/CodeGraph.Mcp') {
            dotnet publish (Join-Path $root $project) -c Release -r $sourceRid --self-contained false -p:PublishReadyToRun=true -o $InstallDir --nologo
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
        $asset = if ($os -eq 'win') { "codegraph-$rid.zip" } else { "codegraph-$rid.tar.gz" }
        Step "Downloading $asset ($Version)"
        $archive = Join-Path $tmp $asset
        Get-Asset $asset $archive
        Test-Checksum $archive $sums

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
        Note "codegraph $(Get-Content (Join-Path $InstallDir 'VERSION'))"
        if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
            Note 'node is not on PATH; C# and the SCIP languages work, TypeScript and Angular need Node.js 22.22.3+ or 24.15+'
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
        # Combine, not Join-Path: a link to an absolute path (/usr/bin/dotnet -> /usr/share/dotnet/dotnet) keeps it.
        $source = Get-Item ([System.IO.Path]::GetFullPath([System.IO.Path]::Combine($source.DirectoryName, @($source.Target)[0])))
    }
    if ($source) { $dotnetRoot = $source.DirectoryName }
    $rootFor = $null
    Invoke-WithRoot $null $indexExe @('--version') *> $null
    $indexOk = $LASTEXITCODE -eq 0
    Invoke-WithRoot $null $mcpExe @('--version') *> $null
    if ($indexOk -and $LASTEXITCODE -eq 0) {
        $installedVersion = & $mcpExe --version
        Note "ok: codegraph $installedVersion"
        Add-Result 'ok' "CodeGraph $installedVersion in $(Pretty $InstallDir)"
    } else {
        if ($dotnetRoot) { Invoke-WithRoot $dotnetRoot $mcpExe @('--version') *> $null }
        if ($dotnetRoot -and $LASTEXITCODE -eq 0) {
            $rootFor = $dotnetRoot
            Note "ok with DOTNET_ROOT=$dotnetRoot (the default .NET location lacks a runtime codegraph needs)"
            Add-Result 'ok' "CodeGraph in $(Pretty $InstallDir) (needs DOTNET_ROOT=$dotnetRoot by hand)"
        } else {
            Write-Warning "codegraph-mcp does not start. Run $mcpExe --version by hand to see why."
            Add-Result 'fail' "CodeGraph in $(Pretty $InstallDir) does not start"
        }
    }

    if ($addPath) {
        if ($alreadyOnPath) {
            Add-Result 'skip' "PATH already has $(Pretty $InstallDir)"
        } elseif ($onWindows) {
            Step 'Adding to user PATH'
            $parts = @($userPath -split ';' | Where-Object { $_ })
            [Environment]::SetEnvironmentVariable('Path', (($parts + $InstallDir) -join ';'), 'User')
            $env:Path = "$env:Path;$InstallDir"
            Add-Result 'ok' 'PATH: added to the user PATH (open a new shell)'
        } else {
            Step 'Adding to PATH'
            $written = @()
            foreach ($rc in @(Get-PathRcs)) {
                Add-Content -Path $rc -Value "`n# codegraph`nexport PATH=`"`$PATH:$InstallDir`""
                Note "added to $(Pretty $rc)"
                $written += Pretty $rc
            }
            if ($written.Count -gt 0) { Add-Result 'ok' "PATH: added to $($written -join ' ') (open a new shell)" }
            else { Add-Result 'fail' "PATH: no ~/.bashrc or ~/.zshrc; add $InstallDir to PATH yourself" }
        }
    }

    foreach ($target in $repos) {
        Step "Setting up $target"
        $initArgs = @('init', $target)
        if ($liveDaemon) { $initArgs += '--live' }
        if ($clients) { $initArgs += @('--client', $clients) }
        Invoke-WithRoot $(if ($rootFor) { $rootFor } else { $env:DOTNET_ROOT }) $indexExe $initArgs
        if ($LASTEXITCODE -eq 0) {
            Add-Result 'ok' "Set up $(Pretty $target) for $(if ($clients) { $clients } else { 'claude' })"
        } else {
            $failed++
            Add-Result 'fail' "Set up $(Pretty $target) failed; see above"
        }
    }

    # ---- the VS Code extension ----------------------------------------------------------------------------
    if ($installExtension) {
        try {
            if ($FromSource) {
                Step 'Building the VS Code extension'
                Push-Location (Join-Path $root 'vscode-extension')
                try {
                    foreach ($npmArgs in @(@('ci', '--no-audit', '--no-fund', '--loglevel=error'), @('run', 'compile'), @('run', 'package'))) {
                        npm @npmArgs
                        if ($LASTEXITCODE -ne 0) { throw "npm $($npmArgs -join ' ') failed" }
                    }
                } finally { Pop-Location }
                $vsix = Join-Path $root 'dist/codegraph-vscode.vsix'
            } else {
                Step "Downloading codegraph-vscode.vsix ($Version)"
                $vsix = Join-Path $tmp 'codegraph-vscode.vsix'
                Get-Asset 'codegraph-vscode.vsix' $vsix
                Test-Checksum $vsix $sums
            }
            foreach ($editor in $editors) {
                Step "Installing the extension with $(Split-Path -Leaf $editor)"
                & $editor --install-extension $vsix --force
                if ($LASTEXITCODE -ne 0) { throw "$editor --install-extension failed" }
            }
            Add-Result 'ok' "VS Code extension $(Get-ExtensionVersion $editors[0]) (reload open VS Code windows)"
            # The extension looks on its own PATH, then in the default folder. A VS Code started before the PATH change,
            # or from the desktop on macOS and Linux, does not see it, so anywhere but the default folder needs the setting.
            if ($InstallDir -ne [System.IO.Path]::GetFullPath($defaultInstallDir)) {
                Add-Result 'skip' "VS Code: set codegraph.installDir to $InstallDir, or the extension installs its own copy"
            }
        } catch {
            Write-Warning $_.Exception.Message
            $failed++
            Add-Result 'fail' 'VS Code extension: not installed; see above'
        }
    }
} finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

Step 'Done'
foreach ($r in $results) {
    switch ($r.State) {
        'ok'   { Write-Host "  $gOk " -ForegroundColor Green -NoNewline }
        'fail' { Write-Host "  $gFail " -ForegroundColor Red -NoNewline }
        default { Write-Host "  $gSkip " -ForegroundColor Yellow -NoNewline }
    }
    Write-Host $r.Text
}
$call = if ($addPath -or $alreadyOnPath) { 'codegraph-index' } elseif ($onWindows) { "& '$indexExe'" } else { $indexExe }
Write-Host ''
if ($rootFor) {
    Write-Host "Run by hand, the executables need DOTNET_ROOT=$rootFor in the environment (.mcp.json entries carry it already)."
}
Write-Host 'Set up another repo (writes .mcp.json and .gitignore at its root, then builds the graph):'
Write-Host "  $call init <repo-or-solution> [--live] [--client copilot,cursor,gemini,codex,roo,kilo,continue]"
Write-Host 'Keep a graph fresh while you work:'
Write-Host "  $call <App.sln> --watch"
if ($failed -gt 0) { throw "$failed step(s) failed; see above" }
