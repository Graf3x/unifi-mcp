namespace UnifiMcp;

/// <summary>The outcome of owner admission.</summary>
public enum Admission
{
    /// <summary>The identity is the owner.</summary>
    Allowed,
    /// <summary>A Google address that is not on the allowed list.</summary>
    NotListed,
    /// <summary>An identity from the sign-in method that is no longer configured (MCP_SIGNIN changed).</summary>
    OtherSignIn,
}

/// <summary>
/// Owner admission: whether an identity is the owner under today's settings. It is asked again every time an identity
/// is used (a cookie, a code exchange, a refresh), so a change to the list or the sign-in method takes effect at once.
/// The Google callback checks that Google verified the address before an identity exists; this only checks who it is.
/// </summary>
public static class OwnerAdmission
{
    public static Admission Admit(OAuthUser user, Settings settings) => settings.SignIn == SignInMode.Code
        ? IsCodeOwner(user) ? Admission.Allowed : Admission.OtherSignIn
        : user.Email.Trim() is not { Length: > 0 } email ? (IsCodeOwner(user) ? Admission.OtherSignIn : Admission.NotListed)
        : settings.AllowedGoogleEmails().Any(a => string.Equals(a.Trim(), email, StringComparison.OrdinalIgnoreCase)) ? Admission.Allowed
        : Admission.NotListed;

    /// <summary>A code sign-in has the shared subject and no email; a Google identity always has an email.</summary>
    private static bool IsCodeOwner(OAuthUser user) => user.Subject == CodeSignIn.Subject && user.Email.Length == 0;
}
