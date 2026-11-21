using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;

namespace UnifiMcp;

public interface ITokenIssuer
{
    /// <summary>The issuer string every token carries: the public base URL.</summary>
    string Issuer { get; }
    /// <summary>The audience: the MCP endpoint URL, so a token for this resource is useless anywhere else.</summary>
    string Audience { get; }
    TimeSpan AccessTokenLifetime { get; }

    Task<RsaSecurityKey> GetSigningKeyAsync(CancellationToken ct = default);
    Task<string> IssueAccessTokenAsync(OAuthUser user, string clientId, string scope, CancellationToken ct = default);
    /// <summary>Public half of the signing key, as a JWKS document.</summary>
    Task<object> GetJwksAsync(CancellationToken ct = default);
}

/// <summary>
/// Mints the bearer tokens Claude presents. One RSA-2048 key, generated on first use and stored in the OAuth store
/// wrapped by DataProtection (whose keyring is sealed under UNIFI_MCP_DATA_KEY in the data folder), so a restart does
/// not log the connector out. The private key never leaves the process unwrapped.
/// </summary>
public sealed class TokenIssuer : ITokenIssuer
{
    private const string ProtectorPurpose = "UnifiMcp.OAuth.SigningKey.v1";

    private readonly SqliteOAuthStore _store;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private RsaSecurityKey? _key;

    public string Issuer { get; }
    public string Audience { get; }
    public TimeSpan AccessTokenLifetime { get; } = TimeSpan.FromHours(1);

    public TokenIssuer(SqliteOAuthStore store, IDataProtectionProvider dataProtection, string issuer, string audience, TimeProvider time)
    {
        _store = store;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _time = time;
        Issuer = issuer.TrimEnd('/');
        Audience = audience;
    }

    public async Task<RsaSecurityKey> GetSigningKeyAsync(CancellationToken ct = default)
    {
        if (_key is not null) return _key;
        await _lock.WaitAsync(ct);
        try
        {
            if (_key is not null) return _key;

            var stored = await _store.GetSigningKeyAsync(ct);
            if (stored is null)
            {
                using var fresh = RSA.Create(2048);
                stored = await _store.SaveSigningKeyIfAbsentAsync(new SigningKey
                {
                    KeyId = Convert.ToHexString(SHA256.HashData(fresh.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant(),
                    ProtectedPrivateKey = _protector.Protect(Convert.ToBase64String(fresh.ExportPkcs8PrivateKey())),
                }, ct);
            }

            var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(_protector.Unprotect(stored.ProtectedPrivateKey)), out _);
            _key = new RsaSecurityKey(rsa) { KeyId = stored.KeyId };
            return _key;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> IssueAccessTokenAsync(OAuthUser user, string clientId, string scope, CancellationToken ct = default)
    {
        var key = await GetSigningKeyAsync(ct);
        var now = _time.GetUtcNow().UtcDateTime;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Subject),
            new(JwtRegisteredClaimNames.Jti, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("name", user.Name),
            new("client_id", clientId),
            new("scope", scope),
        };

        var header = new JwtHeader(new SigningCredentials(key, SecurityAlgorithms.RsaSha256)) { ["typ"] = "at+jwt" };
        var payload = new JwtPayload(Issuer, Audience, claims, notBefore: now.AddMinutes(-1), expires: now.Add(AccessTokenLifetime), issuedAt: now);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    public async Task<object> GetJwksAsync(CancellationToken ct = default)
    {
        var key = await GetSigningKeyAsync(ct);
        var p = key.Rsa.ExportParameters(false);
        return new
        {
            keys = new[]
            {
                new { kty = "RSA", use = "sig", alg = "RS256", kid = key.KeyId, n = Base64UrlEncoder.Encode(p.Modulus), e = Base64UrlEncoder.Encode(p.Exponent) },
            },
        };
    }

    /// <summary>SHA-256 of a code or token, hex: what the store keys on, so the raw value is never persisted.</summary>
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>A 256-bit random value as base64url: authorization codes and refresh tokens.</summary>
    public static string NewSecret() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>RFC 7636 S256 only: base64url(SHA-256(code_verifier)) must equal the stored challenge.</summary>
    public static bool VerifyPkce(string codeVerifier, string codeChallenge)
    {
        if (codeVerifier.Length is < 43 or > 128) return false;
        var computed = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(codeChallenge));
    }
}
