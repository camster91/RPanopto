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

# X.Y.Z and nothing else, enforced before anything is touched. A two-part
# version ("1.4") is a different number to the app's comparison than its
# three-part spelling ("1.4.0"), so a packager who normalized the format
# mid-stream would banner every copy of the shorter spelling with an update
# it already has. The app forgives the comparison anyway, but a gate here is
# cheaper than the app being careful forever: this script is the only writer
# of published version numbers, so the format never has to change.
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw @"
Version '$Version' is not three components.

Use the form X.Y.Z, like 1.4.0. The release tag, the zip name and the csproj
all carry the version, and a format change partway through the app's life
would show every copy of the old spelling an update it already has.
"@
}

$src     = $MyInvocation.MyCommand.Path | Split-Path -Parent
$appProj = Join-Path $src "PanoptoScheduler.App\PanoptoScheduler.App.csproj"
$dist    = Join-Path $src "dist"
$zip     = Join-Path $dist "PanoptoScheduler-$Version-win-x64.zip"
$cer     = Join-Path $dist "PanoptoScheduler-CodeSigning.cer"
$itNote  = Join-Path $dist "Code-signing-certificate-for-IT.txt"

# The gist that VersionFeed.cs reads. The id is the hex string inside the
# FeedUrl constant there; the two are the same gist and must move together.
$gistId  = "f55820430e6625c7e862078ac0eb8bbb"

# The name publish.ps1 gives the executable; the zip is checked for it below.
$exeName = "PanoptoScheduler.App.exe"

# Named in full rather than left for gh to infer, because inference is what
# breaks here: gh resolves the repository from the remotes in the order it
# finds them, and this checkout's 'origin' is Panopto's public upstream --
# a repository this account cannot release to and must never try.
$repo    = "camster91/panopto-recording-scheduler"

if (-not (Test-Path $appProj)) { throw "Cannot find the app project at $appProj" }

# Every native command whose stderr or exit code this script looks at runs
# through here. Under Windows PowerShell 5.1 -- the packager's shell -- a
# native command's stderr that is redirected (2>$null) while the preference
# is 'Stop' becomes a terminating NativeCommandError. "gh release view"
# answering "release not found", which is the ordinary answer for every new
# tag, would abort the script before it ever reached $LASTEXITCODE, and so
# would a gh or git warning printed on the way to success. Inside this block
# the preference is 'Continue' and the caller decides by $LASTEXITCODE, which
# is the only verdict a native command actually gives. PowerShell 7 does not
# convert stderr like this, so the wrapper is a no-op there.
function Invoke-Native([scriptblock] $Command) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command } finally { $ErrorActionPreference = $saved }
}

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

# The tag is created at the commit this checkout is on, named explicitly with
# --target below. Left to gh, the tag lands on the head of the remote's
# default branch -- whatever was pushed last, which need not be what was
# built -- and the source behind a release is then a guess. Naming HEAD only
# helps if HEAD is what was built and GitHub has it, so both are checked:
#
#   * The working tree must be clean. An uncommitted edit is in the zip but in
#     no commit, so no tag can point at the source that was shipped. Ignored
#     files (src\dist, bin, obj) do not count; untracked ones do, because the
#     SDK compiles every .cs file in the folder whether git knows it or not.
#   * HEAD must be on a branch of the remote that IS $repo. Not any remote:
#     'origin' in this checkout is Panopto's public upstream, so "some remote
#     has it" would pass for exactly the wrong repository. A commit GitHub has
#     never seen cannot be tagged, and a release made anyway would point at
#     the default branch instead -- the silent version of the problem above.
#
# The remote-tracking refs are read as they are, not fetched: a push updates
# them, so the only way they are stale is a push from another machine, and
# then the refusal names the fix (git fetch).
$head = "$(Invoke-Native { git -C $src rev-parse HEAD 2>$null })".Trim()
if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$') {
    throw "Could not read the current commit with git (exit code $LASTEXITCODE). This script tags the commit it runs from, so it has to run from the checkout the zip was built in."
}

$dirty = @(Invoke-Native { git -C $src status --porcelain 2>$null })
if ($LASTEXITCODE -ne 0) { throw "git status failed with exit code $LASTEXITCODE -- cannot tell whether the checkout matches a commit." }
if ($dirty.Count -gt 0) {
    throw @"
The checkout has uncommitted changes:

$($dirty -join "`n")

The release is tagged at the current commit, and a zip built from these files
would match no commit at all. Commit and push them, run publish.ps1 again, and
then release -- or discard them if the zip was built without them.
"@
}

$releaseRemote = $null
foreach ($remoteName in @(Invoke-Native { git -C $src remote 2>$null })) {
    $remoteUrl = "$(Invoke-Native { git -C $src remote get-url $remoteName 2>$null })".Trim()
    if ($remoteUrl -match ('[:/]' + [regex]::Escape($repo) + '(\.git)?/?$')) { $releaseRemote = $remoteName; break }
}
if (-not $releaseRemote) {
    throw "No git remote in this checkout points at $repo, so there is no way to check that commit $head has been pushed there. Add one (git remote add release https://github.com/$repo.git), push, and run this again."
}

$onRemote = @(Invoke-Native { git -C $src branch -r --contains $head --list "$releaseRemote/*" 2>$null })
if ($LASTEXITCODE -ne 0 -or $onRemote.Count -eq 0) {
    throw @"
Commit $head is not on any branch of '$releaseRemote' ($repo).

The release is tagged at this commit, and GitHub cannot tag a commit it does
not have. Push it first (git push $releaseRemote HEAD:modern-app), or run
'git fetch $releaseRemote' if it was pushed from another machine.
"@
}

# Checked inside the zip, not beside it: the zip is the thing every machine
# installs, and nothing above has looked inside it. publish.ps1 refuses to
# build an unsigned zip only when a thumbprint is passed, and a zip left in
# dist by an older run carries whatever version it was built as -- both of
# which upload without complaint and then either warn on every machine or
# banner an update that installs the old build.
#
# The signature test is not "Status is Valid", for the reason publish.ps1
# gives: this machine does not trust its own self-signed certificate, so
# Status reads NotTrusted or UnknownError here however good the signature is
# (and Valid on a machine where IT has deployed the certificate). What can be
# checked is that each signed file is signed -- not NotSigned, not
# HashMismatch -- by the very certificate shipped beside the zip, with a
# timestamp. That also proves the .cer IT is handed is the right one.
#
# The version is read from ProductVersion, which carries the csproj <Version>
# (FileVersion is a separate, deliberately unbumped number). The SDK appends
# "+<commit sha>" to it, the same suffix the app splits off for its title bar,
# and that is the strongest check available that the zip was built from the
# commit being tagged -- so when present it must match HEAD.
$check = Join-Path ([System.IO.Path]::GetTempPath()) ("panopto-release-check-" + [Guid]::NewGuid().ToString("N"))
try {
    Expand-Archive -LiteralPath $zip -DestinationPath $check

    foreach ($name in @($exeName, (Split-Path -Leaf $cer), (Split-Path -Leaf $itNote))) {
        if (-not (Test-Path -LiteralPath (Join-Path $check $name))) {
            throw "$zip has no $name in it. Run publish.ps1 again -- it builds the zip with the executable, the certificate and the IT note inside."
        }
    }

    $shippedCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $cer
    foreach ($name in @($exeName, "install.ps1", "uninstall.ps1")) {
        $signature = Get-AuthenticodeSignature -FilePath (Join-Path $check $name)
        $status = [string] $signature.Status
        if (($status -notin @("Valid", "NotTrusted", "UnknownError")) -or
            (-not $signature.SignerCertificate) -or
            ($signature.SignerCertificate.Thumbprint -ne $shippedCert.Thumbprint) -or
            (-not $signature.TimeStamperCertificate)) {
            throw @"
$name in the zip is not signed by the shipped certificate (status: $status, signer: $($signature.SignerCertificate.Thumbprint), expected: $($shippedCert.Thumbprint)).

Every machine would warn about it, or refuse it outright. Run
publish.ps1 -CertificateThumbprint <thumbprint> again with the certificate
that $(Split-Path -Leaf $cer) was exported from.
"@
        }
    }

    $productVersion = [string] (Get-Item -LiteralPath (Join-Path $check $exeName)).VersionInfo.ProductVersion
    $builtVersion, $builtCommit = $productVersion -split '\+', 2
    if ($builtVersion -ne $Version) {
        throw "The $exeName in $zip says it is '$productVersion', not $Version. The zip in dist is from another build; run publish.ps1 again."
    }
    if ($builtCommit -and -not $head.StartsWith($builtCommit, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The zip was built from commit $builtCommit, but this checkout is at $head, which is where the release would be tagged. Run publish.ps1 again from this commit, or check out the one the zip was built from."
    }
} finally {
    Remove-Item -LiteralPath $check -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "  Checked the zip: signed by the shipped certificate, version $Version, built from $($head.Substring(0, 12))."

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
PanoptoScheduler-CodeSigning.cer and Code-signing-certificate-for-IT.txt
(inside the zip, and attached below) to IT -- the note tells them where it goes.
"@

# Release first, gist second -- see the comment at the top of this file for
# why the order looks backwards. gh creates the tag itself, at the commit
# named by --target (HEAD, checked above to be clean and pushed), so there is
# no tag to push by hand.
#
# A release that already exists is a resume, not a failure, but "exists" is
# not the same as "shipped". gh release create makes a DRAFT first, uploads
# the files to it, and only then publishes it -- so a run that died mid-upload
# (a dropped connection on a 60 MB zip is the usual one) leaves a draft with
# some or none of the files. A draft is invisible to /releases/latest and to
# everyone without write access; bumping the gist on its account would banner
# every running copy towards a page that still serves the previous version.
#
# So the release is finished rather than refused: any of the three files that
# is missing, or did not finish uploading, is uploaded again from dist (the
# zip having just been checked above), and a draft is then published at
# HEAD. Finishing is the safer of the two choices, not just the more
# convenient one: refusing would leave the packager to delete or complete the
# draft by hand, which is exactly the hand-typed half ship this script exists
# to prevent, and everything the finish uploads has passed the same checks a
# fresh release would.
#
# One case is refused: a release that is already PUBLISHED with a zip of the
# same name but different size. People may already have downloaded that one,
# and silently replacing it is a decision for a person, not for a resume.
#
# Whatever path was taken, the release is read back and the gist is bumped
# only if it is published and carries a fully uploaded zip.
function Get-Release {
    $json = Invoke-Native { gh release view $tag -R $repo --json "isDraft,assets" 2>$null }
    if ($LASTEXITCODE -ne 0 -or -not $json) { return $null }
    return (($json | Out-String) | ConvertFrom-Json)
}

# "complete", "different" (uploaded, but not this file) or "missing" (absent,
# or an upload that never finished -- GitHub keeps those as state "starter").
function Get-AssetState($Release, [string] $File) {
    $name = Split-Path -Leaf $File
    $size = (Get-Item -LiteralPath $File).Length
    $asset = @($Release.assets | Where-Object { $_.name -eq $name -and (-not $_.state -or $_.state -eq "uploaded") }) |
        Select-Object -First 1
    if (-not $asset) { return "missing" }
    if ([int64] $asset.size -ne $size) { return "different" }
    return "complete"
}

$release = Get-Release
if (-not $release) {
    Invoke-Native { gh release create $tag -R $repo --target $head --title "Panopto Scheduler $Version" --notes $notes $zip $cer $itNote }
    if ($LASTEXITCODE -ne 0) {
        throw @"
gh release create failed with exit code $LASTEXITCODE. The version gist was not
touched, so no running copy has been told about $Version.

gh uploads to a draft before publishing it, so a draft $tag may have been left
behind with some of the files. Re-running this script finishes that draft
rather than starting over.
"@
    }
    Write-Host "  Release $tag created at $($head.Substring(0, 12)) with the zip, the certificate and the IT note."
} else {
    $published = -not $release.isDraft
    if ($published -and (Get-AssetState $release $zip) -eq "different") {
        throw @"
Release $tag is already published, and its $(Split-Path -Leaf $zip) differs
from the one in dist. People may have downloaded that one, so it has not been
replaced and the gist has not been touched. If dist holds the right build,
replace it deliberately (gh release upload $tag -R $repo --clobber $zip)
and run this again; otherwise ship the new build as a new version.
"@
    }

    $toUpload = @(@($zip, $cer, $itNote) | Where-Object { (Get-AssetState $release $_) -ne "complete" })
    if ($toUpload.Count -gt 0) {
        Write-Host "  Release $tag exists but is missing $(@($toUpload | ForEach-Object { Split-Path -Leaf $_ }) -join ', ') -- uploading."
        Invoke-Native { gh release upload $tag -R $repo --clobber $toUpload }
        if ($LASTEXITCODE -ne 0) { throw "gh release upload failed with exit code $LASTEXITCODE. The gist was not touched; re-running this script tries again." }
    }

    if ($published) {
        Write-Host "  Release $tag already exists -- finishing the half that did not ship last time (the gist)."
    } else {
        Write-Host "  Release $tag was left as a draft by an earlier run -- publishing it at $($head.Substring(0, 12))."
        Invoke-Native { gh release edit $tag -R $repo --draft=false --target $head --title "Panopto Scheduler $Version" --notes $notes }
        if ($LASTEXITCODE -ne 0) { throw "gh release edit failed with exit code $LASTEXITCODE -- $tag is still a draft and the gist was not touched; re-running this script tries again." }
    }
}

$release = Get-Release
if (-not $release -or $release.isDraft -or (Get-AssetState $release $zip) -ne "complete") {
    throw @"
Release $tag is not published with a complete $(Split-Path -Leaf $zip), so the
version gist was not touched -- bumping it now would send every running copy to
a page that does not offer $Version yet. Check https://github.com/$repo/releases
and re-run this script, which finishes a draft or a partial upload.
"@
}

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
#
# A failure here is the half-shipped state: the release is live and the gist
# still names the previous version, so no running copy knows there is an
# update. The throw names both facts and the one command that finishes the
# job, and the temp file is deliberately kept -- the recovery command points
# at it, and deleting the thing the message names would be the script
# undoing its own instructions.
$feedFile = Join-Path ([System.IO.Path]::GetTempPath()) "panopto-scheduler-version.json"
[System.IO.File]::WriteAllText(
    $feedFile,
    '{ "version": "' + $Version + '" }',
    (New-Object System.Text.UTF8Encoding($false)))

$gistUpdated = $false
try {
    # Through Invoke-Native for the same reason as the gh calls above: if this
    # script's output is redirected (a log file, a transcript), 5.1 turns gh's
    # stderr into a terminating error that would skip the recovery message.
    Invoke-Native { gh gist edit $gistId --filename "version.json" $feedFile }
    if ($LASTEXITCODE -eq 0) { $gistUpdated = $true }
} finally {
    if ($gistUpdated) { Remove-Item $feedFile -Force -ErrorAction SilentlyContinue }
}

if (-not $gistUpdated) {
    throw @"
Release $tag is live, but the version gist still names the previous version,
so no running copy knows the update exists yet. To finish the ship by hand,
fix the cause first (an expired gh login is the usual one), then run:

  gh gist edit $gistId --filename version.json $feedFile

The file named above has been kept. Re-running this script instead would
also work -- it resumes at this step -- but the one command above is all
that is left to do.
"@
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
