param(
    [Parameter(Mandatory = $true)][string]$Packages,
    [Parameter(Mandatory = $true)][string]$Version
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Run this installation test only on an isolated GitHub-hosted Windows runner.'
}
$Packages = (Resolve-Path $Packages).Path
$installed = Join-Path $env:LOCALAPPDATA 'Programs\Root Six Player'
$launcher = Join-Path $installed 'RootSixPlayer.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Root Six Player.lnk'
$registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RootSixPlayer'
$work = Join-Path $env:RUNNER_TEMP 'Root Installer Tests'
New-Item -ItemType Directory -Path $work -Force | Out-Null
if (Test-Path $installed) { throw "Refusing to overwrite an existing installation: $installed" }

function Wait-Success($process) {
    if (!$process.WaitForExit(60000)) { throw "Process $($process.Id) did not exit in 60 seconds." }
    $process.Refresh()
    if ($process.ExitCode -ne 0) { throw "Process failed with exit code $($process.ExitCode)." }
}
function Install($file) {
    Wait-Success (Start-Process -FilePath $file -ArgumentList '/S' -PassThru)
}
function Uninstall {
    Wait-Success (Start-Process -FilePath (Join-Path $installed 'Uninstall.exe') -ArgumentList '/S' -PassThru)
    # The normal NSIS uninstaller continues from a temporary copy.
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ((Test-Path $launcher) -or (Test-Path (Join-Path $installed 'Uninstall.exe')) -or (Test-Path $registry)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Uninstall did not finish removing its files and registration.' }
        Start-Sleep -Milliseconds 100
    }
}
function Wait-File($path) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (!(Test-Path $path)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "Timed out waiting for $path" }
        Start-Sleep -Milliseconds 100
    }
}
function Check-Shortcut {
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    if ($link.TargetPath -ne $launcher) { throw "Shortcut points to $($link.TargetPath), expected $launcher" }
}

foreach ($line in Get-Content (Join-Path $Packages 'SHA256SUMS')) {
    $digest, $name = $line -split '  ', 2
    if ((Get-FileHash -Algorithm SHA256 (Join-Path $Packages $name)).Hash.ToLowerInvariant() -ne $digest) {
        throw "Package checksum mismatch: $name"
    }
}
Install (Join-Path $Packages "RootSixPlayer-$Version-Setup.exe")
Check-Shortcut
if ((Get-ItemProperty $registry).DisplayVersion -ne $Version) { throw 'Installed version is not registered.' }
$output = Join-Path $work 'version.txt'
Wait-Success (Start-Process -FilePath $launcher -ArgumentList '--headless --version' -RedirectStandardOutput $output -PassThru)
if ((Get-Content $output -Raw).Trim() -ne $Version) { throw 'Installed executable has the wrong version.' }
$notices = Join-Path $work 'notices'
Wait-Success (Start-Process -FilePath $launcher -ArgumentList "--headless --licenses `"$notices`"" -PassThru)
foreach ($name in @('THIRD-PARTY-NOTICES.txt', 'dependency-sources.zip')) {
    if (!(Test-Path (Join-Path $notices $name)) -or (Get-Item (Join-Path $notices $name)).Length -eq 0) {
        throw "Missing bundled notices: $name"
    }
}
# Uninstall must preserve both player data and unrelated files in the app folder.
$saved = Join-Path $env:LOCALAPPDATA 'RootSixPlayer\installer-test-save.txt'
New-Item -ItemType Directory -Path (Split-Path $saved) -Force | Out-Null
Set-Content $saved 'saved match fixture'
$unrelated = Join-Path $installed 'keep.txt'
Set-Content $unrelated 'keep'
Uninstall
if ((Get-Content $saved -Raw).Trim() -ne 'saved match fixture' -or !(Test-Path $unrelated)) { throw 'Uninstall removed player data or unrelated files.' }
if ((Test-Path $launcher) -or (Test-Path $shortcut) -or (Test-Path $registry)) { throw 'Uninstall left the app, shortcut, or registration behind.' }
Remove-Item $unrelated
Remove-Item $installed

# Use the exact production NSIS template with harmless old/new launchers.
$compiler = (Get-Command makensis -ErrorAction SilentlyContinue).Source
if (!$compiler) { $compiler = "${env:ProgramFiles(x86)}\NSIS\makensis.exe" }
$icon = (Resolve-Path 'launcher/assets/launcher.ico').Path
$template = (Resolve-Path 'packaging/windows/installer.nsi').Path
foreach ($number in @(1, 2)) {
    $binary = Join-Path $work "fixture$number.exe"
    & go build -p 1 -ldflags "-X main.version=fixture$number" -o $binary ./tools/installer-fixture/main.go
    if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
    & $compiler /V2 "/DVERSION=0.0.$number" "/DLAUNCHER=$binary" "/DOUTPUT=$work\setup$number.exe" "/DICON=$icon" $template
    if ($LASTEXITCODE -ne 0) { throw 'Fixture installer compilation failed.' }
}
Install (Join-Path $work 'setup1.exe')
$ready = Join-Path $work 'ready'
$release = Join-Path $work 'release'
$env:ROOT_INSTALLER_SMOKE_LOG = Join-Path $work 'restarted'
$holder = Start-Process -FilePath $launcher -ArgumentList "--hold `"$ready`" `"$release`"" -PassThru
try {
    Wait-File $ready
    $update = Start-Process -FilePath (Join-Path $work 'setup2.exe') -ArgumentList "/S /UPDATE /PARENT=$($holder.Id)" -PassThru
    Start-Sleep -Seconds 2
    if ($update.HasExited) { throw 'Update did not wait for the running launcher.' }
    if ((Get-FileHash $launcher).Hash -ne (Get-FileHash (Join-Path $work 'fixture1.exe')).Hash) { throw 'Update replaced the launcher before its parent exited.' }
    Set-Content $release 'exit'
    Wait-Success $holder
    Wait-Success $update
    Wait-File $env:ROOT_INSTALLER_SMOKE_LOG
    if ((Get-Content $env:ROOT_INSTALLER_SMOKE_LOG -Raw) -ne 'fixture2') { throw 'Update did not restart the new launcher.' }
    if ((Get-FileHash $launcher).Hash -ne (Get-FileHash (Join-Path $work 'fixture2.exe')).Hash) { throw 'Update did not replace the installed executable.' }
    Check-Shortcut
    if ((Get-ItemProperty $registry).DisplayVersion -ne '0.0.2') { throw 'Update did not update registration.' }
    if (Get-ChildItem $installed -Filter '*.previous') { throw 'Update left old executables behind.' }
    Uninstall
    if ((Get-Content $saved -Raw).Trim() -ne 'saved match fixture') { throw 'Update changed saved data.' }
} finally {
    if (!$holder.HasExited) { $holder.Kill(); $holder.WaitForExit() }
}
Write-Host 'Installed package, checksum, notices, shortcut, waiting for exit, replacement, restart, and data-preserving uninstall passed.'
