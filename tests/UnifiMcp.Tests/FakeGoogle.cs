using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace UnifiMcp.Tests;

/// <summary>
/// Stands in for accounts.google.com as the OIDC backchannel sees it: discovery, JWKS and the token endpoint. The
/// browser half (the consent screen) is <see cref="Authorize"/>: the test hands it the authorize URL the server
/// redirected to and gets back the code Google would put on the callback.
/// </summary>
public sealed class FakeGoogle : HttpMessageHandler
{
    public const string Authority = "https://google.test";
    public const string AuthorizeEndpoint = Authority + "/o/oauth2/v2/auth";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;
    private readonly Dictionary<string, Grant> _codes = new(StringComparer.Ordinal);
    private (string Email, bool Verified, bool VerifiedAsString) _next = ("", false, false);

    private sealed record Grant(string Email, bool Verified, bool VerifiedAsString, string Nonce, string Challenge, string RedirectUri, string ClientId);

    public FakeGoogle() => _key = new RsaSecurityKey(_rsa) { KeyId = "fake-google-1" };

    public string ClientId { get; init; } = "test-google-client-id";
    public string ClientSecret { get; init; } = "test-google-client-secret";

    /// <summary>Every id_token and code this fake handed out, so tests can check none of them leaks.</summary>
    public List<string> Issued { get; } = [];

    /// <summary>The identity the next <see cref="Authorize"/> approves. Google sends email_verified as a JSON boolean.</summary>
    public void Approve(string email, bool verified, bool verifiedAsString = false) => _next = (email, verified, verifiedAsString);

    /// <summary>The consent screen: checks the request, remembers its nonce and PKCE challenge, returns the code.</summary>
    public string Authorize(Uri authorizeUrl)
    {
        var q = QueryHelpers.ParseQuery(authorizeUrl.Query);
        if (q["client_id"] != ClientId) throw new InvalidOperationException("wrong client_id");
        if (q["code_challenge_method"] != "S256") throw new InvalidOperationException("no S256 PKCE");
        var code = "fake-code-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _codes[code] = new(_next.Email, _next.Verified, _next.VerifiedAsString, q["nonce"].ToString(), q["code_challenge"].ToString(),
            q["redirect_uri"].ToString(), q["client_id"].ToString());
        Issued.Add(code);
        return code;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var uri = req.RequestUri!;
        if (!string.Equals(uri.GetLeftPart(UriPartial.Authority), Authority, StringComparison.OrdinalIgnoreCase))
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        return uri.AbsolutePath switch
        {
            "/.well-known/openid-configuration" => Json(new Dictionary<string, object>
            {
                ["issuer"] = Authority,
                ["authorization_endpoint"] = AuthorizeEndpoint,
                ["token_endpoint"] = Authority + "/token",
                ["jwks_uri"] = Authority + "/jwks",
                ["response_types_supported"] = new[] { "code" },
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                ["scopes_supported"] = new[] { "openid", "email", "profile" },
                ["code_challenge_methods_supported"] = new[] { "plain", "S256" },
            }),
            "/jwks" => Json(new { keys = new[] { Jwk() } }),
            "/token" when req.Method == HttpMethod.Post => await TokenAsync(req, ct),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private async Task<HttpResponseMessage> TokenAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var form = QueryHelpers.ParseQuery("?" + await req.Content!.ReadAsStringAsync(ct));
        if (form["grant_type"] != "authorization_code" || !_codes.Remove(form["code"].ToString(), out var grant))
            return Error("invalid_grant");
        if (form["client_id"] != grant.ClientId || form["client_secret"] != ClientSecret) return Error("invalid_client");
        if (form["redirect_uri"] != grant.RedirectUri) return Error("redirect_uri_mismatch");
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString())));
        if (challenge != grant.Challenge) return Error("invalid_grant");

        var accessToken = "fake-access-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var now = DateTime.UtcNow; // the OIDC handler checks id_token lifetimes against the real clock
        var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Authority,
            Audience = grant.ClientId,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddHours(1),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "google-sub-" + grant.Email.ToLowerInvariant(),
                ["email"] = grant.Email,
                ["email_verified"] = grant.VerifiedAsString ? (grant.Verified ? "true" : "false") : grant.Verified,
                ["name"] = "Test Owner",
                ["nonce"] = grant.Nonce,
                ["at_hash"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken))[..16]),
            },
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });
        Issued.Add(idToken);
        return Json(new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["id_token"] = idToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = 3599,
            ["scope"] = "openid email profile",
        });
    }

    private object Jwk()
    {
        var p = _rsa.ExportParameters(false);
        return new { kty = "RSA", use = "sig", alg = "RS256", kid = _key.KeyId, n = Base64UrlEncoder.Encode(p.Modulus), e = Base64UrlEncoder.Encode(p.Exponent) };
    }

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(string error) =>
        new(HttpStatusCode.BadRequest) { Content = new StringContent($$"""{"error":"{{error}}"}""", Encoding.UTF8, "application/json") };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _rsa.Dispose();
        base.Dispose(disposing);
    }
}
