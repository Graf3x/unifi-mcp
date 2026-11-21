using System.Security.Claims;

namespace UnifiMcp.Tests;

/// <summary>Owner admission, straight through the module: an identity and today's settings in, the decision out.</summary>
public sealed class OwnerAdmissionTests
{
    private const string Owner = "owner@example.test";

    private static readonly Settings Google = new() { SignIn = SignInMode.Google, AllowedGoogleEmailsValue = Owner };
    private static readonly Settings Code = new() { SignIn = SignInMode.Code };

    private static OAuthUser GoogleUser(string email) => new() { Subject = "google-sub-1", Email = email, Name = "Owner" };
    private static readonly OAuthUser CodeOwner = new() { Subject = CodeSignIn.Subject, Name = CodeSignIn.Label };

    [Fact]
    public void A_listed_google_address_is_the_owner() =>
        Assert.Equal(Admission.Allowed, OwnerAdmission.Admit(GoogleUser(Owner), Google));

    [Fact]
    public void An_unlisted_google_address_is_not_listed() =>
        Assert.Equal(Admission.NotListed, OwnerAdmission.Admit(GoogleUser("someone@else.test"), Google));

    [Fact]
    public void Case_and_spaces_are_ignored_on_both_sides()
    {
        var spaced = Google with { AllowedGoogleEmailsValue = "  OWNER@Example.Test , other@example.test" };
        Assert.Equal(Admission.Allowed, OwnerAdmission.Admit(GoogleUser(" Owner@EXAMPLE.test "), spaced));
    }

    [Fact]
    public void An_empty_list_admits_no_one() =>
        Assert.Equal(Admission.NotListed, OwnerAdmission.Admit(GoogleUser(Owner), Google with { AllowedGoogleEmailsValue = "" }));

    [Fact]
    public void The_code_owner_is_the_owner_in_code_mode() =>
        Assert.Equal(Admission.Allowed, OwnerAdmission.Admit(CodeOwner, Code));

    [Fact]
    public void An_owner_subject_with_an_email_is_not_the_code_owner() =>
        Assert.Equal(Admission.OtherSignIn, OwnerAdmission.Admit(new OAuthUser { Subject = CodeSignIn.Subject, Email = Owner }, Code));

    [Fact]
    public void A_google_identity_is_refused_in_code_mode() =>
        Assert.Equal(Admission.OtherSignIn, OwnerAdmission.Admit(GoogleUser(Owner), Code));

    [Fact]
    public void The_code_owner_is_refused_in_google_mode() =>
        Assert.Equal(Admission.OtherSignIn, OwnerAdmission.Admit(CodeOwner, Google));

    [Fact]
    public void A_principal_becomes_the_identity_the_ledger_stores()
    {
        var p = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "google-sub-1"), new Claim("email", Owner), new Claim("name", "Owner")], "test"));
        var user = OAuthUser.From(p);

        Assert.NotNull(user);
        Assert.Equal(("google-sub-1", Owner, "Owner"), (user.Subject, user.Email, user.Name));
    }

    [Fact]
    public void A_principal_without_a_subject_is_no_identity() =>
        Assert.Null(OAuthUser.From(new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", Owner)], "test"))));

    [Fact]
    public void A_code_cookie_principal_is_the_code_owner()
    {
        var p = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", CodeSignIn.Subject), new Claim("name", CodeSignIn.Label), new Claim("signin", "code")], "test"));
        Assert.Equal(Admission.Allowed, OwnerAdmission.Admit(OAuthUser.From(p)!, Code));
    }
}
