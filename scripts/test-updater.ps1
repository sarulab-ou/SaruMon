param(
    [string]$Executable = (Join-Path $PSScriptRoot '../artifacts/publish/win-x64/SaruMon.exe')
)
$ErrorActionPreference = 'Stop'
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
$env:CI = 'true'

# Integration test for the embedded, application-owned PowerShell updater. All writes
# are confined to this run's GUID directory; the supplied executable is read-only.
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$inputExe = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $inputExe -PathType Leaf)) { throw "Build a portable release first: $inputExe" }
$inputHash = (Get-FileHash -LiteralPath $inputExe -Algorithm SHA256).Hash
$source = Get-Content -LiteralPath (Join-Path $repository 'src/SaruMon/Updates/UpdateService.cs') -Raw
$match = [regex]::Match($source, '(?s)private const string InstallerScript = """\r?\n(.*?)\r?\n""";')
if (-not $match.Success) { throw 'Embedded updater script was not found.' }
$installerScript = $match.Groups[1].Value
$testRoot = Join-Path $repository ('artifacts/updater-smoke/' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot -Force
$powershell = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'

function Start-HiddenScript([string]$ScriptPath, [string]$ParameterName, [string]$ParameterValue) {
    # Windows file names cannot contain double quotes; quote paths passed to PS 5.1.
    $arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
        '-File', ('"' + $ScriptPath + '"'), ('-' + $ParameterName), ('"' + $ParameterValue + '"'))
    return Start-Process -FilePath $powershell -ArgumentList $arguments -WindowStyle Hidden -PassThru
}

function Invoke-InstallerCase([string]$CaseName, [bool]$Mismatch) {
    $caseRoot = Join-Path $testRoot $CaseName
    $targetFolder = Join-Path $caseRoot 'target'
    $stageFolder = Join-Path $caseRoot 'stage'
    $null = New-Item -ItemType Directory -Path $targetFolder, $stageFolder -Force
    $targetExe = Join-Path $targetFolder 'SaruMon.exe'
    $stageExe = Join-Path $stageFolder 'SaruMon.exe'
    Copy-Item -LiteralPath $inputExe -Destination $targetExe
    Copy-Item -LiteralPath $inputExe -Destination $stageExe
    # A PE overlay leaves the original binary intact but gives the installed copy a
    # distinct hash, so replacement is observable rather than copying equal files.
    $stream = [IO.File]::Open($targetExe, [IO.FileMode]::Append, [IO.FileAccess]::Write)
    try {
        $marker = [Text.Encoding]::ASCII.GetBytes('SaruMon updater integration test overlay')
        $stream.Write($marker, 0, $marker.Length)
    } finally { $stream.Dispose() }
    $beforeHash = (Get-FileHash -LiteralPath $targetExe -Algorithm SHA256).Hash
    if ($beforeHash -eq $inputHash) { throw 'Fixture hashes must differ.' }

    $releaseParent = Join-Path $caseRoot 'parent-exit'
    $dummyScript = Join-Path $caseRoot 'dummy-parent.ps1'
    @'
param([string]$ReleasePath)
$timeout = [Diagnostics.Stopwatch]::StartNew()
while (-not [IO.File]::Exists($ReleasePath) -and $timeout.Elapsed.TotalSeconds -lt 30) {
    Start-Sleep -Milliseconds 100
}
'@ | Set-Content -LiteralPath $dummyScript -Encoding UTF8
    $dummy = Start-HiddenScript $dummyScript 'ReleasePath' $releaseParent
    $helper = $null
    try {
        $configPath = Join-Path $stageFolder 'install.json'
        $ready = Join-Path $stageFolder 'ready'
        $log = Join-Path $caseRoot 'update.log'
        $scriptPath = Join-Path $stageFolder 'install.ps1'
        $script = $installerScript
        if ($Mismatch) {
            # Exercise the unchanged failure/rollback code without putting up a
            # deliberate error dialog on the user's desktop. Only its UI sink differs.
            $dialogLine = ($script -split '\r?\n' | Where-Object { $_.Contains('[System.Windows.Forms.MessageBox]::Show(') })
            if (@($dialogLine).Count -ne 1) { throw 'Expected exactly one error dialog line.' }
            $script = $script.Replace([string]$dialogLine, '    [IO.File]::WriteAllText([IO.Path]::Combine($folder, "error-ui-suppressed.txt"), $message)')
        }
        [IO.File]::WriteAllText($scriptPath, $script, [Text.UTF8Encoding]::new($true))
        $currentHash = $beforeHash
        if ($Mismatch) { $currentHash = '0' * 64 }
        @{
            Target = $targetExe; Source = $stageExe; SourceHash = $inputHash; CurrentHash = $currentHash
            ParentId = $dummy.Id; ParentStartTicks = $dummy.StartTime.ToUniversalTime().Ticks.ToString()
            Ready = $ready; Log = $log
        } | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding UTF8
        $helper = Start-HiddenScript $scriptPath 'ConfigPath' $configPath
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $ready)) {
            if ($helper.HasExited -or $timer.Elapsed.TotalSeconds -gt 10) { throw "Helper did not become ready ($CaseName)." }
            Start-Sleep -Milliseconds 100
        }
        if ((Get-FileHash -LiteralPath $targetExe -Algorithm SHA256).Hash -ne $beforeHash) {
            throw 'Installer modified the target before its parent exited.'
        }
        [IO.File]::WriteAllText($releaseParent, 'exit')
        if (-not $helper.WaitForExit(20000)) { throw "Helper did not exit ($CaseName)." }
        if ($helper.ExitCode -ne 0) { throw "Helper exit code: $($helper.ExitCode) ($CaseName)." }
        $afterHash = (Get-FileHash -LiteralPath $targetExe -Algorithm SHA256).Hash
        $logText = Get-Content -LiteralPath $log -Raw
        if ($Mismatch) {
            if ($afterHash -ne $beforeHash -or $logText -notmatch 'installed executable changed') {
                throw 'Hash mismatch did not preserve the installed executable.'
            }
            Write-Output 'PASS: changed-target rejection preserved the executable (error UI suppressed).'
            # The failure path deliberately relaunches the preserved executable.
            # Observe its startup before finally stopping this fixture's process.
            $fallbackTimer = [Diagnostics.Stopwatch]::StartNew()
            do {
                $fallback = @(Get-Process -Name SaruMon -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $targetExe })
                if ($fallback.Count -eq 0) { Start-Sleep -Milliseconds 100 }
            } while ($fallback.Count -eq 0 -and $fallbackTimer.Elapsed.TotalSeconds -lt 5)
        } else {
            if ($afterHash -ne $inputHash -or $logText -notmatch 'Update succeeded') { throw 'Replacement or restart failed.' }
            if (@(Get-ChildItem -LiteralPath $targetFolder -Force -Filter '.SaruMon-*.exe').Count -ne 0) {
                throw 'Successful installation left an unexpected candidate or backup.'
            }
            Write-Output 'PASS: parent-exit handshake, atomic EXE replacement, cleanup, and restart launch.'
            $startupTimer = [Diagnostics.Stopwatch]::StartNew()
            do {
                $runningInstance = @(Get-Process -Name SaruMon -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 })
                if ($runningInstance.Count -eq 0) { Start-Sleep -Milliseconds 100 }
            } while ($runningInstance.Count -eq 0 -and $startupTimer.Elapsed.TotalSeconds -lt 10)
            if ($runningInstance.Count -eq 0) { throw 'The restarted application did not open its window.' }
            $startup = Start-Process -FilePath $targetExe -WorkingDirectory $targetFolder -WindowStyle Hidden -PassThru
            try {
                if (-not $startup.WaitForExit(15000)) { throw 'Updated executable did not complete single-instance activation.' }
                if ($startup.ExitCode -ne 0) { throw "Updated executable startup exit code: $($startup.ExitCode)." }
                Write-Output 'PASS: updated executable opened its window and activated the existing instance (exit 0).'
            } finally { $startup.Dispose() }
        }
    } finally {
        if (-not $dummy.HasExited) { $dummy.Kill(); $dummy.WaitForExit() }
        $dummy.Dispose()
        if ($null -ne $helper) {
            if (-not $helper.HasExited) { $helper.Kill(); $helper.WaitForExit() }
            $helper.Dispose()
        }
        # Only processes whose exact executable is our disposable fixture may stop.
        Get-Process -Name SaruMon -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $targetExe } | ForEach-Object {
            if (-not $_.HasExited) { $_.Kill(); $_.WaitForExit() }
        }
    }
}

Invoke-InstallerCase 'successful-replacement' $false
Invoke-InstallerCase 'changed-target-rejection' $true
if ((Get-FileHash -LiteralPath $inputExe -Algorithm SHA256).Hash -ne $inputHash) { throw 'Original input executable changed.' }
Write-Output "PASS: original executable unchanged. Evidence: $testRoot"
