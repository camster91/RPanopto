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

    # Windows PowerShell 5.1's Start-Process joins an ArgumentList with spaces
    # and quotes nothing, so a TEMP under a username with spaces would hand the
    # inner run a broken -File path: it would fail to start, and because every
    # removal in this script happens inside that inner run, an uninstall would
    # "succeed" at removing nothing. The file path is the only element that can
    # contain a space - the switches are fixed - so it is the only one quoted.
    $forward = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $temp + '"'), '-FromTemp')
    if ($Purge) { $forward += '-Purge' }
    if ($Force) { $forward += '-Force' }

    # -NoNewWindow runs the inner copy in THIS console rather than a window of
    # its own. Its own window closed the instant it exited, so a refusal -
    # "the app is still running", the elevation check - flashed past unread
    # and the uninstall looked like it had silently done nothing.
    #
    # Sharing the console is only half of it: launched from Settings > Apps,
    # this console is itself a window that closes when this process exits. So
    # on failure it waits for Enter before closing. Success does not wait - the
    # entry vanishing from Settings is the confirmation - and a run without an
    # interactive console (-NonInteractive, a redirected stdin) cannot be
    # prompted, so the prompt is attempted and its failure ignored.
    $inner = Start-Process -FilePath $powershell -ArgumentList $forward -NoNewWindow -Wait -PassThru
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    $code = $inner.ExitCode
    if ($code -ne 0) {
        try { [void](Read-Host '  Press Enter to close this window') } catch { }
    }
    exit $code
}

# ----------------------------------------------------- can we do this at all?

# A run elevated AS SOMEONE ELSE -- the IT account typed into a UAC prompt --
# would be looking at that account's profile, not the person's, so it would
# find nothing and appear to succeed at doing nothing.
#
# Being an administrator is not the problem by itself. Refusing on that alone
# (as this once did) meant an administrator account on a machine with UAC
# turned off could never uninstall: Settings > Apps runs this with the admin
# token and no -Force, so every attempt failed. The test is whether the
# account this runs as is the account signed in to the desktop, read from the
# owner of explorer.exe in this session -- Explorer is the desktop, so it runs
# as whoever is sitting at it. When that cannot be read (no Explorer, WMI
# unavailable, owners that disagree) the old rule stands and an elevated run
# is refused. install.ps1 makes the same check for the same reason.
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
        Fail "this is running from an elevated (administrator) prompt, and the account signed in to this desktop could not be determined, so it might be removing the program for the administrator rather than for you. Close it and run this from your own account, or pass -Force if you do mean $($identity.Name)."
    }
    if ($desktopSid -ne $identity.User.Value) {
        Fail "this is running as $($identity.Name), not as the account signed in to this desktop, so it would be removing the program for $($identity.Name) rather than for you. Close it and run this from your own account, or pass -Force if you do mean $($identity.Name)."
    }
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
    #
    # A target that cannot be read is left alone rather than deleted: an
    # unreadable shortcut cannot be shown to be this program's, and "maybe it
    # was ours" is not evidence enough to remove something from someone's
    # desktop. The operator can delete it by hand; the reverse mistake would
    # remove something that was never ours.
    $points = ''
    try { $points = $shell.CreateShortcut($path).TargetPath } catch { }
    if ($points -and $InstalledExe -and ($points -ieq $InstalledExe)) {
        Remove-Item -LiteralPath $path -Force
        Write-Host "  removed    $path"
    } elseif ($points) {
        Write-Host "  left alone $path (it points at $points, not at this program)" -ForegroundColor Yellow
    } else {
        Write-Host "  left alone $path (its target could not be read, so it cannot be shown to be this program's)" -ForegroundColor Yellow
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
#
# The comparison demands the separator, not just the prefix: a sibling such as
# ...\ProgramsTools or ...\Programs2 starts with "...\Programs" and would pass a
# bare prefix test, and so would the Programs folder itself - deleting which
# removes every program this account has installed, so equality is rejected
# too.
$underPrograms = $false
try {
    $full = [System.IO.Path]::GetFullPath($folder)
    $root = [System.IO.Path]::GetFullPath($Programs).TrimEnd('\')
    $underPrograms = ($full -ne $root) -and
        $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)
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
