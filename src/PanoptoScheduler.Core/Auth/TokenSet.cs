using System.Text.Json.Serialization;

namespace PanoptoScheduler.Core.Auth;

/// <summary>An issued access token and, when <c>offline_access</c> was granted, its refresh token.</summary>
public sealed record TokenSet(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// Treated as expired slightly early, so a token cannot lapse mid-request
    /// between the check and the server reading it.
    /// </summary>
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);

    // Computed, so they must not reach the on-disk cache — a stale IsExpired
    // written to disk would be read back and believed.
    [JsonIgnore]
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt - Skew;

    [JsonIgnore]
    public bool CanRefresh => !string.IsNullOrWhiteSpace(RefreshToken);
}

/// <summary>Wire shape of Panopto's token endpoint response.</summary>
internal sealed class TokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresInSeconds { get; set; } = 3600;

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    /// <summary>
    /// Builds the token set from a wire response.
    ///
    /// <para>RFC 6749 §6 lets a refresh grant omit <c>refresh_token</c>
    /// entirely. Treating that as "no refresh token" would overwrite a good
    /// cached session with a set that cannot refresh, so the next launch
    /// would demand an interactive browser sign-in with the previously good
    /// token already gone from disk. <paramref name="previousRefreshToken"/>
    /// is carried forward when the response does not supply a new one;
    /// there is nothing to carry on the authorization-code path, where the
    /// response must carry its own.</para>
    /// </summary>
    public TokenSet ToTokenSet(string? previousRefreshToken = null) => new(
        AccessToken ?? throw new InvalidOperationException("Token response contained no access_token."),
        RefreshToken ?? previousRefreshToken,
        DateTimeOffset.UtcNow.AddSeconds(ExpiresInSeconds));
}
