using PanoptoScheduler.Core.Auth;

namespace PanoptoScheduler.Core.Tests;

public class PkceTests
{
    /// <summary>
    /// RFC 7636 Appendix B. If this passes, the S256 implementation is correct
    /// by definition rather than by our own reasoning — which matters, because
    /// a subtly wrong challenge produces an opaque failure from Panopto with no
    /// hint as to the cause.
    /// </summary>
    [Fact]
    public void Matches_the_RFC_7636_test_vector()
    {
        var challenge = Pkce.CreateChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");

        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", challenge);
    }

    [Fact]
    public void Verifier_is_the_length_the_spec_requires()
    {
        // 32 random bytes -> 43 base64url characters, within RFC 7636's 43..128.
        var verifier = Pkce.CreateVerifier();

        Assert.Equal(43, verifier.Length);
    }

    [Fact]
    public void Verifier_contains_only_base64url_characters()
    {
        // '+' '/' '=' would be mangled in transit through a query string.
        for (var i = 0; i < 50; i++)
        {
            var verifier = Pkce.CreateVerifier();
            Assert.All(verifier, c =>
                Assert.True(char.IsLetterOrDigit(c) || c is '-' or '_',
                    $"unexpected character '{c}' in verifier"));
        }
    }

    [Fact]
    public void Verifiers_are_not_reused()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 100; i++) Assert.True(seen.Add(Pkce.CreateVerifier()));
    }

    [Fact]
    public void Challenge_differs_from_verifier()
    {
        var verifier = Pkce.CreateVerifier();
        Assert.NotEqual(verifier, Pkce.CreateChallenge(verifier));
    }

    [Fact]
    public void Challenge_is_deterministic_for_a_given_verifier()
    {
        var verifier = Pkce.CreateVerifier();
        Assert.Equal(Pkce.CreateChallenge(verifier), Pkce.CreateChallenge(verifier));
    }
}

public class TokenSetTests
{
    [Fact]
    public void Valid_token_is_not_expired()
    {
        var tokens = new TokenSet("abc", "refresh", DateTimeOffset.UtcNow.AddHours(1));
        Assert.False(tokens.IsExpired);
    }

    [Fact]
    public void Token_expiring_soon_is_treated_as_expired()
    {
        // A token with 30s left would lapse mid-request, so it is refreshed early.
        var tokens = new TokenSet("abc", "refresh", DateTimeOffset.UtcNow.AddSeconds(30));
        Assert.True(tokens.IsExpired);
    }

    [Fact]
    public void Refresh_token_presence_decides_refreshability()
    {
        var expiring = DateTimeOffset.UtcNow.AddSeconds(30);

        Assert.True(new TokenSet("a", "refresh-me", expiring).CanRefresh);
        Assert.False(new TokenSet("a", null, expiring).CanRefresh);
        Assert.False(new TokenSet("a", "  ", expiring).CanRefresh);
    }
}
