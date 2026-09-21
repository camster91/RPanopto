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

    public TokenSet ToTokenSet() => new(
        AccessToken ?? throw new InvalidOperationException("Token response contained no access_token."),
        RefreshToken,
        DateTimeOffset.UtcNow.AddSeconds(ExpiresInSeconds));
}
