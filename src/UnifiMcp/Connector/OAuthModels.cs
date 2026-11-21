namespace UnifiMcp;

// Everything secret is stored as a SHA-256 hash (computed by the caller): a leaked database yields nothing a
// client could present. These are plain classes so the default ToString() prints only the type name.

/// <summary>
/// A client registered through RFC 7591 dynamic registration. Public client (PKCE, no secret): registering one
/// grants nothing by itself; the owner still has to log in and consent before any token is issued.
/// </summary>
public sealed class OAuthClient
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public List<string> RedirectUris { get; set; } = [];
    public string? ClientUri { get; set; }
    public string? LogoUri { get; set; }
    public string? SoftwareId { get; set; }
    public string? SoftwareVersion { get; set; }
    /// <summary>Who registered it, as far as we can tell: remote address and user agent. Never trusted, only logged.</summary>
    public string? RegisteredFrom { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public bool Disabled { get; set; }
}

/// <summary>Single-use authorization code, keyed by the SHA-256 of the code the client receives.</summary>
public sealed class AuthorizationCode
{
    public string CodeHash { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string CodeChallenge { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string? Resource { get; set; }
    public OAuthUser User { get; set; } = new();
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Redeemed { get; set; }
}

/// <summary>
/// Rotating refresh token, keyed by the SHA-256 of the token the client holds. Redeeming one issues a new one and
/// marks this one redeemed; a redeemed token presented again means it leaked, and the whole family is revoked.
/// </summary>
public sealed class RefreshToken
{
    public string TokenHash { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    /// <summary>Stable across rotations; ties every token descended from one consent together.</summary>
    public string FamilyId { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public OAuthUser User { get; set; } = new();
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Redeemed { get; set; }
    public bool Revoked { get; set; }
}

/// <summary>A user's standing approval of a client; revoking the consent revokes its refresh-token family.</summary>
public sealed class Consent
{
    public string UserId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    /// <summary>For a client identified by a metadata document: the document URL (the actual client_id).</summary>
    public string? ClientUri { get; set; }
    public string Scope { get; set; } = string.Empty;
    public string FamilyId { get; set; } = string.Empty;
    /// <summary>When the owner approved the client (a fresh consent after a disconnect starts over).</summary>
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
}

/// <summary>The token-signing key; the private half arrives already wrapped (protected) by the caller.</summary>
public sealed class SigningKey
{
    public string KeyId { get; set; } = string.Empty;
    public string ProtectedPrivateKey { get; set; } = string.Empty;
}

/// <summary>The identity captured at consent and copied into every token issued from it.</summary>
public sealed class OAuthUser
{
    public string Subject { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>What pages and logs show: the email, or the name when there is none (a code sign-in).</summary>
    public string Label => Email.Length > 0 ? Email : Name;

    /// <summary>The identity a sign-in cookie carries: null when it has no subject.</summary>
    public static OAuthUser? From(System.Security.Claims.ClaimsPrincipal principal)
    {
        var sub = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(sub)) return null;
        var email = principal.FindFirst("email")?.Value ?? "";
        var name = principal.FindFirst("name")?.Value ?? email;
        return new OAuthUser { Subject = sub, Email = email, Name = name };
    }
}
