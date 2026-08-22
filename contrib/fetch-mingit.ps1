<#
.SYNOPSIS
    Fetches the pinned MinGit build that BackerInstaller.iss bundles as the
    git transfer engine's client.

.DESCRIPTION
    The git engine (worker/WorkerGit) shells out to a real git CLI
    (GitCliRunner.cs) and the agent refuses to advertise the "git" capability
    unless a startup `git --version` probe finds git >= 2.31
    (GitWorkerService.cs). A machine without git therefore runs a Backer that
    looks healthy but silently never picks up git jobs - so the installer
    ships its own git rather than depending on whatever is (not) on the
    service account's PATH.

    MinGit is Git for Windows' redistribution-oriented minimal build. This
    script downloads the pinned release, verifies its SHA-256, and extracts it
    to contrib\git\ - which is .gitignore'd, because 91 MB across ~2000 files
    does not belong in the repository the way the single contrib\rclone.exe
    does.

    Run this once before compiling BackerInstaller.iss. Inno Setup fails the
    compile with "no files found matching" if you forget, which is the
    intended loud failure.

.PARAMETER Force
    Re-download and re-extract even when contrib\git already holds the pinned
    version.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File contrib\fetch-mingit.ps1
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# Pinned, not "latest": the bundled client is part of the shipped product, so
# it changes when we decide to test a new one, never because upstream cut a
# release. Verified 2026-08-22 - `cmd\git.exe --version` reports
# 2.55.0.windows.5, comfortably over the 2.31 floor in
# GitWorkerService._minimumGitVersion.
$MinGitVersion = '2.55.0.5'
$MinGitTag     = 'v2.55.0.windows.5'
$MinGitSha256  = '56D7B226B7693196CFC71FEF26568F536C4A021AB6C37FF2DB4287BED908E96E'
$MinGitUrl     = "https://github.com/git-for-windows/git/releases/download/$MinGitTag/MinGit-$MinGitVersion-64-bit.zip"

# The agent's own floor. Checked here too so a bad pin fails at fetch time
# rather than as a silently missing capability on a user's machine.
$MinimumGitVersion = [Version]'2.31'

$TargetDir = Join-Path $PSScriptRoot 'git'
$GitExe    = Join-Path $TargetDir 'cmd\git.exe'

function Get-GitExeVersion {
    param([string]$Path)

    # Runs the binary rather than trusting the directory's existence: a
    # half-extracted tree has cmd\git.exe but no runtime behind it.
    $output = & $Path --version 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "'$Path --version' exited with $LASTEXITCODE : $output"
    }
    if ($output -notmatch 'version\s+(\d+)\.(\d+)(\.(\d+))?') {
        throw "Could not parse a version out of: $output"
    }
    return [Version]::new([int]$Matches[1], [int]$Matches[2])
}

if ((Test-Path $GitExe) -and (-not $Force)) {
    $existing = & $GitExe --version 2>&1
    if (($LASTEXITCODE -eq 0) -and ($existing -match [regex]::Escape($MinGitTag.TrimStart('v')))) {
        Write-Host "contrib\git already holds the pinned MinGit ($existing). Use -Force to re-fetch."
        exit 0
    }
    Write-Host "contrib\git holds a different build ($existing); replacing it with $MinGitVersion."
}

# Windows PowerShell 5.1 still defaults to TLS 1.0 against some hosts; GitHub
# refuses anything below 1.2.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$scratch = Join-Path ([IO.Path]::GetTempPath()) ("backer-mingit-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null

try {
    $zip = Join-Path $scratch 'MinGit.zip'

    Write-Host "Downloading $MinGitUrl ..."
    # ProgressPreference throttles Invoke-WebRequest badly on large files.
    $previousProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri $MinGitUrl -OutFile $zip -UseBasicParsing
    }
    finally {
        $ProgressPreference = $previousProgress
    }

    $actualSha = (Get-FileHash -Path $zip -Algorithm SHA256).Hash
    if ($actualSha -ne $MinGitSha256) {
        throw ("SHA-256 mismatch for MinGit-$MinGitVersion-64-bit.zip.`n" +
               "  expected $MinGitSha256`n" +
               "  actual   $actualSha`n" +
               "Refusing to unpack it. Either the download was corrupted or the pin is wrong.")
    }
    Write-Host "SHA-256 verified."

    $staging = Join-Path $scratch 'extracted'
    # ZipFile rather than Expand-Archive: ~2000 small files, and 5.1's
    # Expand-Archive takes minutes over what this does in seconds.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $staging)

    $stagedGitExe = Join-Path $staging 'cmd\git.exe'
    if (-not (Test-Path $stagedGitExe)) {
        throw "The archive did not contain cmd\git.exe - MinGit's layout changed, and BackerInstaller.iss's GitPath needs updating with it."
    }

    $stagedVersion = Get-GitExeVersion -Path $stagedGitExe
    if ($stagedVersion -lt $MinimumGitVersion) {
        throw "Pinned MinGit reports $stagedVersion, below the $MinimumGitVersion the agent requires."
    }

    # GPLv2: the licence text travels with the binaries into {app}\contrib\git.
    if (-not (Test-Path (Join-Path $staging 'LICENSE.txt'))) {
        throw "The archive did not contain LICENSE.txt; the installer must not ship MinGit without it."
    }

    # Swap in last, so an interrupted run never leaves a half-tree that a
    # later Inno compile would happily package.
    if (Test-Path $TargetDir) {
        Remove-Item -Path $TargetDir -Recurse -Force
    }
    Move-Item -Path $staging -Destination $TargetDir

    Write-Host "MinGit $stagedVersion unpacked to $TargetDir"
    Write-Host "BackerInstaller.iss will ship it as {app}\contrib\git and point GitWorker:GitPath at cmd\git.exe."
}
finally {
    if (Test-Path $scratch) {
        Remove-Item -Path $scratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}
