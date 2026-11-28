using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace UnifiMcp.Tests;

/// <summary>
/// A misspelt setting is otherwise silently ignored (MCP_SETUP_PAGES leaves the setup page on), so unknown UNIFI_* and
/// MCP_* names are warned about at startup, by name only.
/// </summary>
public sealed partial class EnvironmentNamesTests
{
    // Linux (the image) compares environment names case-sensitively, so these tests do too unless they say otherwise.
    private static IReadOnlyList<string> Warnings(params string[] names) => Settings.UnknownNameWarnings(names, StringComparer.Ordinal);

    [Theory]
    [InlineData("MCP_SETUP_PAGES", "MCP_SETUP_PAGE")]
    [InlineData("UNIFI_STE", "UNIFI_SITE")]
    [InlineData("UNIFI_ALLOW_WRITE", "UNIFI_ALLOW_WRITES")]
    [InlineData("MCP_LAN_ACESS_TOKEN", "MCP_LAN_ACCESS_TOKEN")]
    [InlineData("mcp_setup_pages", "MCP_SETUP_PAGE")] // a lower-case name is never read on Linux, so it is checked too
    [InlineData("Unifi_Ste", "UNIFI_SITE")]
    public void A_typo_warns_with_the_closest_setting(string typo, string meant)
    {
        var warning = Assert.Single(Warnings(typo));
        Assert.Contains(typo, warning);
        Assert.Contains($"Did you mean {meant}?", warning);
    }

    [Fact]
    public void A_name_far_from_every_setting_warns_without_a_guess()
    {
        var warning = Assert.Single(Warnings("UNIFI_SOMETHING_ELSE"));
        Assert.Contains("UNIFI_SOMETHING_ELSE", warning);
        Assert.DoesNotContain("Did you mean", warning);
        Assert.Contains(".env.example", warning);
    }

    [Fact]
    public void Known_names_do_not_warn() =>
        Assert.Empty(Warnings([.. Settings.EnvironmentNames, .. Settings.OtherContainerNames]));

    [Fact]
    public void A_known_name_in_other_case_warns_where_names_are_case_sensitive() =>
        // On Linux (the image) mcp_setup_page=off is never read and leaves the setup page on.
        Assert.Contains("Did you mean MCP_SETUP_PAGE?", Assert.Single(Settings.UnknownNameWarnings(["mcp_setup_page"], StringComparer.Ordinal)));

    [Fact]
    public void A_known_name_in_other_case_does_not_warn_where_names_ignore_case() =>
        // Windows reads mcp_setup_page as MCP_SETUP_PAGE.
        Assert.Empty(Settings.UnknownNameWarnings(["mcp_setup_page"], StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void The_new_write_tools_setting_is_known() => Assert.Contains("UNIFI_WRITE_TOOLS", Settings.EnvironmentNames);

    [Theory]
    [InlineData("PATH")]
    [InlineData("TUNNEL_TOKEN")]
    [InlineData("PUID")]
    [InlineData("DOTNET_RUNNING_IN_CONTAINER")]
    [InlineData("ASPNETCORE_URLS")]
    [InlineData("GOOGLE_APPLICATION_CREDENTIALS")]
    [InlineData("XMCP_SETUP_PAGES")]
    public void Other_prefixes_do_not_warn(string name) => Assert.Empty(Warnings(name));

    [Fact]
    public void Each_unknown_name_warns_once() =>
        Assert.Single(Warnings("MCP_SETUP_PAGES", "MCP_SETUP_PAGES", "UNIFI_URL"));

    [Fact]
    public async Task The_host_logs_each_unknown_name_at_warning_and_never_a_value()
    {
        const string secret = "s3cret-value-never-logged";
        var environment = new Dictionary<string, string>
        {
            ["MCP_SETUP_PAGES"] = secret,
            ["UNIFI_STE"] = secret,
            ["UNIFI_URL"] = secret,
            ["PATH"] = secret,
        };
        var logs = new LogCapture();
        var dir = Directory.CreateTempSubdirectory("unifi-mcp-env").FullName;
        await using (var app = McpHost.Build(Harness.NewSettings(dir), new FakeUnifi(),
            b => { b.WebHost.UseTestServer(); b.Logging.AddProvider(logs); },
            unknownNameWarnings: Settings.UnknownNameWarnings(environment.Keys, StringComparer.Ordinal)))
        {
            await app.StartAsync();
            await app.StopAsync();
        }
        Directory.Delete(dir, recursive: true);
        var warnings = logs.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Line).ToList();
        Assert.Single(warnings, w => w.Contains("MCP_SETUP_PAGES") && w.Contains("Did you mean MCP_SETUP_PAGE?"));
        Assert.Single(warnings, w => w.Contains("UNIFI_STE") && w.Contains("Did you mean UNIFI_SITE?"));
        Assert.Equal(2, warnings.Count(w => w.Contains("is not a setting")));
        Assert.DoesNotContain(logs.Lines, l => l.Contains(secret));
    }

    [Fact]
    public async Task A_typo_that_stops_startup_is_still_named_next_to_the_error()
    {
        // The misspelt token is why MCP_LAN_LISTEN refuses to start: the suggestion matters most exactly here.
        const string secret = "0123456789abcdef0123456789abcdef-never-printed";
        var environment = new Dictionary<string, string>
        {
            ["MCP_LAN_LISTEN"] = "http://0.0.0.0:7420",
            ["MCP_LAN_ACESS_TOKEN"] = secret,
        };
        var stderr = new StringWriter();
        await Assert.ThrowsAsync<ArgumentException>(() => McpHost.RunAsync(environment, stderr));
        Assert.Contains("MCP_LAN_ACESS_TOKEN", stderr.ToString());
        Assert.Contains("Did you mean MCP_LAN_ACCESS_TOKEN?", stderr.ToString());
        Assert.DoesNotContain(secret, stderr.ToString());
    }

    [Fact]
    public async Task A_bad_write_tool_name_that_stops_startup_still_shows_the_typo_warnings()
    {
        var environment = new Dictionary<string, string> { ["UNIFI_WRITE_TOOLS"] = "no_such_tool", ["UNIFI_ALLOW_WRITE"] = "true" };
        var stderr = new StringWriter();
        await Assert.ThrowsAsync<ArgumentException>(() => McpHost.RunAsync(environment, stderr));
        Assert.Contains("Did you mean UNIFI_ALLOW_WRITES?", stderr.ToString());
    }

    [Fact]
    public async Task A_startup_error_with_no_unknown_names_prints_nothing_extra()
    {
        var environment = new Dictionary<string, string> { ["MCP_LAN_LISTEN"] = "http://0.0.0.0:7420" };
        var stderr = new StringWriter();
        await Assert.ThrowsAsync<ArgumentException>(() => McpHost.RunAsync(environment, stderr));
        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public async Task Without_warnings_the_host_logs_none()
    {
        var logs = new LogCapture();
        var dir = Directory.CreateTempSubdirectory("unifi-mcp-env").FullName;
        await using (var app = McpHost.Build(Harness.NewSettings(dir), new FakeUnifi(), b => { b.WebHost.UseTestServer(); b.Logging.AddProvider(logs); }))
        {
            await app.StartAsync();
            await app.StopAsync();
        }
        Directory.Delete(dir, recursive: true);
        Assert.DoesNotContain(logs.Lines, l => l.Contains("is not a setting"));
    }

    [GeneratedRegex(@"\b(?:UNIFI|MCP)_[A-Z0-9_]*[A-Z0-9]\b")]
    private static partial Regex PrefixedName();

    /// <summary>
    /// The known names are the ones FromEnvironment asks for with nothing set. That is all of them only if every read is
    /// made whatever the values, so a full environment, with every listener and option on, must ask for exactly the same.
    /// </summary>
    [Fact]
    public void A_full_environment_reads_exactly_the_known_names()
    {
        var full = new Dictionary<string, string>
        {
            ["UNIFI_URL"] = "https://192.0.2.1", ["UNIFI_SITE"] = "home", ["UNIFI_API_KEY"] = "key", ["UNIFI_API_KEY_FILE"] = "/k",
            ["UNIFI_CERT_SHA256"] = new string('A', 64), ["UNIFI_CERT_SHA256_FILE"] = "/c", ["UNIFI_CERT_TRUST"] = "pin",
            ["UNIFI_ALLOW_WRITES"] = "true", ["UNIFI_WRITE_TOOLS"] = "create_dns_policy", ["UNIFI_REVEAL_SECRETS"] = "voucher-codes",
            ["UNIFI_ALLOW_CONFIRM_FALLBACK"] = "true", ["UNIFI_MCP_DATA_KEY"] = new string('d', 32),
            ["MCP_PUBLIC_BASE_URL"] = "https://unifi.example.com", ["MCP_SIGNIN"] = "code", ["MCP_ACCESS_TOKEN"] = new string('a', 32),
            ["MCP_ALLOWED_LOGINS"] = "owner@example.com", ["MCP_ALLOWED_LOGINS_FILE"] = "/l",
            ["MCP_ALLOWED_GOOGLE_EMAILS"] = "owner@example.com", ["MCP_ALLOWED_GOOGLE_EMAILS_FILE"] = "/g",
            ["MCP_LISTEN"] = "unix:/run/a/mcp.sock", ["MCP_PUBLIC_LISTEN"] = "unix:/run/b/public.sock", ["MCP_ALLOW_LOOPBACK_TCP"] = "true",
            ["MCP_DATA_DIR"] = "/d", ["MCP_SETUP_PAGE"] = "on", ["MCP_SETUP_LISTEN"] = "http://127.0.0.1:5198",
            ["MCP_LAN_LISTEN"] = "http://0.0.0.0:7420", ["MCP_LAN_ACCESS_TOKEN"] = new string('b', 32),
            ["GOOGLE_CLIENT_ID"] = "id", ["GOOGLE_CLIENT_SECRET"] = "secret",
        };
        var read = new HashSet<string>(StringComparer.Ordinal);
        var settings = Settings.FromEnvironment(n => { read.Add(n); return full.GetValueOrDefault(n); });
        Assert.NotNull(settings.LanListen); // it parsed with every option on
        Assert.Equal(Settings.EnvironmentNames.Order(StringComparer.Ordinal), read.Order(StringComparer.Ordinal));
        Assert.Equal(full.Keys.Order(StringComparer.Ordinal), read.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_known_names_include_the_settings_the_warnings_matter_for()
    {
        Assert.True(Settings.EnvironmentNames.Count > 20, $"only {Settings.EnvironmentNames.Count} names; has the way settings are read changed?");
        Assert.Contains("MCP_SETUP_PAGE", Settings.EnvironmentNames);
        Assert.Contains("MCP_LAN_ACCESS_TOKEN", Settings.EnvironmentNames);
    }

    /// <summary>The warning sends the owner to the README's Settings reference, so every setting must be there.</summary>
    [Fact]
    public void Every_known_name_is_in_the_readme_settings_reference()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md")).ReplaceLineEndings("\n");
        var start = readme.IndexOf("\n## Settings reference\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "README.md has no '## Settings reference' section");
        var end = readme.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var section = end < 0 ? readme[start..] : readme[start..end];
        Assert.Empty(Settings.EnvironmentNames.Where(n => !Regex.IsMatch(section, $@"\b{n}\b")));
    }

    [Theory]
    [InlineData(".env.example")]
    [InlineData("docker-compose.yml")]
    [InlineData("docker-compose.lan.yml")]
    [InlineData("deploy/compose.yaml")]
    public void Every_name_the_examples_and_compose_files_use_is_known_or_excluded(string file)
    {
        var names = PrefixedName().Matches(File.ReadAllText(Path.Combine(RepoRoot(), file))).Select(m => m.Value).Distinct().ToList();
        Assert.NotEmpty(names);
        Assert.DoesNotContain(names, n => !Settings.EnvironmentNames.Contains(n) && !Settings.OtherContainerNames.Contains(n));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "unifi-mcp.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
