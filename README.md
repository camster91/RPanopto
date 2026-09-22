# Panopto Scheduler

A Windows app for the Rotman AV team: sees the room recording schedule as a
calendar, and schedules, moves, renames and bulk-deletes recordings in
`rotman.ca.panopto.com` without going through the Panopto web dashboard.

Installing takes one double-click, needs no administrator, and is optional.
You sign in as yourself, and the app remembers you.

---

## Install

1. Unzip `PanoptoScheduler-<version>-win-x64.zip` anywhere you like — your
   Desktop, your Documents, a USB stick.
2. Double-click **`Install.cmd`**.
3. Launch it from the **Start Menu**. The first time, a **Sign in** prompt
   appears. Click it, sign in with your own Panopto account in the browser that
   opens, and you are done.

After that, launching the app goes straight to the schedule. You sign in again
only if the session expires.

`Install.cmd` installs for **you only** — nothing machine-wide, no administrator,
no UAC prompt. It puts the program in
`%LOCALAPPDATA%\Programs\Panopto Scheduler\`, adds a Start Menu shortcut and a
Desktop shortcut, and lists it in **Settings → Apps** so you can uninstall it
the normal way. It writes exactly one registry key, under `HKCU`, and the
uninstaller removes it.

**Or don't install it.** Running **`PanoptoScheduler.App.exe`** straight from the
unzipped folder is fully supported — that is the path that touches the registry
not at all and leaves no shortcuts behind. It is the right choice for a USB
stick, a loaner laptop, or a machine you do not want to change. The only thing
you give up is the Start Menu entry.

Uninstalling keeps your saved sign-in, templates and logs. Run
`uninstall.ps1 -Purge` if you want those gone too.

> **Keep the zip to the team.** It carries the shared OAuth client that lets the
> app ask Panopto for a sign-in. It grants no access to any recording by itself
> — you still sign in as yourself — but it should not be posted anywhere public.

### If Windows or your antivirus warns about it

The app is not code-signed. If SmartScreen shows "Windows protected your PC",
choose **More info → Run anyway** — once per machine, not once per launch.

If `Install.cmd` refuses to run at all, that is the script policy rather than the
antivirus: a `.ps1` that arrived inside a zip carries the Mark of the Web, and a
default execution policy will not run it. `Install.cmd` already passes
`-ExecutionPolicy Bypass` for exactly this reason. If your policy blocks that
flag too, clear the mark on the two scripts by hand and run it again:

```powershell
Unblock-File .\install.ps1, .\uninstall.ps1
```

If your antivirus quarantines anything, send the log (below) to whoever
maintains this and it can be allowlisted.

If someone in IT needs to review it before allowing it, **[`AV-ALLOWLIST.md`](AV-ALLOWLIST.md)**
is written for them: what the executable is, every host it contacts, why it
opens a local port while you sign in, exactly what the installer writes, and
what it writes to disk.

### When you get a code-signing certificate

That is the only thing that removes the SmartScreen warning, and it is a
purchase rather than a code change:

1. Buy either an **OV** certificate (cheaper; the warning persists until the
   download builds reputation, which takes weeks) or an **EV** one (immediate
   reputation, higher cost, and issued on a hardware token).
2. Sign the executable and the two scripts after publishing and before zipping.
   With a certificate in the Windows certificate store, `signtool` from the
   Windows SDK does it:

   ```powershell
   signtool sign /sha1 <certificate-thumbprint> /fd SHA256 `
       /tr https://timestamp.digicert.com /td SHA256 `
       src\dist\win-x64\PanoptoScheduler.App.exe
   ```

   The `/tr` timestamp matters: without it the signature stops being valid the
   day the certificate expires, and every installed copy starts warning again.
3. `install.ps1` and `uninstall.ps1` can be signed the same way. `Install.cmd`
   cannot — batch files carry no signature — but SmartScreen does not prompt for
   a `.cmd` the way it does for an unsigned `.exe`, so the executable is the file
   that has to be signed.

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

The program, if you installed it, is in `%LOCALAPPDATA%\Programs\Panopto Scheduler\`.
Nothing you create or change lives there — replacing or removing that folder
never costs you a booking template or your saved sign-in.

Everything of yours is under `%USERPROFILE%\.panopto-scheduler\`:

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

### Where the log is

```
%USERPROFILE%\.panopto-scheduler\logs\app-<today's date>.log
```

One file per day, kept for a month. It opens with the exact build
(`1.3.0+<commit>`), the tenant, and the time zone; after that it records what the
app did and any error in full.

Paste that path into the address bar of any Explorer window — or into the Run
box — and it opens the folder. You can also reach it without touching the
filesystem: the app prints it when something goes wrong.

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

The zip holds the published app plus three files copied out of `src\package\`:

| File | What it is |
|---|---|
| `Install.cmd` | Double-clickable. Wraps `install.ps1` and holds the window open so the outcome can be read. |
| `install.ps1` | The per-user install. |
| `uninstall.ps1` | Placed in the install folder, so Settings → Apps can find it. |

They are plain script on purpose. A packaged installer `.exe` — Inno, NSIS,
anything self-extracting — is the exact shape the endpoint protection on these
workstations deletes on execution, and an `.msi` would need administrator rights.
Readable text is also the version of this that IT can audit.

**`src\dist\win-x64\` is not a package and must not be shipped from.** It is the
publish output of the last build on your machine; `publish.ps1` rebuilds it and
refuses to zip it if a `.pdb` has appeared. Always ship the zip.

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
