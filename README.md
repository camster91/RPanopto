# Panopto Scheduler

Panopto Scheduler is a Windows desktop app for Rotman AV staff to manage room recordings on `rotman.ca.panopto.com`. It shows scheduled recordings by room and provides forms for booking, moving, importing, and bulk editing sessions. Staff sign in with their own Panopto account.

## Install

1. Download the latest `PanoptoScheduler-<version>-win-x64.zip` from this repository's **Releases** page.
2. Unzip it and double-click `Install.cmd`.
3. Open **Panopto Scheduler** from the Start menu and follow the browser sign-in prompt.

Installation is per user and needs no administrator access. You can also run `PanoptoScheduler.App.exe` from the unzipped folder without installing it. The package is for the Rotman team: it includes a shared OAuth client, although access to recordings still requires each person's Panopto sign-in.

Windows may warn about the team's self-signed code-signing certificate. See [the IT review guide](AV-ALLOWLIST.md) for the app's network and installation details.

## What it does

- View a week of scheduled recordings by room.
- Book sessions with a conflict check, and move or rename existing sessions.
- Import XML or CSV schedules with a dry run before booking.
- Preview and confirm bulk changes, including deletion and webcast settings.

Bulk changes are written only after confirmation. The app keeps your sign-in, templates, and logs under `%USERPROFILE%\.panopto-scheduler`; uninstalling the program leaves those files in place.

## Developers and maintainers

The current .NET 9 app, tests, package scripts, and solution are under `src/`. The `PanoptoScheduleUploader.*` projects and the root solution are legacy reference code. GitHub Actions builds and tests the current solution on each push.

```powershell
dotnet build src\PanoptoScheduler.sln
dotnet test src\PanoptoScheduler.sln
```

`src/publish.ps1` builds the Windows package. `src/release.ps1` publishes a signed release and updates the version feed. Packaging requires the team's local signing certificate and Panopto client configuration; the CI build does not package or sign releases.

For installation troubleshooting, data files, signing, release steps, and known issues, see [the reference guide](docs/REFERENCE.md).
