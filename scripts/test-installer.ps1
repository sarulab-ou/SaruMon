#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$NsisRoot,
    [string]$WslDistribution
)

$ErrorActionPreference = 'Stop'
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
$env:CI = 'true'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$profile = 'SaruMon-InstallerTest-' + [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $repoRoot "artifacts/installer-tests/$profile"
$installPath = Join-Path $testRoot 'インストール 試験 #100% &'
$unknownDocFile = Join-Path $installPath 'docs/keep-user-note.md'
$notOwned = Join-Path $testRoot 'unowned'
$programs = Join-Path ([Environment]::GetFolderPath('Programs')) $profile
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) "$profile.lnk"
$settingsDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $profile
$defaultInstallPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Programs/$profile"
$settingsSentinel = Join-Path $settingsDirectory 'settings.json'
$appKey = "Software\$profile"
$uninstallKey = "Software\Microsoft\Windows\CurrentVersion\Uninstall\$profile"
$runKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
$markerBackup = $null
$passed = 0

function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Read-Value([string]$Key, [string]$Name) {
    $item = $registry.OpenSubKey($Key)
    try { if ($item) { return $item.GetValue($Name) } } finally { if ($item) { $item.Dispose() } }
}
function Write-Run([string]$Command) {
    $item = $registry.CreateSubKey($runKey)
    try { $item.SetValue($profile, $Command) } finally { $item.Dispose() }
}
function Remove-Run {
    $item = $registry.OpenSubKey($runKey, $true)
    try { if ($item) { $item.DeleteValue($profile, $false) } } finally { if ($item) { $item.Dispose() } }
}
function Production-Snapshot {
    return ConvertTo-Json @{
        InstallDir = (Read-Value 'Software\SaruMon' 'InstallDir')
        Uninstall = (Read-Value 'Software\Microsoft\Windows\CurrentVersion\Uninstall\SaruMon' 'UninstallString')
        Startup = (Read-Value $runKey 'SaruMon')
    } -Compress
}
function Run-Setup([string]$Directory, [bool]$Desktop = $false, [bool]$NoStartup = $false) {
    $resolvedDirectory = [IO.Path]::GetFullPath($Directory)
    $allowedRoot = [IO.Path]::GetFullPath($testRoot).TrimEnd('\') + '\'
    if (!$resolvedDirectory.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installer tests may target only this invocation''s artifacts/installer-tests directory.'
    }
    $info = [Diagnostics.ProcessStartInfo]::new($installerPath)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    # NSIS /D= must be the last, unquoted remainder, including spaces.
    $info.Arguments = '/S ' + $(if ($Desktop) { '/DesktopShortcut ' } else { '' }) +
        $(if ($NoStartup) { '/NoStartup ' } else { '' }) + '/D=' + $resolvedDirectory
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (!$process.WaitForExit(60000)) { throw 'Installer timed out.' }
        return $process.ExitCode
    }
    finally { $process.Dispose() }
}
function Run-Uninstall {
    $copy = Join-Path $testRoot 'test-uninstall.exe'
    Copy-Item -LiteralPath (Join-Path $installPath 'Uninstall.exe') -Destination $copy -Force
    try {
        $info = [Diagnostics.ProcessStartInfo]::new($copy)
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        # _?= prevents NSIS spawning a temporary uninstaller, so this exit code is final.
        $info.Arguments = '/S _?=' + $installPath
        $process = [Diagnostics.Process]::Start($info)
        try {
            if (!$process.WaitForExit(30000)) { throw 'Uninstaller timed out.' }
            return $process.ExitCode
        }
        finally { $process.Dispose() }
    }
    finally { Remove-Item -LiteralPath $copy -Force -ErrorAction SilentlyContinue }
}
function Pass([string]$Description) {
    $script:passed++
    Write-Output "PASS $Description"
}

try {
    # Every external name is generated per run. Never install the production profile in tests.
    Assert ($profile -match '^SaruMon-InstallerTest-[a-f0-9]{32}$') 'Invalid isolated profile.'
    foreach ($path in @($testRoot, $programs, $desktopShortcut, $settingsDirectory, $defaultInstallPath)) {
        Assert (!(Test-Path -LiteralPath $path)) "Test path unexpectedly exists: $path"
    }
    Assert ($null -eq (Read-Value $appKey 'InstallDir')) 'Test registry key unexpectedly exists.'
    $before = Production-Snapshot
    & (Join-Path $PSScriptRoot 'build-installer.ps1') -SkipBuild -NsisRoot $NsisRoot -WslDistribution $WslDistribution -TestProfile $profile
    [xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'src/SaruMon/SaruMon.csproj') -Raw
    $version = [string]$project.Project.PropertyGroup.Version
    $installerPath = Join-Path $repoRoot "artifacts/installer-tests/$profile-$version-Setup-x64.exe"
    New-Item -ItemType Directory -Path $installPath, $settingsDirectory -Force | Out-Null
    [IO.File]::WriteAllText($settingsSentinel, '{"keep":"user settings"}')

    Assert ((Run-Setup $installPath $true) -eq 0) 'Silent installation failed.'
    $exe = Join-Path $installPath 'SaruMon.exe'
    $expectedStartup = '"' + $exe + '" --tray'
    $marker = Join-Path $installPath 'SaruMon.install.ini'
    Assert (Test-Path -LiteralPath $exe -PathType Leaf) 'Installed executable is missing.'
    Assert ((Get-FileHash -LiteralPath $exe).Hash -eq (Get-FileHash -LiteralPath (Join-Path $repoRoot 'artifacts/publish/win-x64/SaruMon.exe')).Hash) 'Installed executable differs from the published payload.'
    foreach ($document in @('development.md', 'releasing.md', 'verification.md', 'third-party-notices.md')) {
        $installedDocument = Join-Path $installPath "docs/$document"
        Assert (Test-Path -LiteralPath $installedDocument -PathType Leaf) "Bundled document is missing: $document"
        Assert ((Get-FileHash -LiteralPath $installedDocument).Hash -eq (Get-FileHash -LiteralPath (Join-Path $repoRoot "docs/$document")).Hash) "Bundled document differs from its source: $document"
    }
    Assert ((Read-Value $appKey 'InstallDir') -eq $installPath) 'InstallDir registration is missing.'
    Assert ((Read-Value $uninstallKey 'DisplayVersion') -eq $version) 'Uninstall version is wrong.'
    Assert ((Read-Value $uninstallKey 'QuietUninstallString') -eq ('"' + (Join-Path $installPath 'Uninstall.exe') + '" /S')) 'Quiet uninstall command is not correctly quoted.'
    Assert ((Read-Value $runKey $profile) -ceq $expectedStartup) 'Default startup registration must use the quoted installed path and --tray.'
    Assert (Test-Path -LiteralPath $desktopShortcut) 'Optional desktop shortcut was not created.'
    $shell = New-Object -ComObject WScript.Shell
    try {
        foreach ($entry in @(@('SaruMon.lnk', 'SaruMon.exe'), @('アンインストール.lnk', 'Uninstall.exe'))) {
            $shortcutPath = Join-Path $programs $entry[0]
            Assert (Test-Path -LiteralPath $shortcutPath) "Start menu shortcut missing: $($entry[0])"
            $shortcut = $shell.CreateShortcut($shortcutPath)
            Assert ($shortcut.TargetPath -eq (Join-Path $installPath $entry[1])) 'Shortcut target is incorrect.'
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
        }
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    Pass 'silent install, payload/documentation, default startup, registration and shortcuts'

    $unknownFile = Join-Path $installPath 'keep-user-file.txt'
    [IO.File]::WriteAllText($unknownFile, 'Preserve this unrelated file.')
    [IO.File]::WriteAllText($unknownDocFile, 'Preserve this user-authored note.')
    Assert ((Run-Setup $installPath) -eq 0) 'Reinstallation into the owned directory failed.'
    Assert ([IO.File]::ReadAllText($unknownFile) -eq 'Preserve this unrelated file.') 'Reinstallation modified an unrelated file.'
    Assert ([IO.File]::ReadAllText($unknownDocFile) -eq 'Preserve this user-authored note.') 'Reinstallation modified an unrelated documentation file.'
    Pass 'same-directory reinstall preserves unrelated files'

    # This reproduces the Windows deny-write condition of an executing image without
    # starting the production app, changing its settings, or contacting its singleton.
    $locked = [IO.File]::Open($exe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        Assert ((Run-Setup $installPath) -eq 2) 'Installer did not reject a locked/running executable.'
        Assert ((Run-Uninstall) -eq 2) 'Uninstaller did not reject a locked/running executable.'
    }
    finally { $locked.Dispose() }
    Assert (Test-Path -LiteralPath $exe) 'Running-file refusal removed the executable.'
    Pass 'install and uninstall refuse an executable held with a Windows deny-write lock'

    $markerBackup = [IO.File]::ReadAllText($marker)
    [IO.File]::WriteAllText($marker, "[Install]`r`nProduct=unrelated-product`r`n")
    Assert ((Run-Uninstall) -eq 2) 'Uninstaller accepted an invalid ownership marker.'
    Assert ((Run-Setup $installPath) -eq 2) 'Installer accepted a foreign ownership marker.'
    Assert (Test-Path -LiteralPath $exe) 'Invalid-marker refusal removed the executable.'
    [IO.File]::WriteAllText($marker, $markerBackup)
    $markerBackup = $null
    Pass 'invalid ownership marker blocks both setup and uninstall'

    Assert ((Read-Value $runKey $profile) -ceq $expectedStartup) 'Reinstall or rejected operations changed the default startup command.'
    $uninstallCode = Run-Uninstall
    Assert ($uninstallCode -eq 0) "Uninstallation failed (exit $uninstallCode; files: $((Get-ChildItem -LiteralPath $installPath -Force -ErrorAction SilentlyContinue).Name -join ', '))."
    Assert (Test-Path -LiteralPath $unknownFile) 'Uninstaller deleted an unrelated file.'
    Assert ([IO.File]::ReadAllText($unknownDocFile) -eq 'Preserve this user-authored note.') 'Uninstaller deleted or modified an unrelated documentation file.'
    Assert ([IO.File]::ReadAllText($settingsSentinel) -eq '{"keep":"user settings"}') 'Uninstaller modified user settings.'
    foreach ($name in @('SaruMon.exe', 'README.md', 'Uninstall.exe', 'SaruMon.install.ini', 'docs/development.md', 'docs/releasing.md', 'docs/verification.md', 'docs/third-party-notices.md')) {
        Assert (!(Test-Path -LiteralPath (Join-Path $installPath $name))) "Uninstaller left a known file: $name"
    }
    Assert ($null -eq (Read-Value $runKey $profile)) 'Own startup command was not removed.'
    Assert ($null -eq (Read-Value $appKey 'InstallDir')) 'InstallDir registration was not removed.'
    Assert ($null -eq (Read-Value $uninstallKey 'DisplayName')) 'Uninstall registration was not removed.'
    Assert (!(Test-Path -LiteralPath $programs)) 'Start menu entries were not removed.'
    Assert (!(Test-Path -LiteralPath $desktopShortcut)) 'Desktop shortcut was not removed.'
    Remove-Item -LiteralPath $unknownFile, $unknownDocFile
    Remove-Item -LiteralPath (Join-Path $installPath 'docs')
    Remove-Item -LiteralPath $installPath
    Pass 'uninstall removes only known files and its own startup command; user files/settings survive'

    Assert ((Run-Setup $installPath $false $true) -eq 0) 'Installation with /NoStartup failed.'
    Assert (!(Test-Path -LiteralPath $desktopShortcut)) 'Desktop shortcut must be opt-in.'
    Assert ($null -eq (Read-Value $runKey $profile)) '/NoStartup registered a startup command on a fresh install.'
    Pass 'silent /NoStartup and default desktop option'

    $otherStartup = '"C:\Portable\SaruMon.exe" --tray'
    Write-Run $otherStartup
    Assert ((Run-Setup $installPath $false $true) -eq 0) 'Startup opt-out reinstallation failed.'
    Assert ((Read-Value $runKey $profile) -ceq $otherStartup) 'Startup opt-out removed another copy''s startup command.'
    Pass 'startup opt-out preserves another copy''s startup command'

    Assert ((Run-Setup $installPath) -eq 0) 'Reinstallation with startup enabled failed.'
    Assert ((Read-Value $runKey $profile) -ceq $expectedStartup) 'Default-selected startup did not register this copy.'
    Assert ((Run-Setup $installPath $false $true) -eq 0) 'Reinstallation with startup disabled failed.'
    Assert ($null -eq (Read-Value $runKey $profile)) 'Startup opt-out did not remove this copy''s exact command.'
    Pass 'startup can be enabled and explicitly disabled for the same installed path'

    Write-Run $otherStartup
    Assert ((Run-Uninstall) -eq 0) 'Second uninstallation failed.'
    Assert ((Read-Value $runKey $profile) -eq $otherStartup) 'Uninstaller removed another copy''s startup command.'
    Remove-Run
    Assert (!(Test-Path -LiteralPath $installPath)) 'Uninstaller did not remove an otherwise empty installation folder.'
    Pass 'uninstall preserves another copy''s startup command'

    Assert ((Run-Setup $installPath) -eq 0) 'Partial-installation fixture could not be installed.'
    Remove-Item -LiteralPath (Join-Path $installPath 'SaruMon.exe'), (Join-Path $installPath 'README.md'), (Join-Path $installPath 'docs/development.md')
    Assert ((Run-Uninstall) -eq 0) 'Uninstaller could not clean up an installation with missing payload files.'
    Assert (!(Test-Path -LiteralPath $installPath)) 'Partial-installation uninstall left known files.'
    Assert ($null -eq (Read-Value $uninstallKey 'DisplayName')) 'Partial-installation uninstall left registration.'
    Pass 'uninstall recovers an owned installation with missing payload files'

    New-Item -ItemType Directory -Path $notOwned -Force | Out-Null
    $existing = Join-Path $notOwned 'existing.txt'
    [IO.File]::WriteAllText($existing, 'Untouched existing file')
    Assert ((Run-Setup $notOwned) -eq 2) 'Installer accepted an unrelated nonempty folder.'
    Assert ([IO.File]::ReadAllText($existing) -eq 'Untouched existing file') 'Installer changed the unrelated file.'
    Assert (!(Test-Path -LiteralPath (Join-Path $notOwned 'SaruMon.exe'))) 'Installer wrote a payload into an unrelated directory.'
    # Root rejection is reviewed in ValidateInstallDirectory. Never execute a test
    # against a real drive root: a regression would write outside this fixture.
    Pass 'nonempty unowned directory protection'
    Assert ((Production-Snapshot) -eq $before) 'Production SaruMon registration changed during isolated tests.'
    Assert (!(Test-Path -LiteralPath $defaultInstallPath)) 'Installer unexpectedly used the default LocalAppData installation directory.'
    Pass 'production SaruMon registration remains unchanged'
    Write-Output "Installer integration tests passed: $passed groups."
}
finally {
    # Cleanup is restricted to known files and this invocation's random profile.
    # No recursive directory deletion: unexpected files are left for inspection.
    if ($markerBackup -and (Test-Path -LiteralPath (Join-Path $installPath 'SaruMon.install.ini'))) {
        [IO.File]::WriteAllText((Join-Path $installPath 'SaruMon.install.ini'), $markerBackup)
    }
    if (Test-Path -LiteralPath (Join-Path $installPath 'Uninstall.exe')) {
        try { [void](Run-Uninstall) } catch { Write-Warning "Test install remains at $installPath : $_" }
    }
    Remove-Run
    foreach ($file in @($settingsSentinel, (Join-Path $notOwned 'existing.txt'), (Join-Path $installPath 'keep-user-file.txt'), $unknownDocFile)) {
        if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file -Force }
    }
    foreach ($directory in @($settingsDirectory, $notOwned, (Join-Path $installPath 'docs'), $installPath, $testRoot)) {
        if ((Test-Path -LiteralPath $directory -PathType Container) -and !(Get-ChildItem -LiteralPath $directory -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $directory
        }
    }
    $registry.Dispose()
}
