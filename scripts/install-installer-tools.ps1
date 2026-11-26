#requires -Version 7.0
[CmdletBinding()]
param([string]$WslDistribution)

$ErrorActionPreference = 'Stop'
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
$env:CI = 'true'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($repoRoot -notmatch '^([A-Za-z]):\\(.*)$') { throw 'A Windows drive path is required for WSL.' }
$wslRoot = '/mnt/' + $Matches[1].ToLowerInvariant() + '/' + $Matches[2].Replace('\', '/')
$quotedRoot = "'" + $wslRoot.Replace("'", "'\''") + "'"
# Official Ubuntu 24.04 packages, fixed versions, extracted locally without sudo.
$command = "cd $quotedRoot && mkdir -p .tools/nsis-packages .tools/nsis-root && cd .tools/nsis-packages && apt-get download nsis=3.09-4ubuntu1 nsis-common=3.09-4ubuntu1 && dpkg-deb -x nsis_3.09-4ubuntu1_amd64.deb ../nsis-root && dpkg-deb -x nsis-common_3.09-4ubuntu1_all.deb ../nsis-root && NSISDIR='../nsis-root/usr/share/nsis' ../nsis-root/usr/bin/makensis -VERSION"
$wslArguments = @()
if ($WslDistribution) { $wslArguments += @('--distribution', $WslDistribution) }
$wslArguments += @('bash', '-lc', $command)
& wsl.exe @wslArguments
if ($LASTEXITCODE -ne 0) { throw 'NSIS preparation failed. Check WSL Ubuntu 24.04 and access to the official Ubuntu package mirror.' }
