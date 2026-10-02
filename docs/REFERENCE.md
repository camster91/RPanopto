# Panopto Scheduler reference guide

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

Uninstalling keeps your saved sign-in, templates and logs. To remove those
too, run the installed uninstaller with `-Purge` from a Command Prompt (the
`-ExecutionPolicy Bypass` is needed for the same reason `Install.cmd` passes
it — a default policy will not run the script directly):

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File "%LOCALAPPDATA%\Programs\Panopto Scheduler\uninstall.ps1" -Purge
```

If you never installed it, run the same command against the `uninstall.ps1`
in the folder you unzipped.

> **Keep the zip to the team.** It carries the shared OAuth client that lets the
> app ask Panopto for a sign-in. It grants no access to any recording by itself
> — you still sign in as yourself — but it should not be posted anywhere public.

### If Windows or your antivirus warns about it

The app is signed, but with the team's own certificate rather than one a
certificate authority issued, so Windows does not know who vouches for it:
SmartScreen shows "Windows protected your PC" until IT deploys the
sidecar certificate (below). Choose **More info → Run anyway** — once per
machine, not once per launch. The signature still proves something: a build
whose files were tampered with after packaging fails the check, so
"unknown publisher" is the honest reading, not "unverified file".

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

If someone in IT needs to review it before allowing it, **[`AV-ALLOWLIST.md`](../AV-ALLOWLIST.md)**
is written for them: what the executable is, every host it contacts, why it
opens a local port while you sign in, exactly what the installer writes, and
what it writes to disk.

### Code signing

The executable, `install.ps1` and `uninstall.ps1` are signed during packaging —
there is no separate signing step to remember:

```powershell
.\src\publish.ps1 -CertificateThumbprint <certificate-thumbprint>
```

Everything about this lives inside `publish.ps1`, and each piece is there for
a reason that was learned rather than guessed:

- **`Set-AuthenticodeSignature`, not `signtool`.** signtool ships with the
  Windows SDK, not with Windows, and installing the SDK on the packaging
  machine needs an administrator. `Set-AuthenticodeSignature` writes the same
  Authenticode signature with the same hash and timestamp and ships with every
  PowerShell.
- **SHA-256, timestamped over RFC 3161.** Without the timestamp the signature
  dies with the certificate and every installed copy starts warning again. The
  timestamp server is reached over plain `http://` because PowerShell 7's
  signing client fails its timestamp step over https ("The parameter is
  incorrect") — and the timestamp token carries its own signature, so the
  transport does not have to prove anything.
- **Success is checked by reading the signature back**, not by `Status`: the
  packaging machine does not trust its own self-signed certificate, so every
  `Status` reading there is `NotTrusted` no matter how well the signing went.
  The checkable facts are that the signature names the certificate and carries
  a timestamp; trust is a property of the machines the zip reaches.
- **The certificate is the team's own self-signed one.** A signature from it
  proves integrity (tampered files fail) but not reputation — SmartScreen
  keeps warning until the certificate is deployed to the machines, which is
  what `PanoptoScheduler-CodeSigning.cer` and
  `Code-signing-certificate-for-IT.txt` in the zip (and attached beside it on
  every release) are for: the note tells IT
  to put the `.cer` into Trusted Root and Trusted Publishers, and the warnings
  stop.
- **If a CA-issued certificate is ever bought** (OV: cheaper, reputation builds
  over weeks; EV: immediate reputation, hardware token), it slots into the same
  `-CertificateThumbprint` flag and everything above keeps working. `Install.cmd`
  itself stays unsigned — batch files carry no signature — but SmartScreen does
  not prompt for a `.cmd` the way it does for an unsigned `.exe`.

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
| `logs\bulk-<date>.jsonl` | Audit trail of bulk changes: one JSON record per row of every bulk run. Kept for a month like the log. |

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
(`1.3.1+<commit>`), the tenant, and the time zone; after that it records what the
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

A signed build (`-CertificateThumbprint`) also carries
`PanoptoScheduler-CodeSigning.cer` and `Code-signing-certificate-for-IT.txt`,
copied from `src\dist\` on the packaging machine — they are the packager's to
export and are not in the repository. `publish.ps1` warns if either is missing,
and `release.ps1` refuses a zip without them.

They are plain script on purpose. A packaged installer `.exe` — Inno, NSIS,
anything self-extracting — is the exact shape the endpoint protection on these
workstations deletes on execution, and an `.msi` would need administrator rights.
Readable text is also the version of this that IT can audit.

**`src\dist\win-x64\` is not a package and must not be shipped from.** It is the
publish output of the last build on your machine; `publish.ps1` rebuilds it and
refuses to zip it if a `.pdb` has appeared. Always ship the zip.

### Cutting a release

1. Bump `<Version>` in `src\PanoptoScheduler.App\PanoptoScheduler.App.csproj`.
   That one string is the package name, the title bar and the log line; the two
   lines under it are not bumped with it. Use the three-part form (`1.4.0`):
   `release.ps1` refuses anything else, because a format change part-way
   through the app's life would show every copy of the old spelling an update
   it already has.
2. Build the signed package and let it finish without throwing:

   ```powershell
   .\src\publish.ps1 -CertificateThumbprint <certificate-thumbprint>
   ```

   It asserts the publish is self-contained and pdb-free before it zips, so a
   run that completes **is** the check that the result will start on a machine
   with no .NET runtime — and that the signature is in the zip, since signing
   happens inside the same run.
3. Ship it:

   ```powershell
   .\src\release.ps1 -Version <version>
   ```

   `release.ps1` creates the GitHub release on `camster91/panopto-recording-scheduler`
   (the tag, the zip, the code-signing `.cer` and the IT note) and then points
   the public version gist at the new number — that order is load-bearing: the
   banner every running copy shows links to a Releases page that already
   holds the download. Running copies notice on their next start, because the
   update check reads the gist on every launch.

   Before touching GitHub it checks that the checkout is clean and its commit
   is pushed to that repository — the tag is created at that commit, not at
   whatever the default branch points to — and it opens the zip to confirm the
   executable and both scripts are signed by the shipped `.cer`, and that the
   executable's version (and, when stamped, its commit) match.

   If a run half-finished, re-running it resumes. A draft or partly uploaded
   release (a dropped upload leaves one) is finished — missing files uploaded,
   then published — and the gist is bumped only once the release is published
   with its zip. If the release is live but the gist was not bumped, the error
   names the one `gh gist edit` command that finishes the job by hand.

**The release page is the download.** The banner in the app opens the
repository's Releases page in the browser, and the browser's own GitHub
sign-in decides who may download: the repository is private, so the zip is
reachable by the team and nobody else. That is the design, and it is why the
zip can ship there at all — `defaults.json` carries the shared OAuth client
secret, which is exactly what keeps the repository (and the fork that would
come with making it public) permanently off the table. A team member who gets
the banner but a 404 from the page is not a collaborator yet:

```powershell
gh api -X PUT repos/camster91/panopto-recording-scheduler/collaborators/<their-github-name> -F permission=pull
```

Release notes are where a version's changes go, not this file.

### Known issues

**The paging phantom.** Twice in September 2026 (the 18th and the 21st) a
calendar load warned that a listing had stopped at the page ceiling holding
about **15,000 items** — from a tenant with **nineteen recorders**, which
cannot be that large. Something server-side was serving fresh-looking pages
past the real end of the set. It was never root-caused, and it has not
recurred since the walk learned to stop on its own terms: deduplicate items by
identity, treat a page that adds nothing (or re-serves the first page) as the
end, and refuse to report "complete" while the server still owes items
(`src\PanoptoScheduler.Core\Clients\SoapPaging.cs`).

If the warning comes back, read it as a serve-side anomaly first — the tenant
is not this large. **Raising `SoapPaging.MaxPages` is the one response already
tried** (20 → 60), and it tripled the phantom count instead of fixing it; the
in-warning advice to "raise SoapPaging.MaxPages" is stale for exactly that
reason. The honest signal to watch is whether the rooms or folders the tenant
actually has all show up, not the item count on the warning line.

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
