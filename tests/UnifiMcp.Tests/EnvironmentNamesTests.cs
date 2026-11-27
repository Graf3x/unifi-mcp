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
    [Theory]
    [InlineData("MCP_SETUP_PAGES", "MCP_SETUP_PAGE")]
    [InlineData("UNIFI_STE", "UNIFI_SITE")]
    [InlineData("UNIFI_ALLOW_WRITE", "UNIFI_ALLOW_WRITES")]
    [InlineData("MCP_LAN_ACESS_TOKEN", "MCP_LAN_ACCESS_TOKEN")]
    [InlineData("mcp_setup_pages", "MCP_SETUP_PAGE")] // a lower-case name is never read on Linux, so it is checked too
    [InlineData("Unifi_Ste", "UNIFI_SITE")]
    public void A_typo_warns_with_the_closest_setting(string typo, string meant)
    {
        var warning = Assert.Single(Settings.UnknownNameWarnings([typo]));
        Assert.Contains(typo, warning);
        Assert.Contains($"Did you mean {meant}?", warning);
    }

    [Fact]
    public void A_name_far_from_every_setting_warns_without_a_guess()
    {
        var warning = Assert.Single(Settings.UnknownNameWarnings(["UNIFI_SOMETHING_ELSE"]));
        Assert.Contains("UNIFI_SOMETHING_ELSE", warning);
        Assert.DoesNotContain("Did you mean", warning);
        Assert.Contains(".env.example", warning);
    }

    [Fact]
    public void Known_names_do_not_warn() =>
        Assert.Empty(Settings.UnknownNameWarnings([.. Settings.EnvironmentNames, .. Settings.OtherContainerNames]));

    [Fact]
    public void A_known_name_in_other_case_warns_only_where_it_is_not_read()
    {
        // Linux (the image) reads environment names case-sensitively: mcp_setup_page=off leaves the setup page on.
        var warnings = Settings.UnknownNameWarnings(["mcp_setup_page"]);
        if (OperatingSystem.IsWindows()) Assert.Empty(warnings);
        else Assert.Contains("Did you mean MCP_SETUP_PAGE?", Assert.Single(warnings));
    }

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
    public void Other_prefixes_do_not_warn(string name) => Assert.Empty(Settings.UnknownNameWarnings([name]));

    [Fact]
    public void Each_unknown_name_warns_once() =>
        Assert.Single(Settings.UnknownNameWarnings(["MCP_SETUP_PAGES", "MCP_SETUP_PAGES", "UNIFI_URL"]));

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
            b => { b.WebHost.UseTestServer(); b.Logging.AddProvider(logs); }, environmentNames: environment.Keys))
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
    public async Task Without_environment_names_the_host_checks_nothing()
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

    // env("NAME") in Settings.FromEnvironment, Environment.GetEnvironmentVariable("NAME") anywhere else.
    [GeneratedRegex(@"(?:\benv|\bGetEnvironmentVariable)\(""([A-Za-z0-9_]+)""\)")]
    private static partial Regex EnvRead();

    [GeneratedRegex(@"\b(?:UNIFI|MCP)_[A-Z0-9_]*[A-Z0-9]\b")]
    private static partial Regex PrefixedName();

    /// <summary>
    /// Every name the server reads is in the list, so a new setting cannot be added without it, and every name in the
    /// list is read, so a removed one does not linger. DOTNET_* names belong to the runtime and image, not to the owner.
    /// </summary>
    [Fact]
    public void The_known_names_are_exactly_the_names_the_code_reads()
    {
        var src = Path.Combine(RepoRoot(), "src", "UnifiMcp");
        var read = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => EnvRead().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Where(n => !n.StartsWith("DOTNET_", StringComparison.Ordinal))
            .ToHashSet();
        Assert.True(read.Count > 20, $"the scan found only {read.Count} names; has the way settings are read changed?");
        Assert.Empty(read.Except(Settings.EnvironmentNames));
        Assert.Empty(Settings.EnvironmentNames.Except(read));
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
