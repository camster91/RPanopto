# Installs Panopto Scheduler for the current user only. No administrator needed.
#
# Read this before allowing it: it is a plain script on purpose.
#
#   * It is NOT an .exe or an .msi. The endpoint protection on these
#     workstations deletes self-extracting single-file executables on execution,
#     which is the shape every packaged installer has, and an .msi would need an
#     administrator. A script is also the version of this that can be read.
#   * It installs under %LOCALAPPDATA%\Programs, creates two shortcuts, and
#     writes EXACTLY ONE registry key - under HKCU, never HKLM - so that
#     Settings > Apps can list and remove it.
#   * It installs no service, no driver, no scheduled task, and no startup
#     entry.
#   * The application it installs writes nothing to the registry and nothing to
#     the folder it is installed in. Everything it saves goes to
#     %USERPROFILE%\.panopto-scheduler\ and is per-user.
#
# Usage:
#   .\install.ps1              install, with Start Menu and Desktop shortcuts
#   .\install.ps1 -NoDesktop   install without the Desktop shortcut
#   .\install.ps1 -Force       proceed even from an elevated prompt
#
# Normally reached by double-clicking Install.cmd beside this file.

[CmdletBinding()]
param(
    [switch] $NoDesktop,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$AppName = 'Panopto Scheduler'
$ExeName = 'PanoptoScheduler.App.exe'
$Publisher = 'Rotman School of Management'

function Fail([string] $Message) {
    Write-Host ''
    Write-Host "  Cannot install: $Message" -ForegroundColor Red
    Write-Host ''
    exit 1
}

$source = $PSScriptRoot
if ([string]::IsNullOrEmpty($source)) { $source = (Get-Location).Path }

# ---------------------------------------------------------------- the package

if (-not (Test-Path -LiteralPath (Join-Path $source $ExeName))) {
    Fail "there is no $ExeName in $source. Run this from the folder you unzipped, beside the application."
}

# Without this every person is stopped on first run by a dialog asking for a
# Panopto client id and secret, which they have no way to know. That is the
# entire reason the file is packaged. Failing here, once, beats failing on
# nineteen machines later.
if (-not (Test-Path -LiteralPath (Join-Path $source 'defaults.json'))) {
    Fail "defaults.json is missing from $source, so every person would be asked for a Panopto client id and secret they cannot know. Use the package as published rather than a folder that has been edited."
}

# ------------------------------------------------------------ can we do this?

# A per-user install run elevated AS SOMEONE ELSE -- the IT account typed into
# a UAC prompt over the person's shoulder -- installs into that account's
# %LOCALAPPDATA%, not the person's, which is a silent and confusing outcome.
# Refuse that, and say why.
#
# Being an administrator is not the problem by itself, and refusing on that
# alone (as this once did) broke two ordinary cases: an account that is an
# administrator on a machine with UAC turned off, where every process runs
# with the admin token, and someone who elevated their own account. Both
# write to their own profile, which is the right one; under the old rule every
# install from them failed, and so did every uninstall from Settings > Apps,
# which passes no -Force. So the test is whether the account this runs as is
# the account signed in to the desktop.
#
# The desktop's account is read from the owner of explorer.exe in this
# session: Explorer is the desktop, so it runs as whoever is sitting at it,
# whatever account an elevation prompt switched this window to. When that
# cannot be read -- no Explorer (a replaced shell), WMI unavailable, or two
# owners that disagree -- the old rule stands: an elevated run is refused,
# because "probably fine" is not good enough to guess at someone's profile.
function Get-DesktopUserSid {
    try {
        $session = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
        $owners = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'explorer.exe' AND SessionId = $session" -ErrorAction Stop |
            ForEach-Object { Invoke-CimMethod -InputObject $_ -MethodName GetOwnerSid -ErrorAction Stop } |
            Where-Object { $_.ReturnValue -eq 0 -and $_.Sid } |
            ForEach-Object { $_.Sid } |
            Select-Object -Unique)
        if ($owners.Count -eq 1) { return $owners[0] }
    } catch { }
    return $null
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -and -not $Force) {
    $desktopSid = Get-DesktopUserSid
    if (-not $desktopSid) {
        Fail "this is running from an elevated (administrator) prompt, and the account signed in to this desktop could not be determined, so it might install into the administrator's profile rather than your own. Close it and run Install.cmd normally. Pass -Force if you do mean to install for $($identity.Name)."
    }
    if ($desktopSid -ne $identity.User.Value) {
        Fail "this is running as $($identity.Name), not as the account signed in to this desktop, so it would install into $($identity.Name)'s profile rather than your own. Close it and run Install.cmd normally, without 'Run as administrator'. Pass -Force if you do mean to install for $($identity.Name)."
    }
}

# Replacing files underneath a running instance is the one way to leave a broken
# install, and killing someone's session mid-booking is worse than asking them
# to close it.
$running = @(Get-Process -Name 'PanoptoScheduler.App' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Fail "$AppName is running. Close it and run this again."
}

# --------------------------------------------------------------- put it there

$programs = Join-Path $env:LOCALAPPDATA 'Programs'
$target = Join-Path $programs $AppName
$staging = Join-Path $programs ('.' + $AppName + '.installing')
$retired = Join-Path $programs ('.' + $AppName + '.retired')

New-Item -ItemType Directory -Path $programs -Force | Out-Null
foreach ($leftover in @($staging, $retired)) {
    if (Test-Path -LiteralPath $leftover) { Remove-Item -LiteralPath $leftover -Recurse -Force }
}

Write-Host ''
Write-Host "  Installing $AppName ..."

# Copy to a staging folder first and then swap, so a failure halfway through
# leaves the previous version intact rather than a half-replaced one.
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $staging -Recurse -Force

if (-not (Test-Path -LiteralPath (Join-Path $staging $ExeName))) {
    Fail "the files could not be copied into $staging."
}

$hadPrevious = Test-Path -LiteralPath $target
if ($hadPrevious) { Move-Item -LiteralPath $target -Destination $retired }

try {
    Move-Item -LiteralPath $staging -Destination $target
} catch {
    if ($hadPrevious -and (Test-Path -LiteralPath $retired) -and -not (Test-Path -LiteralPath $target)) {
        Move-Item -LiteralPath $retired -Destination $target
        Write-Host '  the previous version was put back' -ForegroundColor Yellow
    }
    Fail "the new files could not be put in place: $($_.Exception.Message)"
}

if (Test-Path -LiteralPath $retired) {
    try {
        Remove-Item -LiteralPath $retired -Recurse -Force -ErrorAction Stop
    } catch {
        Write-Host "  (the previous version is still in $retired and could not be removed)" -ForegroundColor Yellow
    }
}

$installedExe = Join-Path $target $ExeName

# The package arrives in a zip, so the script files carry the mark of the web.
# Clearing it means a curious user can read and run these later. Uninstall is
# invoked with -ExecutionPolicy Bypass regardless, so this is tidiness rather
# than a requirement.
try {
    Get-ChildItem -LiteralPath $target -Filter '*.ps1' -ErrorAction SilentlyContinue |
        Unblock-File -ErrorAction SilentlyContinue
} catch { }

# ----------------------------------------------------------------- shortcuts

$shell = New-Object -ComObject WScript.Shell

function New-Shortcut([string] $LinkPath) {
    $link = $shell.CreateShortcut($LinkPath)
    $link.TargetPath = $installedExe
    $link.WorkingDirectory = $target
    $link.IconLocation = "$installedExe,0"
    $link.Description = $AppName
    $link.Save()
    Write-Host "  shortcut   $LinkPath"
}

New-Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk")
if (-not $NoDesktop) {
    New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk")
}

# ---------------------------------------------------- so Settings > Apps sees it

$version = ''
try { $version = (Get-Item -LiteralPath $installedExe).VersionInfo.ProductVersion } catch { }
if (-not $version) {
    # The catch used to leave this empty, which reached two places: Settings >
    # Apps listed the app with a blank version, and the completion line below
    # read "Panopto Scheduler  is installed" with a gap in it. The stamp is
    # cosmetic, so the install stands - but it says what happened rather than
    # silently showing nothing.
    $version = 'unknown'
    Write-Host '  (the executable carries no readable version stamp - Settings > Apps will show "unknown")' -ForegroundColor Yellow
}
if ($version) { $version = $version.Split('+')[0] }

$sizeKb = 0
Get-ChildItem -LiteralPath $target -Recurse -File | ForEach-Object { $sizeKb += $_.Length }
$sizeKb = [int]($sizeKb / 1KB)

$shellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$uninstallCommand = '"{0}" -NoProfile -ExecutionPolicy Bypass -File "{1}"' -f `
    $shellExe, (Join-Path $target 'uninstall.ps1')

# HKCU, never HKLM. This is what keeps the whole install admin-free.
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PanoptoScheduler'
if (Test-Path $key) { Remove-Item $key -Recurse -Force }
New-Item -Path $key -Force | Out-Null
Set-ItemProperty -Path $key -Name 'DisplayName'     -Value $AppName
Set-ItemProperty -Path $key -Name 'DisplayVersion'  -Value $version
Set-ItemProperty -Path $key -Name 'Publisher'       -Value $Publisher
Set-ItemProperty -Path $key -Name 'InstallLocation' -Value $target
Set-ItemProperty -Path $key -Name 'DisplayIcon'     -Value "$installedExe,0"
Set-ItemProperty -Path $key -Name 'UninstallString' -Value $uninstallCommand
Set-ItemProperty -Path $key -Name 'InstallDate'     -Value (Get-Date -Format 'yyyyMMdd')
Set-ItemProperty -Path $key -Name 'EstimatedSize'   -Value $sizeKb -Type DWord
Set-ItemProperty -Path $key -Name 'NoModify'        -Value 1 -Type DWord
Set-ItemProperty -Path $key -Name 'NoRepair'        -Value 1 -Type DWord
Write-Host '  listed in  Settings > Apps > Installed apps'

# ---------------------------------------------------------------------- done

$shortcutWhere = 'Start Menu'
if (-not $NoDesktop) { $shortcutWhere = 'Start Menu and Desktop' }

Write-Host ''
Write-Host "  $AppName $version is installed." -ForegroundColor Green
Write-Host "  program    $installedExe"
Write-Host "  shortcuts  $shortcutWhere"
Write-Host ''
Write-Host '  Launch it from the Start Menu. The first time it asks you to sign in with'
Write-Host '  your own Panopto account; after that it opens straight to the schedule.'
Write-Host ''
Write-Host '  Your saved sign-in, booking templates and logs are kept in:'
Write-Host "    $(Join-Path $env:USERPROFILE '.panopto-scheduler')"
Write-Host '  They stay there if you uninstall, unless you ask for them to be removed.'
Write-Host ''
Write-Host '  The folder you unzipped can now be deleted.'
Write-Host ''
