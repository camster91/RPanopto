# Panopto Scheduler

A Windows app for the Rotman AV team: sees the room recording schedule as a
calendar, and schedules, moves, renames and bulk-deletes recordings in
`rotman.ca.panopto.com` without going through the Panopto web dashboard.

Nothing needs installing. Nothing needs an administrator. You sign in as
yourself, and the app remembers you.

---

## Install

1. Unzip `PanoptoScheduler-<version>-win-x64.zip` anywhere you like —
   your Desktop, your Documents, a USB stick. There is no installer.
2. Double-click **`PanoptoScheduler.App.exe`**.
3. The first time, a **Sign in** prompt appears. Click it, sign in with your own
   Panopto account in the browser that opens, and you are done.

After that, launching the app goes straight to the schedule. You sign in again
only if the session expires.

> **Keep the zip to the team.** It carries the shared OAuth client that lets the
> app ask Panopto for a sign-in. It grants no access to any recording by itself
> — you still sign in as yourself — but it should not be posted anywhere public.

### If Windows warns about the app

The app is not code-signed. If SmartScreen shows "Windows protected your PC",
choose **More info → Run anyway**. If your antivirus quarantines it, send the
log (below) to whoever maintains this and it can be allowlisted.

If someone in IT needs to review it before allowing it, **[`AV-ALLOWLIST.md`](AV-ALLOWLIST.md)**
is written for them: what the executable is, every host it contacts, why it
opens a local port while you sign in, and what it writes to disk.

---

## What it does

| | |
|---|---|
| **Calendar** | A week of recordings by room, from the same source the Panopto dashboard uses. |
| **Schedule** | Book a recording on a room's recorder, with clash checking before anything is written. |
| **Move** | Drag a session to a new time, or retype it. |
| **Import** | Load an XML or CSV schedule and book many rows at once — **dry run first**, always. |
| **Bulk edit** | Rename, move, delete or change webcasting across many sessions at once. |

Bulk operations show a preview of exactly what they will do and write nothing
until you confirm.

---

## Where things live

Everything is under `%USERPROFILE%\.panopto-scheduler\`:

| File | What it is |
|---|---|
| `credentials.json` | Tenant, client id and secret for this machine. Optional — see below. |
| `tokens.dat` | Your saved sign-in, encrypted with Windows DPAPI. Only your Windows account can read it. |
| `templates.json` | Booking patterns you saved on the *Book recordings* tab. Yours alone; delete a file to start over. |
| `logs\app-<date>.log` | What the app did, including any error. |

A saved template keeps the pattern — rooms, weekdays, times, title format,
presenter, folder — and **not the dates**. So it is the same next term as it was
this term: loading one leaves the dates already in the form alone, and the
length of the range is what comes back. That is deliberate. A template carrying
last term's dates would generate a term of bookings in the past.

### If something goes wrong

Send the support contact the newest file in `logs\`. It records the version, the
tenant, and the full error — usually that is enough to see the cause without
reproducing it. If the app closes by itself, it writes the log and shows you
where it is.

You can check that logging works on a machine before anything actually breaks:

```powershell
.\PanoptoScheduler.App.exe --self-test-crash
```

It raises a deliberate fault, shows the error and where the log went, and closes.
Nothing is wrong with the app when you do this — and if no log appears, that is
the thing worth reporting.

---

## For whoever maintains this

### Building the package

```powershell
.\src\publish.ps1
```

Produces `src\dist\PanoptoScheduler-<version>-win-x64.zip`, self-contained for
64-bit Windows. It reads the tenant and client from your own
`%USERPROFILE%\.panopto-scheduler\credentials.json` and writes them beside the
executable as `defaults.json`, so users are asked for nothing on first run.

Bump `<Version>` in `src\PanoptoScheduler.App\PanoptoScheduler.App.csproj`
before a release; that string is what the title bar and the log both show.

### Changing the tenant, or the client secret

`defaults.json` sits next to the executable. Edit it and repackage, or — to
change one machine without repackaging — give that machine its own
`~\.panopto-scheduler\credentials.json`. A person's own file always wins over
the shipped one.

**Rotating the client secret:** generate a new one in Panopto, put it in your
own `credentials.json`, and run `publish.ps1` again. Machines on the old package
keep working until the old secret is revoked, then need the new zip.

```json
{
  "tenantUrl":    "https://rotman.ca.panopto.com",
  "clientId":     "...",
  "clientSecret": "...",
  "timeZone":     "America/Toronto"
}
```

`timeZone` is the zone the **rooms** keep time in, and it is load-bearing rather
than cosmetic: a booking is converted through it before it is sent, so a wrong
value books the wrong hour. It defaults to `America/Toronto`.

### Building from source

Requires the .NET 9 SDK.

```powershell
dotnet build src\PanoptoScheduler.sln
dotnet test  src\PanoptoScheduler.sln
```

`src\PanoptoScheduler.Probe` talks to the live tenant without the UI and is how
the API behaviour above was established. All of it is read-only except
`--verify-write`, which books one session and deletes it.

---

## Legacy code

The `PanoptoScheduleUploader.*` projects and the solution at the repository root
are the original .NET Framework tool this replaces. They are kept for reference
and are not built or shipped. The current app is everything under `src\`.
