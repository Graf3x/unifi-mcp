using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UnifiMcp.Tests;

public class SettingsTests
{
    private static string? None(string _) => null;

    [Fact]
    public void Defaults_are_read_only_and_point_at_the_console()
    {
        var s = Settings.FromEnvironment(None);
        Assert.False(s.AllowWrites);
        Assert.Equal(new Uri("https://192.168.1.1"), s.BaseUrl);
        Assert.StartsWith("unix:", s.Listen);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    [InlineData("", false)]
    public void Writes_are_on_only_for_the_literal_true(string value, bool expected) =>
        Assert.Equal(expected, Settings.FromEnvironment(k => k == "UNIFI_ALLOW_WRITES" ? value : null).AllowWrites);

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_confirm_fallback_is_on_only_for_the_literal_true(string? value, bool expected) =>
        Assert.Equal(expected, Settings.FromEnvironment(k => k == "UNIFI_ALLOW_CONFIRM_FALLBACK" ? value : null).AllowConfirmFallback);

    [Fact]
    public void A_plain_http_console_url_is_refused() =>
        Assert.Throws<ArgumentException>(() => Settings.FromEnvironment(k => k == "UNIFI_URL" ? "http://192.168.1.1" : null));

    [Fact]
    public void Missing_names_each_absent_item_without_a_value()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var s = new Settings { ApiKeyFile = Path.Combine(dir, "k"), CertPinFile = Path.Combine(dir, "p") };
        var missing = s.Missing();
        Assert.Equal(2, missing.Count);
        Assert.Contains(missing, m => m.Contains("API key"));
        // Both ways to supply it: the environment variable (named plainly, not as a Fleet key) and the file.
        Assert.Contains(missing, m => m.Contains("set UNIFI_API_KEY") && m.Contains(s.ApiKeyFile));
        Assert.Contains(missing, m => m.Contains("certificate"));
        Assert.Contains(missing, m => m.Contains("set UNIFI_CERT_SHA256") && m.Contains(s.CertPinFile));
        Assert.All(missing, m => Assert.DoesNotContain("Fleet key", m));
    }

    [Fact]
    public void The_pin_from_the_environment_wins_over_the_file()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "p"), new string('a', 64));
        var s = Settings.FromEnvironment(k => k switch
        {
            "UNIFI_CERT_SHA256" => new string('b', 64),
            "UNIFI_CERT_SHA256_FILE" => Path.Combine(dir, "p"),
            _ => null,
        });
        Assert.Equal([new string('B', 64)], s.CertPins());
    }

    [Fact]
    public void A_malformed_pin_counts_as_missing()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "p"), "not-a-fingerprint");
        var s = new Settings { ApiKeyFile = Path.Combine(dir, "k"), CertPinFile = Path.Combine(dir, "p") };
        Assert.Null(s.CertPins());
        Assert.Contains(s.Missing(), m => m.Contains("certificate"));
    }

    [Fact]
    public void Pin_accepts_colons_and_lower_case()
    {
        var colons = string.Join(":", Enumerable.Repeat("ab", 32));
        Assert.Equal(string.Concat(Enumerable.Repeat("AB", 32)), new Settings { CertSha256 = colons }.CertPins()!.Single());
    }

    [Fact]
    public void Several_pins_split_on_commas_and_white_space()
    {
        var a = new string('a', 64);
        var b = string.Join(":", Enumerable.Repeat("bc", 32));
        Assert.Equal([new string('A', 64), string.Concat(Enumerable.Repeat("BC", 32))], new Settings { CertSha256 = $"{a}, {b}\n{a}" }.CertPins());
    }

    [Fact]
    public void One_malformed_pin_in_a_list_makes_the_whole_value_missing()
    {
        var s = new Settings { CertSha256 = $"{new string('a', 64)},typo", CertPinFile = "/nonexistent/p" };
        Assert.Null(s.CertPins());
        Assert.Contains(s.Missing(), m => m.Contains("certificate pin"));
    }

    [Fact]
    public void Ca_trust_needs_no_pin()
    {
        var s = Settings.FromEnvironment(k => k == "UNIFI_CERT_TRUST" ? "CA" : null) with { CertPinFile = "/nonexistent/p" };
        Assert.Equal(CertTrust.Ca, s.Trust);
        Assert.DoesNotContain(s.Missing(), m => m.Contains("certificate"));
        Assert.Equal(CertTrust.Pin, Settings.FromEnvironment(_ => null).Trust);
        Assert.Throws<ArgumentException>(() => Settings.FromEnvironment(k => k == "UNIFI_CERT_TRUST" ? "off" : null));
    }

    [Fact]
    public void The_missing_pin_message_offers_ca_trust()
    {
        var s = new Settings { CertPinFile = "/nonexistent/p" };
        Assert.Contains(s.Missing(), m => m.Contains("UNIFI_CERT_TRUST=ca") && m.Contains("public key"));
    }

    [Fact]
    public void Blank_key_file_counts_as_missing()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "k"), "  \n");
        Assert.Null(new Settings { ApiKeyFile = Path.Combine(dir, "k") }.ApiKey());
    }

    [Fact]
    public void Allowed_logins_come_from_the_file_one_per_line()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "l"), "a@x.test\n\nb@x.test\n");
        Assert.Equal(["a@x.test", "b@x.test"], new Settings { AllowedLoginsFile = Path.Combine(dir, "l") }.AllowedLogins());
    }

    private static string TempFile(string name, string? content)
    {
        var p = Path.Combine(Directory.CreateTempSubdirectory().FullName, name);
        if (content is not null) File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Env_api_key_wins_over_the_file() =>
        Assert.Equal("ENV-KEY", new Settings { ApiKeyFile = TempFile("k", "FILE-KEY"), ApiKeyValue = "ENV-KEY" }.ApiKey());

    [Fact]
    public void Blank_env_api_key_falls_back_to_the_file() =>
        Assert.Equal("FILE-KEY", new Settings { ApiKeyFile = TempFile("k", "FILE-KEY"), ApiKeyValue = "  " }.ApiKey());

    [Theory]
    [InlineData("http://x.example")]
    [InlineData("https://x.example/path")]
    [InlineData("https://x.example?q=1")]
    public void Public_base_url_must_be_an_https_origin(string url)
    {
        var e = Assert.Throws<ArgumentException>(() => Settings.FromEnvironment(k => k == "MCP_PUBLIC_BASE_URL" ? url : null));
        Assert.Equal("MCP_PUBLIC_BASE_URL must be an https:// origin.", e.Message);
    }

    [Fact]
    public void Public_base_url_accepts_an_https_origin() =>
        Assert.Equal(new Uri("https://unifi.example.com"), Settings.FromEnvironment(k => k == "MCP_PUBLIC_BASE_URL" ? "https://unifi.example.com" : null).PublicBaseUrl);

    [Fact]
    public void Public_missing_names_every_absent_value()
    {
        var file = TempFile("allowed-google-emails", null);
        var s = new Settings { AllowedGoogleEmailsFile = file };
        Assert.Equal(
            ["MCP_PUBLIC_BASE_URL", "GOOGLE_CLIENT_ID", "GOOGLE_CLIENT_SECRET", "UNIFI_MCP_DATA_KEY", $"allowed Google emails (MCP_ALLOWED_GOOGLE_EMAILS or {file})"],
            s.PublicMissing());
    }

    [Fact]
    public void Short_data_key_counts_as_missing() =>
        Assert.Contains("UNIFI_MCP_DATA_KEY", new Settings { DataKey = "short" }.PublicMissing());

    [Fact]
    public void Allowed_google_emails_parse_like_allowed_logins()
    {
        var s = new Settings { AllowedGoogleEmailsFile = TempFile("g", "a@x.test\n\nb@x.test,c@x.test\r\n") };
        Assert.Equal(["a@x.test", "b@x.test", "c@x.test"], s.AllowedGoogleEmails());
    }

    [Fact]
    public void Sign_in_defaults_to_google() =>
        Assert.Equal(SignInMode.Google, Settings.FromEnvironment(_ => null).SignIn);

    [Theory]
    [InlineData("google", SignInMode.Google)]
    [InlineData("code", SignInMode.Code)]
    [InlineData(" Code ", SignInMode.Code)]
    public void Sign_in_mode_is_read(string value, SignInMode expected) =>
        Assert.Equal(expected, Settings.FromEnvironment(k => k == "MCP_SIGNIN" ? value : null).SignIn);

    [Fact]
    public void An_unknown_sign_in_mode_is_refused()
    {
        var e = Assert.Throws<ArgumentException>(() => Settings.FromEnvironment(k => k == "MCP_SIGNIN" ? "password" : null));
        Assert.Equal("MCP_SIGNIN must be google or code.", e.Message);
    }

    [Fact]
    public void Code_sign_in_needs_no_google_values()
    {
        var s = new Settings { SignIn = SignInMode.Code, AllowedGoogleEmailsFile = TempFile("allowed-google-emails", null) };
        Assert.Equal(["MCP_PUBLIC_BASE_URL", "UNIFI_MCP_DATA_KEY"], s.PublicMissing());
    }

    [Fact]
    public void Access_token_is_read_from_the_environment()
    {
        var token = new string('t', 32);
        Assert.Equal(token, Settings.FromEnvironment(k => k == "MCP_ACCESS_TOKEN" ? token : null).AccessToken);
        Assert.Null(Settings.FromEnvironment(_ => null).AccessToken);
    }

    [Fact]
    public void A_short_access_token_is_refused()
    {
        var e = Assert.Throws<ArgumentException>(() => Settings.FromEnvironment(k => k == "MCP_ACCESS_TOKEN" ? "short" : null));
        Assert.Equal("MCP_ACCESS_TOKEN must be at least 32 characters.", e.Message);
    }

    private static Settings WithSecrets() => new()
    {
        ApiKeyValue = "SENTINEL-API-KEY",
        GoogleClientSecret = "SENTINEL-GOOGLE-SECRET",
        DataKey = "SENTINEL-DATA-KEY-0123456789",
        AccessToken = "SENTINEL-ACCESS-TOKEN-0123456789abcdef",
    };

    [Fact]
    public void Settings_tostring_hides_secret_values()
    {
        var text = WithSecrets().ToString();
        Assert.DoesNotContain("SENTINEL", text);
        Assert.Contains("***", text);
    }

    [Fact]
    public void Startup_summary_never_contains_connector_secrets()
    {
        var summary = Harness.Summary(WithSecrets(), 5);
        Assert.DoesNotContain("SENTINEL", summary);
        Assert.Contains("connector not configured", summary);
    }
}

/// <summary><see cref="ConsoleTransport.Judge"/>: the one rule for trusting the console's certificate, at the handshake and on the setup page.</summary>
public class TrustTests
{
    private const string A = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string B = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string C = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";

    /// <summary>Pins from the environment only; the default pin file does not exist where tests run.</summary>
    private static Settings Pins(string? pins, CertTrust trust = CertTrust.Pin) =>
        new() { CertSha256 = pins, CertPinFile = Path.Combine(Path.GetTempPath(), "no-such-pin-" + Guid.NewGuid()), Trust = trust };

    private static CertificateVerdict Judge(Settings s, string certificate, string key, SslPolicyErrors errors = SslPolicyErrors.RemoteCertificateChainErrors) =>
        ConsoleTransport.Judge(s, certificate, key, errors);

    [Fact]
    public void A_pinned_certificate_is_trusted_despite_the_chain_errors_of_a_self_signed_one() =>
        Assert.Equal(CertificateVerdict.CertificatePin, Judge(Pins(A), certificate: A, key: B));

    [Fact]
    public void A_pinned_public_key_is_trusted() =>
        Assert.Equal(CertificateVerdict.KeyPin, Judge(Pins(B), certificate: A, key: B));

    [Fact]
    public void With_both_pinned_the_key_pin_is_the_one_reported() =>
        Assert.Equal(CertificateVerdict.KeyPin, Judge(Pins($"{A},{B}"), certificate: A, key: B));

    [Fact]
    public void Any_of_several_pins_will_do_in_any_case() =>
        Assert.Equal(CertificateVerdict.CertificatePin, Judge(Pins($"{C}, {A.ToLowerInvariant()}"), certificate: A, key: B));

    [Theory]
    [InlineData(C)]          // another certificate's
    [InlineData(null)]       // no pin: nothing matches, so checking cannot be off
    [InlineData(A + ",oops")] // a typo drops no pin quietly: the whole list is refused
    public void Anything_else_is_refused(string? pins) =>
        Assert.Equal(CertificateVerdict.Refused, Judge(Pins(pins), certificate: A, key: B));

    [Fact]
    public void Ca_mode_trusts_a_certificate_the_platform_has_no_complaint_about() =>
        Assert.Equal(CertificateVerdict.Ca, Judge(Pins(null, CertTrust.Ca), A, B, SslPolicyErrors.None));

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    public void Ca_mode_refuses_any_complaint_and_reads_no_pins(SslPolicyErrors errors) =>
        Assert.Equal(CertificateVerdict.Refused, Judge(Pins(A, CertTrust.Ca), certificate: A, key: B, errors));

    [Fact]
    public void The_pins_are_the_sha256_of_the_certificate_and_of_its_subject_public_key_info()
    {
        // The key pin is what `openssl x509 -pubkey -noout | openssl pkey -pubin -outform der | openssl dgst -sha256` prints.
        using var rsa = RSA.Create(2048);
        using var cert = new CertificateRequest("CN=udm", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(cert.RawData)), ConsoleTransport.CertificateSha256(cert));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo())), ConsoleTransport.KeySha256(cert));
        // The handshake may hand over a plain X509Certificate, which takes the other path to the key.
#pragma warning disable SYSLIB0057 // the obsolete constructor is the way to get a plain X509Certificate
        using var plain = new X509Certificate(cert.RawData);
#pragma warning restore SYSLIB0057
        Assert.Equal(ConsoleTransport.KeySha256(cert), ConsoleTransport.KeySha256(plain));
    }
}
