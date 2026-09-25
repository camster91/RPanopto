using System.Text.Json;
using System.Text.Json.Serialization;
using PanoptoScheduler.Core.Diagnostics;

namespace PanoptoScheduler.Core.Updates;

/// <summary>
/// The latest published version, read from a public gist.
///
/// <para><b>Why a gist and not GitHub releases.</b> The repository is private,
/// so GitHub answers every unauthenticated request for it with 404 — the app
/// could not even learn a version exists without a token, and shipping a
/// token in the package means shipping read access to the repository's
/// source. The gist carries one version number and nothing else: no source,
/// no zip, no tenant. A person who finds it learns that some version of
/// something exists, which is the entire disclosure.</para>
///
/// <para><b>Why every failure is quiet.</b> An update check is a courtesy, not
/// a step in anyone's work. The feed being unreachable must not produce a
/// dialog, a status line, or any interruption for an operator about to book a
/// recording — the app they are running is the one they have, and it works.
/// Each failure leaves one <see cref="AppLog.Warn"/> line so a feed URL that
/// has rotted is diagnosable from the log without ever having been the
/// user's problem.</para>
/// </summary>
public static class VersionFeed
{
    /// <summary>
    /// The gist's raw URL. Raw with no revision, so a gist edit takes effect
    /// without the app changing. This is the one place the feed's location is
    /// written down in the app; the release script edits the gist, not the
    /// binary.
    /// </summary>
    public const string FeedUrl =
        "https://gist.githubusercontent.com/camster91/f55820430e6625c7e862078ac0eb8bbb/raw/version.json";

    /// <summary>The newest published version, exactly as the feed spells it.</summary>
    public sealed record UpdateInfo(
        [property: JsonPropertyName("version")] string LatestVersion);

    /// <summary>
    /// Short, because the check waits for nobody: it runs while the calendar
    /// is loading, and a feed that takes ten seconds is a feed that is not
    /// answering.
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// Reads the feed, or returns <see langword="null"/> when anything at all
    /// went wrong — unreachable, slow, a non-200 body, unparseable JSON, or a
    /// version string <see cref="Version"/> cannot read.
    /// </summary>
    /// <param name="feed">
    /// Where to read from. Defaults to <see cref="FeedUrl"/>; the tests point
    /// this at their own loopback server, which is why it exists.
    /// </param>
    public static async Task<UpdateInfo?> TryReadAsync(
        Uri? feed = null, CancellationToken ct = default)
    {
        try
        {
            using var response = await Http
                .GetAsync(feed ?? new Uri(FeedUrl), ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                AppLog.Warn($"Version feed answered {response.StatusCode}; leaving the update check alone.");
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var info = JsonSerializer.Deserialize<UpdateInfo>(body);

            // The feed is one string; anything the app cannot turn into a
            // version is a feed nobody edited carefully, and "treat junk as
            // the newest version" is the one reading that shows a banner for
            // an update that does not exist.
            if (info is null || !Version.TryParse(info.LatestVersion, out _))
            {
                AppLog.Warn($"Version feed carried '{info?.LatestVersion ?? "(no version key)"}', which is not a version.");
                return null;
            }

            return info;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                    or OperationCanceledException
                                    or JsonException)
        {
            AppLog.Warn($"Version feed could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="latest"/> is ahead of <paramref name="current"/>,
    /// comparing them the way <see cref="Version"/> does, with one correction:
    /// a component <see cref="Version"/> never had reads as zero rather than
    /// as -1, so "1.4" and "1.4.0" are the same version and not a banner. A
    /// banner for a version the operator already has is worse than no banner,
    /// and a format drift across releases — the packager writing 1.4 once and
    /// 1.4.0 the next time — is exactly how it would happen. Anything that
    /// does not parse on either side still answers false.
    /// </summary>
    public static bool IsNewer(string? latest, string? current)
        => Version.TryParse(latest?.Trim(), out var newest)
            && Version.TryParse(current?.Trim(), out var installed)
            && WithImplicitZeroes(newest) > WithImplicitZeroes(installed);

    /// <summary>
    /// Fills the components <see cref="Version"/> reports as -1 with zeros, so
    /// the comparison sees "1.4" as "1.4.0" instead of something one revision
    /// below it.
    /// </summary>
    private static Version WithImplicitZeroes(Version version) =>
        new(version.Major, version.Minor,
            Math.Max(0, version.Build), Math.Max(0, version.Revision));
}
