using System.Security.Cryptography;
using System.Text;

namespace PanoptoScheduler.Core.Auth;

/// <summary>
/// PKCE (RFC 7636) verifier and challenge generation.
///
/// S256 is used rather than <c>plain</c>: the challenge travels through the
/// browser and the redirect, and only its SHA-256 preimage — the verifier,
/// which never leaves this process — can redeem the code.
/// </summary>
public static class Pkce
{
    public static string CreateVerifier()
        => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string CreateChallenge(string verifier)
        => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Base64url without padding, as RFC 7636 requires.</summary>
    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
