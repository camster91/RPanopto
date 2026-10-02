# Allow-listing Panopto Scheduler

For whoever reviews this on behalf of IT or security. Everything below was read
out of the app's source rather than produced by running it, and the file
references are there so each claim can be checked rather than taken on faith.

The app is a scheduling front end for the Rotman AV team. It shows the room
recording schedule for `rotman.ca.panopto.com` as a calendar and lets the team
create, move, rename and bulk-edit recordings without going through Panopto's
web dashboard.

---

## Why it may be flagged

Four properties, each individually ordinary, that together describe the shape of
a commodity loader:

| Property | Why |
|---|---|
| **Signed, but by an in-house certificate** | The executable, `install.ps1` and `uninstall.ps1` are signed during packaging (`publish.ps1 -CertificateThumbprint`, which calls `Set-AuthenticodeSignature` with SHA-256 and an RFC 3161 timestamp from `timestamp.digicert.com`). The certificate is the team's own self-signed one, so the signature is real but chains to nothing a machine already trusts: Windows still shows an unknown-publisher warning until IT deploys the sidecar `.cer` — `PanoptoScheduler-CodeSigning.cer`, inside the zip and also attached beside it in every release — to Trusted Root and Trusted Publishers, which is what the `Code-signing-certificate-for-IT.txt` note next to it describes. |
| **Ships the .NET runtime alongside it** | 396 files, ~135 MB unpacked, ~59 MB zipped. The `.exe` itself is 150 KB — the bulk is the runtime and framework assemblies, not application code. |
| **`createdump.exe` in the folder** | A memory-dumping diagnostic that ships with the .NET runtime itself. It is not part of this app and is never invoked by it, but a file with that name and that capability is a reasonable thing for an engine to score. |
| **Opens a listening socket** | `127.0.0.1:51820`, during sign-in only — see below. |

If a behavioural engine scores it, the score is coming from that combination
rather than from anything the app does.

One deliberate choice worth knowing: the build is published as a **folder**, not
as a single-file self-extracting bundle — `PublishSingleFile` is off on purpose.
Single-file bundles are deleted on execution by the endpoint protection already
on these workstations, so the folder form is the one that survives.

A sixth property, only if the person chose to install rather than run it in
place: the zip carries an installer, which is `Install.cmd` plus two PowerShell
scripts. It is plain text, it does no self-extraction, and it writes under HKCU
only — but a script that creates shortcuts and writes a registry key is a shape
a behavioural engine may score, so it is listed here rather than left to be
discovered. **Installing is optional**; see [The installer](#the-installer).

---

## What it contacts, and nothing else

All traffic is outbound HTTPS on 443 to two hosts:

| Destination | Purpose |
|---|---|
| `rotman.ca.panopto.com` | Sign-in (OAuth 2.0 authorization code with PKCE), the public SOAP API for writes, and Panopto's own internal `Data.svc` for reads |
| `gist.githubusercontent.com` | The update check (`PanoptoScheduler.Core/Updates/VersionFeed.cs`): one GET of a public GitHub gist that holds the newest published version number, on every start. It reads one JSON string and sends nothing. |

There is no telemetry, no analytics, no crash reporting, and no third-party
service of any kind. The update check is the one request that is not the tenant,
and all it can ever learn or reveal is that some version of this app exists —
which is the entire disclosure of the public gist it reads. Every failure of
that request is silent by design: an operator about to book a recording is
never shown a dialog about an update.

Two things that look like other hosts in the source and are not: XML namespace
identifiers (`schemas.datacontract.org`, `tempuri.org`, `w3.org`) are strings
that identify a message format and are never fetched, and `*.example` hosts
appear only in test fixtures.

Opening a session in a browser hands a `https://rotman.ca.panopto.com/...` URL
to the default browser. The scheme is validated as `http` or `https` first —
that check exists because these URLs come out of Panopto's JSON and the shell
resolves *any* registered scheme, so a `file:` or custom URL arriving from the
tenant's data would otherwise be launched.

## The loopback listener

This is the part that reads as a backdoor in a behavioural report. It is the
standard desktop-app OAuth redirect, and it is narrow by construction
(`PanoptoScheduler.Core/Auth/LoopbackListener.cs`):

- Binds **`127.0.0.1`** — IPv4 loopback only, taken from `IPAddress.Loopback`.
  It is not reachable from the network, from another host, or from another
  machine on the LAN.
- Bound **only while a sign-in is in progress**, accepts **one** request, and
  closes as soon as the redirect arrives or the attempt is abandoned.
- Its whole job is to catch
  `http://localhost:51820/oauth/callback?code=...&state=...` after the user
  signs in at Panopto in their browser. The `state` value is checked against the
  one this attempt issued, and a mismatch is refused.

It is a raw socket listener rather than the usual `HttpListener` for a specific
reason: `HttpListener` requires a URL ACL registered with `netsh`, which needs
administrator rights. A team member on a standard account has no way to grant
that, so the app would fail to sign in for everyone who is not an admin.

**No port is open when the app is not signing in.**

## What it writes

Per-user only, under `%USERPROFILE%\.panopto-scheduler\`:

| File | Contents |
|---|---|
| `credentials.json` | Tenant, OAuth client id and secret, redirect URI, time zone |
| `tokens.dat` | The cached sign-in, encrypted with **Windows DPAPI** — readable only by that Windows account, on that machine |
| `templates.json` | Saved booking patterns. No credentials. |
| `logs\app-<date>.log` | Plain-text log of what the app did and any error |
| `logs\bulk-<date>.jsonl` | Audit trail of bulk changes: one JSON record per row of every bulk run, pruned on the same 30-day schedule as the log |

Nowhere else. There is no machine-wide state to review: nothing under
`Program Files`, nothing in the registry, and no entry added to startup.

## The installer

The zip carries `Install.cmd`, `install.ps1` and `uninstall.ps1` beside the
executable. **Installing is optional.** Running `PanoptoScheduler.App.exe`
straight from the unzipped folder is fully supported and is the path that writes
nothing to the registry and creates no shortcuts at all.

If the installer is run, it installs for the current user only and does exactly
this — the whole list, nothing implied:

| What | Where |
|---|---|
| The program files | `%LOCALAPPDATA%\Programs\Panopto Scheduler\` |
| One Start Menu shortcut | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Panopto Scheduler.lnk` |
| One Desktop shortcut | the folder Windows reports as the Desktop — `%USERPROFILE%\Desktop\Panopto Scheduler.lnk`, or under OneDrive where Desktop is redirected to it; skipped when run with `-NoDesktop` |
| **One registry key** | `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\PanoptoScheduler` |

The registry key is what lists the app in **Settings > Apps > Installed apps**.
It is under **HKCU, never HKLM**; it holds exactly ten values — six display
strings (`DisplayName`, `DisplayVersion`, `Publisher`, `InstallLocation`,
`DisplayIcon`, `UninstallString`), `InstallDate`, `EstimatedSize` in KB, and the
`NoModify` and `NoRepair` flags that hide Repair and Modify in Settings > Apps —
and the uninstaller deletes it. Nothing the installer does needs
administrator rights, and it refuses to run elevated as a different account
from the one signed in to the desktop — a per-user install run as, say, an IT
account typed into a UAC prompt would install into that account's profile,
not the user's, which is a silent and confusing outcome. (An administrator
installing for themselves, or a machine with UAC off, is allowed: that is
their own profile. If the signed-in account cannot be determined, any
elevated run is refused.)

There is deliberately **no packaged installer executable**. A self-extracting
`.exe` — Inno Setup, NSIS, any of them — is the exact shape the endpoint
protection on these workstations deletes on execution, and an `.msi` would
require administrator rights and a WiX toolchain. Three readable script files
are also the version of this that a reviewer can audit without a decompiler.

Uninstalling removes the two shortcuts, that one key, and the program folder. It
deliberately leaves `%USERPROFILE%\.panopto-scheduler\` in place — the saved
sign-in, the booking templates and the logs — unless it is run with `-Purge`,
because losing someone's templates as a side effect of removing a program would
be a nasty surprise.

## What it does not do

Each of these was checked for in the source and is absent:

- no elevation, UAC prompt, or `runas`
- no Windows service, driver, or scheduled task
- no registry writes **by the application**. The installer writes one HKCU key,
  and only if you choose to install — see [The installer](#the-installer).
- no self-modification and no self-applied update. The update check reads a
  version number and, if the feed names a newer one, shows a banner whose link
  opens the browser at the repository's Releases page — the download and the
  upgrade happen by hand, in the browser, or not at all.
- no downloaded code that is then executed
- no filesystem scanning, enumeration, or exfiltration

The app is run by double-clicking the `.exe` and stops when it is closed.

## The one thing to be deliberate about

**The distributed zip contains the shared OAuth client secret**, in
`defaults.json` beside the executable.

This is not an oversight and cannot be engineered away. Panopto does not support
OAuth public clients, so it rejects the authorization-code exchange from any
client that does not present a secret — and a secret shipped inside a desktop
application is readable by anyone holding the file. There is no build
configuration that changes this.

What the secret grants is the ability to **ask Panopto for a sign-in**. It grants
no access to any recording, folder, or session. Every user still authenticates as
themselves in Panopto's own browser page and is authorised as themselves, so the
app can never do anything a given user could not do in the dashboard.

The consequence is a distribution rule rather than a code change: **the zip is
team-internal and must not be posted publicly.** A copy on a share the team can
reach is fine; a public download link is not.

## Allow-listing

Allow-list by the executable's path, and **install first if you can**: it turns
the rule from "wherever this person happened to unzip a folder" into a fixed
path that is the same on every machine.

```
%LOCALAPPDATA%\Programs\Panopto Scheduler\PanoptoScheduler.App.exe
```

Running it in place remains supported, and then the path is wherever the zip was
unpacked:

```
<wherever it was unzipped>\PanoptoScheduler.App.exe
```

A path rule is the better fit here either way, because the folder is replaced
wholesale when a new version arrives, so a rule that pins the folder survives an
upgrade where a hash rule does not.

Allow-listing the installer as well is worth doing if people will use it:
`Install.cmd`, `install.ps1` and `uninstall.ps1`, in the same folder. Allow-listing
just the application leaves the portable path working and only the install step
blocked.

If a hash rule is required instead, hash the specific executable from the release
being deployed, and re-do it per version. The version is shown in the app's title
bar and recorded at the top of every log file, so which release is running is
always answerable.
