# Runs each WPF self-test window and reads back what it said.
#
# Written as a file rather than a one-liner for two measured reasons:
#
#   1. `& $exe` does NOT wait for a WinExe. The shell hands the process over and
#      moves on, so an exit code read straight after is the launcher's, and every
#      self-test reads green whether or not it ran.
#   2. `Start-Process -Wait` hangs on the fixtures meant to be looked at by eye:
#      --self-test-week, -panel and -rooms show a window and stay up. So each is
#      given a timed window and then killed, rather than waited on.
#
# The verdict for the interactive ones is not this script's stdout. It is the app
# log, which is what the last block reads: a DEBUG binding listener turns every
# unresolved binding into a WARN there, and that is the only thing that catches a
# mis-scoped DataTemplate binding -- a page that renders with an empty cell looks
# exactly like a page with nothing to put in it.
#
# Pure ASCII, deliberately. This shell is Windows PowerShell 5.1, which reads a
# BOM-less .ps1 as ANSI, so a non-ASCII character in this file arrives mangled.

$ErrorActionPreference = 'Continue'

$exe = Join-Path $PSScriptRoot 'PanoptoScheduler.App\bin\Debug\net9.0-windows\PanoptoScheduler.App.exe'
$out = Join-Path $env:TEMP 'selftest-out'
$logs = Join-Path $env:USERPROFILE '.panopto-scheduler\logs'

New-Item -ItemType Directory -Force $out | Out-Null

# How long each is left up. The self-closing ones shut down long before this; the
# interactive ones need enough to lay out and realise their templates, which is
# the moment a binding either resolves or warns.
$flags = @{
    '--self-test-bulk'    = 30
    '--self-test-dialogs' = 30
    '--self-test-panel'   = 20
    '--self-test-rooms'   = 20
    '--self-test-week'    = 20
}

$started = Get-Date

foreach ($flag in $flags.Keys | Sort-Object)
{
    $name = $flag.TrimStart('-')
    $stdout = Join-Path $out "$name.out.txt"
    $stderr = Join-Path $out "$name.err.txt"

    $p = Start-Process -FilePath $exe -ArgumentList $flag -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr

    $closed = $p.WaitForExit($flags[$flag] * 1000)

    if (-not $closed) { $p.Kill(); $p.WaitForExit(5000) | Out-Null }

    # Assigned plainly, and only when the file is there. Get-Content -Raw on a
    # 0-byte file gives $null in this shell, and .Trim() on it throws where the
    # empty output was the whole answer.
    $text = ''
    $errs = ''

    if (Test-Path $stdout) { $text = [string](Get-Content $stdout -Raw) }
    if (Test-Path $stderr) { $errs = [string](Get-Content $stderr -Raw) }

    $how = if ($closed) { "closed itself, exit $($p.ExitCode)" } else { 'stayed open (killed)' }

    Write-Output "===== $flag  $how ====="
    if ($text.Trim().Length -gt 0) { Write-Output $text.Trim() }
    if ($errs.Trim().Length -gt 0) { Write-Output '--- stderr ---'; Write-Output $errs.Trim() }
}

$logFile = Get-ChildItem $logs -Filter 'app-*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 1

Write-Output ''
Write-Output '===== app log, everything from this run ====='

if ($null -eq $logFile)
{
    Write-Output "(no app log found under $logs)"
    Write-Output '=== ALL DONE ==='
    return
}

# The log's own shape is "2026-09-21 11:50:37 [INFO ] message". Two things about
# that tripped earlier versions of this script and are worth stating: the stamp is
# not bracketed, and the level is padded, so a [WARN] with no space in the pattern
# matches nothing at all.
$runLines = Get-Content $logFile.FullName | Where-Object {
    $line = $_

    if ($line -notmatch '^(?<t>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})') { return $false }

    $stamp = $matches['t']
    $when = [datetime]::ParseExact($stamp, 'yyyy-MM-dd HH:mm:ss', $null)

    return $when -ge $started
}

$runLines | ForEach-Object { Write-Output $_ }

Write-Output ''
Write-Output '===== every WARN or ERROR in this run, which is the actual verdict ====='

$warnings = @($runLines | Where-Object { $_ -match '\[(WARN|ERROR)\s*\]' })

$warnings | ForEach-Object { Write-Output $_ }

Write-Output "(that is $($warnings.Count) line(s) in total)"
Write-Output '=== ALL DONE ==='
