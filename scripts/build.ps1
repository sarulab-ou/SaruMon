[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
$env:CI = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $repoRoot 'artifacts'
$publishDirectory = Join-Path $artifactRoot 'publish/win-x64'
$appProject = Join-Path $repoRoot 'src/SaruMon/SaruMon.csproj'
$testProject = Join-Path $repoRoot 'tests/SaruMon.Tests/SaruMon.Tests.csproj'

function Invoke-DotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed (exit code $LASTEXITCODE): $($Arguments -join ' ')"
    }
}

Push-Location -LiteralPath $repoRoot
try {
    Invoke-DotNet -Arguments @('run', '--project', $testProject, '--configuration', $Configuration)
    Invoke-DotNet -Arguments @(
        'publish', $appProject, '--configuration', $Configuration,
        '--runtime', 'win-x64', '--self-contained', 'true', '--output', $publishDirectory,
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false'
    )

    $executable = Join-Path $publishDirectory 'SaruMon.exe'
    if (!(Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Published executable is missing: $executable"
    }

    # Package an explicit file list so stale publish output cannot enter a release.
    $zipPath = Join-Path $artifactRoot 'SaruMon-win-x64.zip'
    $temporaryZip = Join-Path $artifactRoot ('.SaruMon-' + [Guid]::NewGuid().ToString('N') + '.zip')
    try {
        Compress-Archive -LiteralPath @($executable, (Join-Path $repoRoot 'README.md'), (Join-Path $repoRoot 'docs')) -DestinationPath $temporaryZip -CompressionLevel Optimal
        Move-Item -LiteralPath $temporaryZip -Destination $zipPath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryZip -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryZip -Force
        }
    }

    $sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$zipPath.sha256", "$sha256  SaruMon-win-x64.zip`n", [Text.UTF8Encoding]::new($false))
    Write-Output "Executable: $executable"
    Write-Output "Archive:    $zipPath"
    Write-Output "SHA-256:    $sha256"
}
finally {
    Pop-Location
}
