using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PanoptoScheduler.Core.Auth;

namespace PanoptoScheduler.Core.Configuration;

/// <summary>
/// Caches sessions in <c>~/.panopto-scheduler/tokens.dat</c>, encrypted with the
/// Windows Data Protection API under the current user.
///
/// <para>Encrypted rather than plaintext because the refresh token is the
/// long-lived half: an access token is dead within the hour, a refresh token is
/// good for weeks. Scope is <see cref="DataProtectionScope.CurrentUser"/>, so a
/// copy of the file is inert on another machine or under another account — which
/// is what makes it safe to leave sitting in a profile that gets backed up.</para>
///
/// <para>The client secret is deliberately <i>not</i> here. It stays in the
/// plaintext <c>credentials.json</c> that a person or an admin edits by hand,
/// because on its own it grants nothing — Panopto still requires the user to
/// sign in.</para>
/// </summary>
public sealed class DpapiTokenStore : ITokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PanoptoScheduler.TokenCache.v1");

    private readonly string _path;

    public DpapiTokenStore(string? path = null) => _path = path ?? DefaultPath;

    public static string DefaultPath => Path.Combine(CredentialStore.Directory, "tokens.dat");

    public TokenSet? Load(string tenantUrl, string clientId)
    {
        if (!File.Exists(_path)) return null;

        return ReadAll().TryGetValue(Key(tenantUrl, clientId), out var tokens) ? tokens : null;
    }

    public void Save(string tenantUrl, string clientId, TokenSet tokens)
    {
        var all = ReadAll();
        all[Key(tenantUrl, clientId)] = tokens;
        WriteAll(all);
    }

    public void Clear(string tenantUrl, string clientId)
    {
        var all = ReadAll();
        if (all.Remove(Key(tenantUrl, clientId))) WriteAll(all);
    }

    /// <summary>
    /// Tenant and client id together, so two accounts on one machine — or one
    /// account against two tenants — cannot inherit each other's session.
    /// </summary>
    private static string Key(string tenantUrl, string clientId)
        => $"{tenantUrl.TrimEnd('/').ToLowerInvariant()}|{clientId.Trim().ToLowerInvariant()}";

    private Dictionary<string, TokenSet> ReadAll()
    {
        if (!File.Exists(_path)) return NewCache();

        // Inline rather than behind a helper: the platform analyser only
        // suppresses CA1416 when it can see the OperatingSystem check itself.
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(WindowsOnly);

        try

        {
            var plain = ProtectedData.Unprotect(
                File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);

            return JsonSerializer.Deserialize<Dictionary<string, TokenSet>>(plain)
                   ?? NewCache();
        }
        catch (CryptographicException)
        {
            // Written under a different Windows account or on a different
            // machine, or truncated. Discard it so the user is asked to sign in
            // once rather than hitting this on every launch.
            TryDelete();
            return NewCache();
        }
        catch (JsonException)
        {
            TryDelete();
            return NewCache();
        }
    }

    private void WriteAll(Dictionary<string, TokenSet> all)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(WindowsOnly);

        var plain = JsonSerializer.SerializeToUtf8Bytes(all);
        var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // Temp file then move: an interrupted write must not leave a truncated
        // cache that fails to decrypt on the next launch.
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, _path, overwrite: true);
    }

    private void TryDelete()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // Locked by something else. Harmless — it will be overwritten.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private const string WindowsOnly =
        "The token cache uses the Windows Data Protection API, and Panopto Scheduler targets Windows.";

    private static Dictionary<string, TokenSet> NewCache() => new(StringComparer.Ordinal);
}
