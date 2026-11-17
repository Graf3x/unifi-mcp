using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace UnifiMcp.Tests;

public sealed class TokenIssuerTests : IDisposable
{
    private const string Base = "https://unifi.example.com";
    private const string Audience = Base + "/mcp";

    private readonly string _dir = Directory.CreateTempSubdirectory("unifi-mcp-issuer").FullName;
    private string DataDir => Path.Combine(_dir, "data");

    private static readonly OAuthUser User = new() { Subject = "google-sub-1", Email = "owner@example.test", Name = "Owner" };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private (TokenIssuer Issuer, ServiceProvider Protection) NewIssuer(TimeProvider time, string dataKey = DataKeyTests.KeyA)
    {
        var protection = DataKeyTests.Protection(DataDir, dataKey);
        var store = new SqliteOAuthStore(Path.Combine(DataDir, "oauth.db"), time);
        return (new TokenIssuer(store, protection.GetRequiredService<IDataProtectionProvider>(), Base, Audience, time), protection);
    }

    [Fact]
    public async Task Issued_token_has_the_pinned_shape()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var (issuer, protection) = NewIssuer(time);
        using var _ = protection;

        Assert.Equal(Base, issuer.Issuer);
        Assert.Equal(Audience, issuer.Audience);
        Assert.Equal(TimeSpan.FromHours(1), issuer.AccessTokenLifetime);

        var jwt = await issuer.IssueAccessTokenAsync(User, "client-1", "mcp:tools");
        var t = new JwtSecurityTokenHandler().ReadJwtToken(jwt);

        Assert.Equal("RS256", t.Header.Alg);
        Assert.Equal("at+jwt", t.Header.Typ);
        var key = await issuer.GetSigningKeyAsync();
        Assert.Equal(key.KeyId, t.Header.Kid);
        Assert.Equal(2048, key.KeySize);
        Assert.Equal(Base, t.Issuer);
        Assert.Equal([Audience], t.Audiences);

        var iat = long.Parse(t.Claims.Single(c => c.Type == "iat").Value);
        var exp = long.Parse(t.Claims.Single(c => c.Type == "exp").Value);
        Assert.Equal(time.GetUtcNow().ToUnixTimeSeconds(), iat);
        Assert.Equal(3600, exp - iat);

        string Claim(string type) => t.Claims.Single(c => c.Type == type).Value;
        Assert.Equal(User.Subject, Claim("sub"));
        Assert.Matches("^[0-9a-f]{32}$", Claim("jti"));
        Assert.Equal(User.Email, Claim("email"));
        Assert.Equal(User.Name, Claim("name"));
        Assert.Equal("client-1", Claim("client_id"));
        Assert.Equal("mcp:tools", Claim("scope"));

        // Two tokens never share a jti.
        var second = new JwtSecurityTokenHandler().ReadJwtToken(await issuer.IssueAccessTokenAsync(User, "client-1", "mcp:tools"));
        Assert.NotEqual(Claim("jti"), second.Claims.Single(c => c.Type == "jti").Value);
    }

    [Fact]
    public async Task Restart_keeps_tokens_valid()
    {
        string jwt;
        var (first, firstProtection) = NewIssuer(TimeProvider.System);
        using (firstProtection)
            jwt = await first.IssueAccessTokenAsync(User, "client-1", "mcp:tools");
        SqliteConnection.ClearAllPools();

        var (second, secondProtection) = NewIssuer(TimeProvider.System);
        using var _ = secondProtection;
        var jwks = new JsonWebKeySet(JsonSerializer.Serialize(await second.GetJwksAsync()));

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(jwt, new TokenValidationParameters
        {
            ValidIssuer = Base,
            ValidAudience = Audience,
            ValidTypes = ["at+jwt"],
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeys = jwks.GetSigningKeys(),
        });
        Assert.True(result.IsValid, result.Exception?.Message);
    }

    [Fact]
    public void Pkce_verifies_s256_only()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"; // RFC 7636 appendix B
        const string challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        Assert.True(TokenIssuer.VerifyPkce(verifier, challenge));
        Assert.False(TokenIssuer.VerifyPkce(verifier, verifier)); // "plain" method
        Assert.False(TokenIssuer.VerifyPkce(verifier + "x", challenge));
        Assert.False(TokenIssuer.VerifyPkce("short", challenge));
    }

    [Fact]
    public void Hash_and_secret_helpers_match_data_manager()
    {
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", TokenIssuer.Hash("hello"));
        var a = TokenIssuer.NewSecret();
        Assert.Equal(43, a.Length);
        Assert.NotEqual(a, TokenIssuer.NewSecret());
    }
}
