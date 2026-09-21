using System.Text.Json;
using PanoptoScheduler.Core.Auth;

namespace PanoptoScheduler.Core.Configuration;

/// <summary>Tenant connection settings, as stored on disk.</summary>
public sealed record PanoptoCredentials
{
    public required string TenantUrl { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public string RedirectUri { get; init; } = "http://localhost:51820/oauth/callback";

    /// <summary>
    /// The zone the rooms keep time in, as an IANA or Windows zone id.
    ///
    /// <para>Load-bearing for writes, not a display preference: Panopto stores the
    /// instant it is sent, so this is what decides which hour a booking lands on
    /// the room's clock. See <see cref="Scheduling.RoomClock"/>.</para>
    ///
    /// <para>Optional. Absent or empty means <see cref="Scheduling.RoomClock.DefaultZoneId"/>
    /// — a name that is present but unresolvable is an error rather than a reason
    /// to guess.</para>
    /// </summary>
    public string TimeZone { get; init; } = Scheduling.RoomClock.DefaultZoneId;

    /// <summary>
    /// The zone, resolved. Throws when the file names one this machine does not
    /// know, rather than booking into a different hour.
    /// </summary>
    public TimeZoneInfo ResolveTimeZone() => Scheduling.RoomClock.Resolve(TimeZone);

    public OAuthOptions ToOAuthOptions() => new()
    {
        TenantUrl = TenantUrl,
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        RedirectUri = RedirectUri,
    };
}

/// <summary>
/// Loads and saves credentials in a folder outside the repository.
///
/// Deliberately not app.config, environment variables, or anything inside the
/// repo: the secret must never be committed, and must not be visible to anyone
/// browsing the project directory.
///
/// <para>Written as plaintext JSON on purpose. A person or an admin edits this
/// file by hand when onboarding someone, and the client secret on its own grants
/// no access to any recording — Panopto still requires that person to sign in as
/// themselves. The refresh token, which <i>is</i> a real credential, is kept
/// separately and encrypted; see <see cref="DpapiTokenStore"/>.</para>
/// </summary>
public static class CredentialStore
{
    /// <summary><c>~/.panopto-scheduler</c></summary>
    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".panopto-scheduler");

    /// <summary><c>~/.panopto-scheduler/credentials.json</c></summary>
    public static string DefaultPath => Path.Combine(Directory, "credentials.json");

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Matches the hand-written file: camelCase keys, indented, readable.</summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static bool Exists(string? path = null) => File.Exists(path ?? DefaultPath);

    /// <summary>Loads credentials, or returns <see langword="null"/> if the file is absent.</summary>
    public static PanoptoCredentials? TryLoad(string? path = null) => Exists(path) ? Load(path) : null;

    /// <summary>
    /// The credentials this machine should use, from the best source available.
    ///
    /// <para>In order: the person's own file, then the one shipped beside the
    /// app, then <see langword="null"/> — which means nothing is configured and
    /// the first-run dialog is the only way forward.</para>
    ///
    /// <para>The person's file wins over the shipped one on purpose. It is what
    /// lets someone point their machine at a different tenant, and what keeps a
    /// secret rotation from needing the app repackaged: edit one file and this
    /// machine is on the new secret while everyone else's still works.</para>
    /// </summary>
    /// <param name="path">
    /// The person's own file. Defaults to <see cref="DefaultPath"/>; tests pass
    /// a temporary one rather than depending on the machine they run on.
    /// </param>
    /// <param name="shippedPath">
    /// The file that ships beside the app. Defaults to
    /// <see cref="ShippedDefaults.DefaultPath"/>, for the same reason.
    /// </param>
    public static PanoptoCredentials? Resolve(string? path = null, string? shippedPath = null)
        => TryLoad(path) ?? ShippedDefaults.TryLoad(shippedPath);

    public static PanoptoCredentials Load(string? path = null)
    {
        var file = path ?? DefaultPath;

        if (!File.Exists(file))
            throw new FileNotFoundException(
                $"No credentials found at {file}. Create it with the tenant URL, client id and client secret.",
                file);

        var credentials = JsonSerializer.Deserialize<PanoptoCredentials>(
            File.ReadAllText(file), ReadOptions)
            ?? throw new InvalidOperationException($"Credentials file at {file} was empty.");

        Validate(credentials, file);
        return credentials;
    }

    public static void Save(PanoptoCredentials credentials, string? path = null)
    {
        var file = path ?? DefaultPath;
        Validate(credentials, file);

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(credentials, WriteOptions));
        File.Move(temp, file, overwrite: true);
    }

    private static void Validate(PanoptoCredentials credentials, string file)
    {
        if (string.IsNullOrWhiteSpace(credentials.TenantUrl) ||
            string.IsNullOrWhiteSpace(credentials.ClientId) ||
            string.IsNullOrWhiteSpace(credentials.ClientSecret))
            throw new InvalidOperationException(
                $"Credentials file at {file} must set tenantUrl, clientId and clientSecret.");

        if (!Uri.TryCreate(credentials.TenantUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException(
                $"tenantUrl in {file} is not an absolute URL: '{credentials.TenantUrl}'.");

        if (!Uri.TryCreate(credentials.RedirectUri, UriKind.Absolute, out _))
            throw new InvalidOperationException(
                $"redirectUri in {file} is not an absolute URL: '{credentials.RedirectUri}'.");
    }
}
