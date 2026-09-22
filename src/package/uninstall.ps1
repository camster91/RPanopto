# Removes Panopto Scheduler for the current user.
#
# Reached one of two ways:
#   * Settings > Apps > Installed apps > Panopto Scheduler > Uninstall, which
#     runs the copy that the installer placed in the install folder.
#   * By hand, from the unzipped folder, if you never installed it.
#
# It removes the two shortcuts, the one HKCU registry key, and the program
# folder. It deliberately KEEPS your saved sign-in, booking templates and logs
# in %USERPROFILE%\.panopto-scheduler\ - losing your templates as a side effect
# of removing a program would be a nasty surprise. Pass -Purge for a clean sweep.
#
# Usage:
#   .\uninstall.ps1          remove the program, keep your data
#   .\uninstall.ps1 -Purge   remove your saved sign-in, templates and logs too
#   .\uninstall.ps1 -Force   proceed even from an elevated prompt

[CmdletBinding()]
param(
    [switch] $Purge,
    [switch] $Force,

    # Set by this script when it re-executes itself from %TEMP%. Removing the
    # program folder means removing the folder this file is running from, and
    # Windows will not let a running script delete its own directory. So the
    # first thing it does is copy itself somewhere else and run that copy.
    [switch] $FromTemp
)

$ErrorActionPreference = 'Stop'

$AppName = 'Panopto Scheduler'
$ExeName = 'PanoptoScheduler.App.exe'
$DataFolder = Join-Path $env:USERPROFILE '.panopto-scheduler'
$RegistryKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PanoptoScheduler'

$Programs = Join-Path $env:LOCALAPPDATA 'Programs'
$DefaultFolder = Join-Path $Programs $AppName

function Fail([string] $Message) {
    Write-Host ''
    Write-Host "  Cannot uninstall: $Message" -ForegroundColor Red
    Write-Host ''
    exit 1
}

$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

# ------------------------------------------------- re-execute out of the way

if (-not $FromTemp) {
    $scriptPath = $PSCommandPath
    if ([string]::IsNullOrEmpty($scriptPath)) { $scriptPath = $MyInvocation.MyCommand.Path }

    $temp = Join-Path $env:TEMP ('panopto-scheduler-uninstall-' + [Guid]::NewGuid().ToString('N') + '.ps1')
    Copy-Item -LiteralPath $scriptPath -Destination $temp -Force

    $forward = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $temp, '-FromTemp')
    if ($Purge) { $forward += '-Purge' }
    if ($Force) { $forward += '-Force' }

    $inner = Start-Process -FilePath $powershell -ArgumentList $forward -Wait -PassThru
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    exit $inner.ExitCode
}

# ----------------------------------------------------- can we do this at all?

# An elevated prompt would be looking at the administrator's profile, not the
# person's, so it would find nothing and appear to succeed at doing nothing.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -and -not $Force) {
    Fail "this is running from an elevated (administrator) prompt, so it would be removing the program for the administrator rather than for you. Close it and run this from your own account."
}

$running = @(Get-Process -Name 'PanoptoScheduler.App' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Fail "$AppName is still running. Close it and run this again, so that nothing of yours is lost mid-edit."
}

Write-Host ''
Write-Host "  Removing $AppName ..."

# --------------------------------------------------------------- shortcuts

$shell = New-Object -ComObject WScript.Shell

function Remove-OurShortcut([string] $Folder, [string] $InstalledExe) {
    if ([string]::IsNullOrEmpty($Folder)) { return }
    $path = Join-Path $Folder "$AppName.lnk"
    if (-not (Test-Path -LiteralPath $path)) { return }

    # Read before deleting. A shortcut is nothing but a file with a name, and
    # someone else's shortcut could plausibly be called the same thing; only
    # remove this one if it actually points at the program we are removing.
    $points = ''
    try { $points = $shell.CreateShortcut($path).TargetPath } catch { }
    if ($points -and $InstalledExe -and ($points -ieq $InstalledExe)) {
        Remove-Item -LiteralPath $path -Force
        Write-Host "  removed    $path"
    } elseif ($points) {
        Write-Host "  left alone $path (it points at $points, not at this program)" -ForegroundColor Yellow
    } else {
        Remove-Item -LiteralPath $path -Force
        Write-Host "  removed    $path"
    }
}

# The registry records where it was actually installed, which is the only
# reliable answer when someone has moved it since.
$installedExe = Join-Path $DefaultFolder $ExeName
$folder = $DefaultFolder
if (Test-Path $RegistryKey) {
    $recorded = (Get-ItemProperty -Path $RegistryKey -Name 'InstallLocation' -ErrorAction SilentlyContinue).InstallLocation
    if ($recorded) {
        $folder = $recorded
        $installedExe = Join-Path $folder $ExeName
    }
}

Remove-OurShortcut ([Environment]::GetFolderPath('Programs')) $installedExe
Remove-OurShortcut ([Environment]::GetFolderPath('Desktop')) $installedExe

# --------------------------------------------------------------- registry

if (Test-Path $RegistryKey) {
    Remove-Item -Path $RegistryKey -Recurse -Force
    Write-Host '  removed    the entry in Settings > Apps'
}

# ----------------------------------------------------------- program folder

# Only ever delete something that lives under %LOCALAPPDATA%\Programs. If this
# script is being run by hand out of the folder you unzipped, that folder is
# somewhere else entirely - and deleting the folder a person is standing in
# because they asked to uninstall a program would be indefensible.
$underPrograms = $false
try {
    $full = [System.IO.Path]::GetFullPath($folder)
    $root = [System.IO.Path]::GetFullPath($Programs)
    $underPrograms = $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
} catch { $underPrograms = $false }

if (-not $underPrograms) {
    Write-Host ''
    Write-Host '  The program is not installed for this account, so there is no folder to'
    Write-Host '  remove. Nothing was deleted from wherever you ran this from.'
} elseif (-not (Test-Path -LiteralPath $folder)) {
    Write-Host '  (the program folder was already gone)'
} else {
    # A file can stay locked for a moment after the program holding it exits.
    $removed = $false
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction Stop
            $removed = $true
            break
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }

    if ($removed) {
        Write-Host "  removed    $folder"
    } else {
        Write-Host "  could not remove $folder - something still has a file open there." -ForegroundColor Yellow
        Write-Host '  Sign out and back in, then delete that folder by hand.' -ForegroundColor Yellow
    }
}

# ------------------------------------------------------------ your own data

Write-Host ''
if ($Purge) {
    if (Test-Path -LiteralPath $DataFolder) {
        Remove-Item -LiteralPath $DataFolder -Recurse -Force
        Write-Host "  removed    $DataFolder (your saved sign-in, templates and logs)"
    } else {
        Write-Host '  (there was no saved data to remove)'
    }
    Write-Host ''
    Write-Host "  $AppName has been removed." -ForegroundColor Green
} else {
    Write-Host "  $AppName has been removed." -ForegroundColor Green
    Write-Host ''
    Write-Host '  Your saved sign-in, booking templates and logs are still here:'
    Write-Host "    $DataFolder"
    Write-Host '  Delete that folder yourself if you want them gone as well, or run this'
    Write-Host '  again with -Purge. It holds no password: the saved sign-in is a token'
    Write-Host '  issued by Panopto, not your account password.'
}

Write-Host ''
