<#
.SYNOPSIS
    Removes what install.ps1 put on this machine.

.DESCRIPTION
    Deletes the install folder, takes it out of the user PATH if install.ps1 -AddToPath put it there, and
    optionally removes the `codegraph` server entry from a repo's .mcp.json and that repo's .codegraph/ graph.
    Running codegraph processes keep the executables locked; the script refuses unless -Force stops them.

.PARAMETER InstallDir
    The folder install.ps1 published into. Default: C:\tools\codegraph on Windows, ~/.codegraph/bin elsewhere.

.PARAMETER Repo
    A repo whose .mcp.json loses its `codegraph` entry and whose .codegraph/ folder is deleted. Repeatable.

.PARAMETER Force
    Stop running codegraph-mcp / codegraph-index processes first.

.PARAMETER Yes
    Do not ask before deleting.

.EXAMPLE
    .\uninstall.ps1
    .\uninstall.ps1 -InstallDir D:\tools\codegraph -Repo C:\dev\App -Force -Yes
#>
[CmdletBinding()]
param(
    [string] $InstallDir,
    [string[]] $Repo = @(),
    [switch] $Force,
    [switch] $Yes
)

$ErrorActionPreference = 'Stop'
# $IsWindows does not exist in Windows PowerShell 5.1, which only runs on Windows.
$onWindows = $PSVersionTable.PSEdition -eq 'Desktop' -or $IsWindows
if (-not $InstallDir) {
    $InstallDir = if ($onWindows) { 'C:\tools\codegraph' } else { Join-Path $HOME '.codegraph/bin' }
}
$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)

function Step([string] $message) { Write-Host "`n==> $message" -ForegroundColor Cyan }

function Confirm-Step([string] $question) {
    if ($Yes) { return $true }
    $answer = Read-Host "$question [y/N]"
    return $answer.Trim().ToLowerInvariant() -in @('y', 'yes')
}

# ---- running processes --------------------------------------------------------------------------------
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

# ---- install folder -----------------------------------------------------------------------------------
if (Test-Path $InstallDir) {
    if (-not (Test-Path (Join-Path $InstallDir 'codegraph-mcp.dll')) -and -not (Test-Path (Join-Path $InstallDir 'codegraph-index.dll'))) {
        throw "$InstallDir does not look like a codegraph install (no codegraph-mcp.dll or codegraph-index.dll); not deleting it"
    }
    Step "Install folder: $InstallDir"
    if (Confirm-Step "Delete $InstallDir and everything in it?") {
        Remove-Item -Recurse -Force $InstallDir
        Write-Host 'deleted'
    }
} else {
    Write-Host "no install folder at $InstallDir"
}

# ---- PATH ---------------------------------------------------------------------------------------------
if ($onWindows) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $parts = @($userPath -split ';' | Where-Object { $_ })
    $kept = @($parts | Where-Object { -not $_.Trim('\').Equals($InstallDir.Trim('\'), [System.StringComparison]::OrdinalIgnoreCase) })
    if ($kept.Count -ne $parts.Count) {
        Step 'Removing from user PATH'
        [Environment]::SetEnvironmentVariable('Path', ($kept -join ';'), 'User')
        Write-Host 'Open a new shell for the PATH change to apply elsewhere.'
    }
} else {
    # The "# codegraph" marker and the export line after it, as install.sh and install.ps1 write them.
    $export = "export PATH=`"`$PATH:$InstallDir`""
    foreach ($rc in (Join-Path $HOME '.bashrc'), (Join-Path $HOME '.zshrc')) {
        if (-not (Test-Path $rc)) { continue }
        $lines = @(Get-Content $rc)
        if ($lines -notcontains $export) { continue }
        Step "Removing from $rc"
        $kept = [System.Collections.Generic.List[string]]::new()
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -eq '# codegraph' -and $i + 1 -lt $lines.Count -and $lines[$i + 1] -eq $export) { $i++; continue }
            $kept.Add($lines[$i])
        }
        Set-Content -Path $rc -Value $kept
        Write-Host 'Open a new shell for the PATH change to apply.'
    }
}

# ---- repos --------------------------------------------------------------------------------------------
foreach ($dir in $Repo) {
    $dir = [System.IO.Path]::GetFullPath($dir)
    Step "Repo: $dir"

    $configPath = Join-Path $dir '.mcp.json'
    if (Test-Path $configPath) {
        # Objects, not -AsHashtable: Windows PowerShell 5.1 does not have that parameter.
        $config = Get-Content $configPath -Raw | ConvertFrom-Json
        $servers = if ($config) { $config.PSObject.Properties['mcpServers'] } else { $null }
        if ($servers -and $servers.Value.PSObject.Properties['codegraph']) {
            if (Confirm-Step "Remove the codegraph server entry from $configPath?") {
                $servers.Value.PSObject.Properties.Remove('codegraph')
                $json = $config | ConvertTo-Json -Depth 10
                [System.IO.File]::WriteAllText($configPath, $json + "`n", [System.Text.UTF8Encoding]::new($false))
                Write-Host 'entry removed'
            }
        } else {
            Write-Host "no codegraph entry in $configPath"
        }
    }

    $graphDir = Join-Path $dir '.codegraph'
    if (Test-Path $graphDir) {
        if (Confirm-Step "Delete the graph and telemetry in $graphDir?") {
            Remove-Item -Recurse -Force $graphDir
            Write-Host 'deleted'
        }
    }
}

Write-Host "`nDone."
