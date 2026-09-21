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
| **Unsigned** | No code-signing certificate. Nothing is wrong with the file; there is simply no signature to check. |
| **Ships the .NET runtime alongside it** | 396 files, ~135 MB unpacked, ~59 MB zipped. The `.exe` itself is 150 KB — the bulk is the runtime and framework assemblies, not application code. |
| **`createdump.exe` in the folder** | A memory-dumping diagnostic that ships with the .NET runtime itself. It is not part of this app and is never invoked by it, but a file with that name and that capability is a reasonable thing for an engine to score. |
| **Opens a listening socket** | `127.0.0.1:51820`, during sign-in only — see below. |

If a behavioural engine scores it, the score is coming from that combination
rather than from anything the app does.

One deliberate choice worth knowing: the build is published as a **folder**, not
as a single-file self-extracting bundle — `PublishSingleFile` is off on purpose.
Single-file bundles are deleted on execution by the endpoint protection already
on these workstations, so the folder form is the one that survives.

---

## What it contacts, and nothing else

All traffic is outbound HTTPS on 443 to a single host:

| Destination | Purpose |
|---|---|
| `rotman.ca.panopto.com` | Sign-in (OAuth 2.0 authorization code with PKCE), the public SOAP API for writes, and Panopto's own internal `Data.svc` for reads |

There is no telemetry, no analytics, no crash reporting, no update check, and no
third-party service of any kind. The app never contacts a host that is not the
configured tenant.

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

Nowhere else. There is no machine-wide state to review: nothing under
`Program Files`, nothing in the registry, and no entry added to startup.

## What it does not do

Each of these was checked for in the source and is absent:

- no elevation, UAC prompt, or `runas`
- no Windows service, driver, or scheduled task
- no registry writes
- no auto-update or self-modification
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

Allow-list by the executable's path in the unzipped folder:

```
<wherever it was unzipped>\PanoptoScheduler.App.exe
```

A path rule is the better fit here, because the app is unpacked wherever the
person likes — Desktop, Documents, a USB stick — and the folder is replaced
wholesale when a new version arrives.

If a hash rule is required instead, hash the specific executable from the release
being deployed, and re-do it per version. The version is shown in the app's title
bar and recorded at the top of every log file, so which release is running is
always answerable.
