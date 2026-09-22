@echo off
setlocal
title Panopto Scheduler - install

rem Double-click this to install Panopto Scheduler for the current user.
rem
rem It is a wrapper so that the window stays open afterwards and you can read
rem what happened. Everything it does is in install.ps1 beside it, which is
rem plain text you can read first.
rem
rem -ExecutionPolicy Bypass is here because a file that arrived inside a zip
rem carries the Mark of the Web, and a default execution policy refuses to run
rem it. There is nothing hidden behind the flag: the script beside this one is
rem the whole install.

"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
set "code=%ERRORLEVEL%"

if not "%code%"=="0" echo Install did not complete. The reason is printed above.
echo.
pause
exit /b %code%
