<#
.SYNOPSIS
    Ships a version the team can update from.

.DESCRIPTION
    The back half of publishing. publish.ps1 builds the zip; this script puts
    it where the app's update banner can point: a GitHub release on the
    private repository, plus a bump of the public version gist that tells
    running copies a newer build exists.

    The order of the two steps is load-bearing and looks backwards: the
    release is created BEFORE the gist is bumped. The banner only appears
    once the gist names a newer version, so a copy that checks the feed finds
    the release already sitting there. The other order has a window where
    the banner links to a Releases page that is still empty -- an operator
    who follows the banner and finds nothing concludes updating is broken
    and never tries again. If the gist bump fails after the release exists,
    the outcome is only "nobody was told yet", which is the safe direction.

    The gist is public and carries one version number, nothing else -- no
    source, no zip, no tenant names. That is the whole reason the app can
    check for updates without a token: the repository is private and answers
    unauthenticated requests with 404, so shipping a token in the app would
    be shipping read access to the source. The gist id below is the same one
    in VersionFeed.cs's FeedUrl; change both or neither.

    Publishing order, end to end:

      1. Bump <Version> in PanoptoScheduler.App.csproj.
      2. src\publish.ps1 -CertificateThumbprint <thumbprint>
         (builds dist\PanoptoScheduler-<version>-win-x64.zip, signed)
      3. src\release.ps1 -Version <version>

    CI cannot do any of this: the runner must not hold the client secret that
    publish.ps1 packages, and the signing certificate is a machine-local
    secret, so this script runs on the packager's machine by design.

.EXAMPLE
    .\release.ps1 -Version 1.4.0
    Creates release v1.4.0 and points the version gist at 1.4.0.
#>
[CmdletBinding()]
param(
    # The version being shipped, which must match the csproj and the zip
    # publish.ps1 built. The release tag is this with a 'v' in front.
    [Parameter(Mandatory = $true)]
    [string] $Version
)

$ErrorActionPreference = "Stop"

$src     = $MyInvocation.MyCommand.Path | Split-Path -Parent
$appProj = Join-Path $src "PanoptoScheduler.App\PanoptoScheduler.App.csproj"
$dist    = Join-Path $src "dist"
$zip     = Join-Path $dist "PanoptoScheduler-$Version-win-x64.zip"
$cer     = Join-Path $dist "PanoptoScheduler-CodeSigning.cer"
$itNote  = Join-Path $dist "Code-signing-certificate-for-IT.txt"

# The gist that VersionFeed.cs reads. The id is the hex string inside the
# FeedUrl constant there; the two are the same gist and must move together.
$gistId  = "f55820430e6625c7e862078ac0eb8bbb"

# Named in full rather than left for gh to infer, because inference is what
# breaks here: gh resolves the repository from the remotes in the order it
# finds them, and this checkout's 'origin' is Panopto's public upstream --
# a repository this account cannot release to and must never try.
$repo    = "camster91/panopto-scheduler"

if (-not (Test-Path $appProj)) { throw "Cannot find the app project at $appProj" }

# The csproj decides what the running app believes it is, and the gist decides
# what it believes is newest. A zip that disagrees with the csproj ships a
# banner that never appears (or one that never stops appearing), so the two
# are asserted equal before anything is published.
[xml] $project = Get-Content -Raw $appProj
$projectVersion = @($project.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -Last 1
if ($projectVersion -ne $Version) {
    throw @"
The csproj says $projectVersion but this release says $Version.

Bump <Version> in PanoptoScheduler.App.csproj and run publish.ps1 again, or
release $projectVersion. A zip whose contents disagree with its tag cannot be
updated from, because every copy of it would compare itself against the wrong
number forever.
"@
}

if (-not (Test-Path $zip)) {
    throw @"
No $zip.

publish.ps1 builds it. A release without the zip would be a banner that
leads somewhere with nothing to download, which is how people learn to
ignore the banner.
"@
}

# The certificate travels beside the zip so a new machine can be made to trust
# this build without asking the packager for a file. The note tells IT what
# to do with it; one without the other answers a question nobody asked.
foreach ($sidecar in @($cer, $itNote)) {
    if (-not (Test-Path $sidecar)) {
        throw "No $sidecar -- the certificate (or its instructions) must ship beside the zip or IT cannot deploy it."
    }
}

Write-Host "Shipping Panopto Scheduler $Version..."

$tag = "v$Version"
$notes = @"
Panopto Scheduler $Version

On a machine: unzip, then double-click Install.cmd. An existing install is
upgraded in place -- install.ps1 swaps the files and keeps your sign-in,
your settings and your logs; close the app first, and run it as yourself,
not as an administrator.

Running straight from the unzipped folder still works and installs nothing.

The first run after installing asks you to sign in as yourself; after that it
remembers you.

New machine, or warnings from Windows about the publisher? Hand
PanoptoScheduler-CodeSigning.cer and Code-signing-certificate-for-IT.txt to
IT -- the note tells them where it goes.
"@

# Release first, gist second -- see the comment at the top of this file for
# why the order looks backwards. gh creates the tag itself, at the head of
# the default branch (modern-app), so there is no tag to push by hand.
gh release create $tag -R $repo --title "Panopto Scheduler $Version" --notes $notes $zip $cer $itNote
if ($LASTEXITCODE -ne 0) { throw "gh release create failed with exit code $LASTEXITCODE -- nothing else was touched." }

Write-Host "  Release $tag created with the zip, the certificate and the IT note."

# The gist is edited by handing gh a file, because the alternative is typing
# the JSON inline on a command line, and quoting is exactly the thing that
# would one day wrap the version in a stray quote and leave a feed every
# copy of the app quietly fails to parse. The positional file replaces the
# named gist file's content; the local file is written with no BOM for the
# same reason defaults.json in publish.ps1 is: a byte order mark in front of
# the first key is a failure that works until it doesn't.
#
# One thing this edit cannot do: make it visible instantly. The gist's raw
# URL is served from a cache that keeps serving the previous version for a
# few minutes after an edit, so a copy launched right now still sees the old
# number. That is deliberate and fine -- an update check is a courtesy, and
# "the banner appears within a few minutes of shipping" is the promise, not
# "the moment release.ps1 returns".
$feedFile = Join-Path ([System.IO.Path]::GetTempPath()) "panopto-scheduler-version.json"
[System.IO.File]::WriteAllText(
    $feedFile,
    '{ "version": "' + $Version + '" }',
    (New-Object System.Text.UTF8Encoding($false)))

try {
    gh gist edit $gistId --filename "version.json" $feedFile
    if ($LASTEXITCODE -ne 0) { throw "gh gist edit failed with exit code $LASTEXITCODE." }
} finally {
    Remove-Item $feedFile -Force -ErrorAction SilentlyContinue
}

Write-Host "  Version gist now says $Version -- every running copy will notice on its next start."

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  https://github.com/$repo/releases/tag/$tag"
Write-Host ""
Write-Host "The release page is the download: the banner in the app opens it,"
Write-Host "and the browser's own GitHub sign-in is what lets a team member"
Write-Host "see it. Anyone who gets the banner but a 404 from the page is not a"
Write-Host "collaborator yet -- invite them:"
Write-Host ""
Write-Host "  gh api -X PUT repos/$repo/collaborators/<their-github-name> -F permission=pull"