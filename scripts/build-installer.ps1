#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string]$NsisRoot,
    [string]$WslDistribution,
    [ValidatePattern('^SaruMon-InstallerTest-[a-f0-9]{32}$')]
    [string]$TestProfile
)

$ErrorActionPreference = 'Stop'
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
$env:CI = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Quote-Bash([string]$Value) { return "'" + $Value.Replace("'", "'\''") + "'" }
function ConvertTo-WslPath([string]$Value) {
    $absolute = [IO.Path]::GetFullPath($Value, $repoRoot)
    if ($absolute -notmatch '^([A-Za-z]):\\(.*)$') { throw 'A Windows drive path is required for WSL.' }
    return '/mnt/' + $Matches[1].ToLowerInvariant() + '/' + $Matches[2].Replace('\', '/')
}

if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
[xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'src/SaruMon/SaruMon.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Installer requires a numeric major.minor.patch project Version.' }
$artifactRoot = Join-Path $repoRoot 'artifacts'
$payload = Join-Path $artifactRoot 'publish/win-x64'
if (!(Test-Path -LiteralPath (Join-Path $payload 'SaruMon.exe') -PathType Leaf)) { throw 'Published SaruMon.exe is missing. Run scripts/build.ps1 first.' }
$payloadVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'SaruMon.exe')).ProductVersion
if (!$payloadVersion -or $payloadVersion.Split('+')[0] -ne $version) {
    throw "Published EXE version '$payloadVersion' differs from project version '$version'. Run scripts/build.ps1 before packaging."
}
if (!(Test-Path -LiteralPath (Join-Path $repoRoot 'assets/SaruMon.ico') -PathType Leaf)) { throw 'assets/SaruMon.ico is missing.' }
$profile = if ($TestProfile) { $TestProfile } else { 'SaruMon' }
$outputDirectory = if ($TestProfile) { Join-Path $artifactRoot 'installer-tests' } else { $artifactRoot }
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$outputFile = Join-Path $outputDirectory "$profile-$version-Setup-x64.exe"

$compiler = 'makensis'
$environmentPrefix = ''
if (!$NsisRoot -and (Test-Path -LiteralPath (Join-Path $repoRoot '.tools/nsis-root/usr/bin/makensis'))) {
    $NsisRoot = Join-Path $repoRoot '.tools/nsis-root'
}
if ($NsisRoot) {
    $NsisRoot = [IO.Path]::GetFullPath($NsisRoot, $repoRoot)
    $compilerPath = Join-Path $NsisRoot 'usr/bin/makensis'
    if (!(Test-Path -LiteralPath $compilerPath -PathType Leaf)) { throw "NSIS compiler is missing under $NsisRoot" }
    $compiler = Quote-Bash (ConvertTo-WslPath $compilerPath)
    $environmentPrefix = 'NSISDIR=' + (Quote-Bash (ConvertTo-WslPath (Join-Path $NsisRoot 'usr/share/nsis'))) + ' '
}
$arguments = @(
    '-V3', '-WX', "-DPRODUCT_VERSION=$version", "-DINSTALLER_PROFILE=$profile",
    "-DPAYLOAD_DIR=$(ConvertTo-WslPath $payload)", "-DOUTPUT_FILE=$(ConvertTo-WslPath $outputFile)",
    'installer/SaruMon.nsi'
) | ForEach-Object { Quote-Bash $_ }
$command = 'cd ' + (Quote-Bash (ConvertTo-WslPath $repoRoot)) + ' && ' + $environmentPrefix + $compiler + ' ' + ($arguments -join ' ')
$wslArguments = @()
if ($WslDistribution) { $wslArguments += @('--distribution', $WslDistribution) }
$wslArguments += @('bash', '-lc', $command)
& wsl.exe @wslArguments
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed. Run scripts/install-installer-tools.ps1 if NSIS is missing.' }
if (!(Test-Path -LiteralPath $outputFile -PathType Leaf)) { throw 'NSIS did not produce the expected setup executable.' }
$checksum = (Get-FileHash -LiteralPath $outputFile -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumFile = Join-Path $outputDirectory "$profile-$version-SHA256SUMS.txt"
[IO.File]::WriteAllText($checksumFile, "$checksum  $([IO.Path]::GetFileName($outputFile))`n", [Text.UTF8Encoding]::new($false))
Write-Output "Installer: $outputFile"
Write-Output "Checksums: $checksumFile"
