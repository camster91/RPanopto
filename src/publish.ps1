<#
.SYNOPSIS
    Builds the zip the team installs from.

.DESCRIPTION
    Publishes the app self-contained for 64-bit Windows, puts the tenant and
    OAuth client beside the executable as defaults.json so the first run asks
    the user for nothing, and zips the result.

    The client secret comes from the packager's own credentials file and is
    never printed. It has to travel inside the package: Panopto has no
    public-client support, so a desktop app cannot hold a secret secretly no
    matter how it is built. What the package carries is the ability to ask for
    a sign-in, not access to anything -- every user still authenticates as
    themselves.

    Keep the produced zip off anything shared publicly. It is meant for the
    team, not for a download page.

.EXAMPLE
    .\publish.ps1
    Builds dist\PanoptoScheduler-<version>-win-x64.zip
#>
[CmdletBinding()]
param(
    # Where the zip lands. Defaults to src\dist.
    [string] $OutputDirectory
)

$ErrorActionPreference = "Stop"

# The SDK is user-local on this machine; a machine-wide install needs nothing.
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
if (Test-Path $env:DOTNET_ROOT) { $env:PATH = "$env:DOTNET_ROOT;$env:PATH" }

$src       = $MyInvocation.MyCommand.Path | Split-Path -Parent
$appProj   = Join-Path $src "PanoptoScheduler.App\PanoptoScheduler.App.csproj"
$publish   = Join-Path $src "dist\win-x64"
$defaults  = Join-Path $publish "defaults.json"
$exeName   = "PanoptoScheduler.App.exe"

$mine      = Join-Path $env:USERPROFILE ".panopto-scheduler\credentials.json"

if (-not (Test-Path $appProj)) { throw "Cannot find the app project at $appProj" }

[xml] $project = Get-Content -Raw $appProj
$version = @($project.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -Last 1
if (-not $version) { throw "No <Version> in $appProj -- the package would be unidentifiable." }

if (-not (Test-Path $mine)) {
    throw @"
No credentials at:
  $mine

The package has to carry a tenant and client, or every user is asked for three
things they cannot know. Create that file first -- run the app once and fill in
the dialog, or write it by hand:

  {
    "tenantUrl":    "https://rotman.ca.panopto.com",
    "clientId":     "...",
    "clientSecret": "...",
    "timeZone":     "America/Toronto"
  }
"@
}

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $src "dist" }
$zip = Join-Path $OutputDirectory "PanoptoScheduler-$version-win-x64.zip"

Write-Host "Publishing Panopto Scheduler $version for win-x64..."

# A running copy holds the assemblies open and publish fails halfway through.
Get-Process "PanoptoScheduler.App" -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

# PublishDir is passed explicitly so this script, not the profile, decides
# where the package lands. The profile's own value is relative to the project
# and the two drifted apart once already, which produced a "publish succeeded
# but there is no exe" that took a run to notice.
#
# DebugType is passed for the same reason the profile sets it: it applies to the
# app project alone, so the referenced Core project kept emitting a pdb and
# shipped it -- a package contradicting the comment in the profile that says no
# pdb travels. A global property reaches every project in the graph.
& dotnet publish $appProj -c Release -p:PublishProfile=win-x64 -p:PublishDir="$publish\" -p:DebugType=none -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$exe = Join-Path $publish $exeName
if (-not (Test-Path $exe)) { throw "Publish reported success but produced no $exeName." }

# Checked rather than trusted, because this is how the pdb shipped in the first
# place: a symbol file triples the download and carries the build machine's
# source paths to every copy of the package.
$strays = @(Get-ChildItem $publish -Filter *.pdb -ErrorAction SilentlyContinue)
if ($strays.Count -gt 0) {
    throw "The package contains $($strays.Count) .pdb file(s): $($strays.Name -join ', ')"
}

# Copied, never read aloud. Copy-Item moves bytes; nothing here logs contents.
Copy-Item $mine $defaults -Force
Write-Host "  Added defaults.json (tenant + client, from your own credentials file)."

# Fail loudly rather than shipping a zip that prompts on first run.
$shipped = Get-Content -Raw $defaults | ConvertFrom-Json
foreach ($field in @("tenantUrl", "clientId", "clientSecret")) {
    if (-not $shipped.$field) { throw "defaults.json is missing $field -- the package would prompt on first run." }
}

# The zone is load-bearing for writes, not a display preference: Panopto stores
# the instant it is sent, so this is what decides which hour a booking lands on
# the room's clock. Absent it would fall back to the same default anyway, but a
# packaged file that does not say is one an admin cannot check.
if (-not $shipped.timeZone) {
    $shipped | Add-Member -NotePropertyName timeZone -NotePropertyValue "America/Toronto" -Force
}

# Checked with pwsh, not in this shell. Windows PowerShell 5.1 runs on .NET
# Framework, which does not know IANA zone ids at all -- it rejects
# "America/Toronto" as unknown. The app runs on .NET 9, which resolves them.
# Validating in the wrong runtime would fail a perfectly good package, which is
# the sort of check that gets deleted instead of fixed.
$zone = [string] $shipped.timeZone
if ($zone -notmatch '^[A-Za-z0-9_/+\-]+$') {
    throw "timeZone '$zone' has characters no zone id uses. Use an IANA or Windows zone id, or 'America/Toronto'."
}

$pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
if ($pwsh) {
    $verdict = & $pwsh.Source -NoProfile -Command `
        "try { [void][System.TimeZoneInfo]::FindSystemTimeZoneById('$zone'); 'ok' } catch { 'unknown' }"
    if ($verdict -ne 'ok') {
        throw "timeZone '$zone' is one .NET 9 does not know, so the app would refuse to start on every machine the package reaches. Use an IANA or Windows zone id, or 'America/Toronto'."
    }
} else {
    Write-Warning "pwsh is not installed, so '$zone' could not be checked. The app checks it at startup and stops with a clear message if it is wrong."
}

# Rebuilt key by key rather than copied wholesale. The source file is a working
# document that may carry notes to self -- a rotation reminder, a comment about
# where the secret came from -- and those should not travel to every machine in
# the team. What ships is exactly the five keys the app reads.
$out = [ordered] @{
    tenantUrl    = $shipped.tenantUrl
    clientId     = $shipped.clientId
    clientSecret = $shipped.clientSecret
    redirectUri  = if ($shipped.redirectUri) { $shipped.redirectUri } else { "http://localhost:51820/oauth/callback" }
    timeZone     = $shipped.timeZone
}

$json = $out | ConvertTo-Json -Depth 5

# Written BOM-free: the app reads this with File.ReadAllText, and a byte-order
# mark in front of the first key is exactly the kind of thing that works until
# it does not.
[System.IO.File]::WriteAllText($defaults, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "  Wrote defaults.json: tenant $($out.tenantUrl), zone $($out.timeZone)."

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host "Zipping..."
Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zip -CompressionLevel Optimal

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  $zip"
Write-Host "  $sizeMb MB -- unzip anywhere and run $exeName; nothing to install."
Write-Host ""
Write-Host "First run on a machine: launch it, click Sign in, use your own Panopto"
Write-Host "account in the browser. After that it remembers you."
