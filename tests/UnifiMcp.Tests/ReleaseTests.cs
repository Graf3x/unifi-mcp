using System.Diagnostics;
using System.Text.RegularExpressions;

namespace UnifiMcp.Tests;

/// <summary>
/// The release around the code: which runtime the images run, the gate an image passes before it is published, and the
/// operator docs for upgrades and for sharing one server. These read the repository's files; nothing here builds an image.
/// </summary>
public sealed partial class ReleaseTests
{
    private static readonly string Root = RepoRoot();
    private static readonly string[] Dockerfiles = ["Dockerfile", Path.Combine("deploy", "Dockerfile")];

    // ---- The runtime -------------------------------------------------------------------------------------------------

    [Fact]
    public void Both_dockerfiles_pin_the_same_exact_runtime_and_sdk()
    {
        var runtimes = Dockerfiles.Select(f => Pin(f, "aspnet")).ToList();
        var sdks = Dockerfiles.Select(f => Pin(f, "sdk")).ToList();
        Assert.All(runtimes.Concat(sdks), v => Assert.Matches(@"^\d+\.\d+\.\d+$", v));
        Assert.Single(runtimes.Distinct());
        Assert.Single(sdks.Distinct());
    }

    [Fact]
    public void The_image_runtime_is_not_older_than_the_aspnet_packages_the_server_is_built_and_tested_with()
    {
        var csproj = File.ReadAllText(Path.Combine(Root, "src", "UnifiMcp", "UnifiMcp.csproj"));
        var packages = PackageVersion().Matches(csproj).Select(m => Version.Parse(m.Groups[1].Value)).ToList();
        Assert.NotEmpty(packages);
        var newest = packages.Max()!;
        foreach (var file in Dockerfiles)
            Assert.True(Version.Parse(Pin(file, "aspnet")) >= newest,
                $"{file} runs ASP.NET Core {Pin(file, "aspnet")}, older than the {newest} packages in UnifiMcp.csproj.");
    }

    // ---- The image gate ----------------------------------------------------------------------------------------------

    [Fact]
    public void Every_image_check_starts_the_image_through_the_gate_on_amd64_and_arm64()
    {
        var check = Jobs()["image-check"];
        Assert.Contains("tools/image-gate.sh", check);
        Assert.Contains("linux/amd64", check);
        Assert.Contains("linux/arm64", check);
        Assert.Contains("ubuntu-24.04-arm", check); // arm64 runs natively, not under emulation
        Assert.Contains("load: true", check);
    }

    [Fact]
    public void A_release_is_tagged_only_after_the_gate_passed_on_the_pushed_digest()
    {
        var jobs = Jobs();
        // The build pushes by digest only: no tag a user could pull exists until the gate passes.
        var build = jobs.Single(j => j.Value.Contains("docker/build-push-action") && j.Value.Contains("push-by-digest=true"));
        Assert.DoesNotMatch(@"(?m)^\s+tags:", build.Value);
        Assert.Contains("digest", build.Value);

        var gate = jobs.Single(j => j.Key != "image-check" && j.Value.Contains("tools/image-gate.sh"));
        Assert.Contains(build.Key, Needs(gate.Value));
        Assert.Contains("@${{ needs." + build.Key + ".outputs.digest }}", gate.Value);
        Assert.Contains("linux/arm64", gate.Value);
        Assert.Contains("ubuntu-24.04-arm", gate.Value);

        var promote = jobs.Single(j => j.Value.Contains("imagetools create"));
        Assert.Contains(gate.Key, Needs(promote.Value));
        Assert.Contains(build.Key, Needs(promote.Value));
        Assert.All(jobs.Where(j => j.Key != promote.Key && j.Key != build.Key), j => Assert.DoesNotContain("packages: write", j.Value));
    }

    [Fact]
    public void The_gate_runs_the_image_as_compose_does_never_as_root()
    {
        var gate = File.ReadAllText(Path.Combine(Root, "tools", "image-gate.sh"));
        foreach (var flag in new[] { "--read-only", "--tmpfs /tmp", "--user \"$uid:$gid\"", "/data", "/run/unifi-mcp", "127.0.0.1:" })
            Assert.Contains(flag, gate);
        // The setup listener's isolation: what it refuses, and the socket check the public socket's move will extend.
        foreach (var path in new[] { "/mcp", "/healthz", "/oauth/jwks", "/connections", "/signin-code" })
            Assert.Contains($"setup_refuses {path}", gate);
        Assert.Contains("check_sockets", gate);
        Assert.Matches(@"uid.*= 0|uid.*-eq 0", gate);
    }

    [Fact]
    public void The_gate_mounts_each_socket_folder_where_the_server_binds_by_default()
    {
        var gate = File.ReadAllText(Path.Combine(Root, "tools", "image-gate.sh"));
        var defaults = new Settings();
        foreach (var listen in new[] { defaults.Listen, defaults.PublicListen! })
        {
            var folder = Path.GetDirectoryName(listen["unix:".Length..])!.Replace('\\', '/');
            Assert.Matches($@"-v ""\$\w+:{System.Text.RegularExpressions.Regex.Escape(folder)}""", gate);
        }
    }

    [Fact]
    public void The_gate_script_is_valid_bash()
    {
        if (Bash() is not { } bash) return; // no bash here (CI on Linux always has one)
        using var p = Process.Start(new ProcessStartInfo(bash, ["-n", Path.Combine(Root, "tools", "image-gate.sh").Replace('\\', '/')])
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
    }

    [Fact]
    public void A_failed_command_in_a_check_fails_the_check_instead_of_reading_as_empty()
    {
        // Bash drops the exit status of a process substitution and, under pipefail, a grep -q that stops reading early
        // can fail its writer: either way a failed command reads as "nothing to report" and the check passes. Checks
        // capture each command's output in an assignment, which set -e checks, and compare only what they captured.
        foreach (var file in Workflows().Append(Path.Combine(Root, "tools", "image-gate.sh")))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"<\(([^)]*)\)"))
                Assert.Matches(@"^printf '%s\\n' ""\$\w+""$", m.Groups[1].Value);
            Assert.DoesNotMatch(@"\|\s*grep\s+-\w*q", text);
        }
    }

    // ---- Dependencies ------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_action_a_workflow_uses_is_pinned_to_a_commit()
    {
        // A tag can be moved to other code after it was reviewed; a commit cannot. The tag stays as a comment so a reader
        // and Dependabot know which release it is.
        var uses = Workflows()
            .SelectMany(w => Regex.Matches(File.ReadAllText(w), @"(?m)^\s*(?:-\s+)?uses:\s*(.+?)\s*$")
                .Select(m => (File: Path.GetFileName(w), Ref: m.Groups[1].Value)))
            .ToList();
        Assert.NotEmpty(uses);
        Assert.All(uses, u => Assert.True(u.Ref.StartsWith("./") || Regex.IsMatch(u.Ref, @"^[\w.-]+/[\w./-]+@[0-9a-f]{40} # v\d+\.\d+\.\d+$"),
            $"{u.File} uses {u.Ref}: pin it to the release's full commit SHA with the tag as a comment, e.g. actions/checkout@<40-hex sha> # v4.4.0"));
    }

    [Fact]
    public void Dependabot_moves_both_dockerfiles_in_one_pull_request()
    {
        // One entry over both folders with one group: Dependabot raises a group's update across all its directories as a
        // single pull request, so the published image and the Fleet build never pin different .NET versions.
        var docker = Assert.Single(DependabotUpdates(), u => u.Ecosystem == "docker");
        var folders = Dockerfiles.Select(f => "/" + Path.GetDirectoryName(f)!.Replace('\\', '/')).Order();
        Assert.Equal(folders, YamlList(docker.Block, "directories").Order());
        Assert.DoesNotMatch(@"(?m)^\s+directory:", docker.Block);
        Assert.Matches(@"(?m)^    groups:\s*$", docker.Block);
        Assert.Contains("*", YamlList(docker.Block, "patterns"));
        Assert.DoesNotContain("group-by:", docker.Block); // one pull request per image, which splits the pair again
        // A new major is a new .NET (TargetFramework, setup-dotnet, both images): a change of its own, not a weekly bump.
        Assert.Contains("version-update:semver-major", docker.Block);
    }

    [Fact]
    public void Dependabot_watches_every_project_and_the_workflows()
    {
        var updates = DependabotUpdates();
        var nuget = Assert.Single(updates, u => u.Ecosystem == "nuget");
        var projects = Regex.Matches(File.ReadAllText(Path.Combine(Root, "unifi-mcp.slnx")), @"<Project Path=""([^""]+)/[^/""]+\.csproj""")
            .Select(m => "/" + m.Groups[1].Value).Order();
        Assert.Equal(projects, YamlList(nuget.Block, "directories").Order());
        var actions = Assert.Single(updates, u => u.Ecosystem == "github-actions");
        Assert.Matches(@"(?m)^    directory:\s*""?/""?\s*$", actions.Block);
    }

    [Fact]
    public void A_workflow_audits_every_package_weekly_and_reads_the_report_not_the_exit_status()
    {
        var audit = File.ReadAllText(Assert.Single(Workflows(), w => File.ReadAllText(w).Contains("--vulnerable")));
        foreach (var trigger in new[] { "schedule:", "workflow_dispatch:", "pull_request:", "push:" })
            Assert.Contains(trigger, audit);
        Assert.Contains("--include-transitive", audit);
        // The listing exits 0 when it finds vulnerable packages, and after a failed restore it reports nothing at all.
        Assert.Contains("dotnet restore unifi-mcp.slnx", audit);
        Assert.Contains("--no-restore --format json", audit);
        Assert.Contains(".problems", audit);
        Assert.Contains(".vulnerabilities", audit);
    }

    [Fact]
    public void A_new_advisory_never_breaks_a_build()
    {
        // The Fleet deploy builds from source: an advisory published overnight must show in the audit, not stop a deploy.
        var projects = Regex.Matches(File.ReadAllText(Path.Combine(Root, "unifi-mcp.slnx")), @"<Project Path=""([^""]+)""")
            .Select(m => Path.Combine(Root, m.Groups[1].Value)).ToList();
        Assert.NotEmpty(projects);
        // Directory.Build.* anywhere from the root down to a project applies to it.
        var folders = projects.SelectMany(p => Ancestors(Path.GetDirectoryName(p)!)).Distinct();
        var props = folders.SelectMany(d => new[] { "Directory.Build.props", "Directory.Build.targets" }.Select(n => Path.Combine(d, n))).Where(File.Exists);
        foreach (var file in projects.Concat(props))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotMatch(@"<TreatWarningsAsErrors>\s*true", text);
            Assert.DoesNotMatch(@"<WarningsAsErrors>[^<]*NU19", text);
        }

        static IEnumerable<string> Ancestors(string dir)
        {
            for (var d = new DirectoryInfo(dir); d is not null && d.FullName.Length >= Root.Length; d = d.Parent) yield return d.FullName;
        }
    }

    // ---- Operator docs -----------------------------------------------------------------------------------------------

    private static readonly SetupGuide Guide = new(File.ReadAllText(Path.Combine(Root, "README.md")), new Settings());

    private static string PartText(string id) =>
        Regex.Replace(Guide.Parts.SingleOrDefault(p => p.Id == id)?.Html ?? throw new Xunit.Sdk.XunitException($"no setup part #{id}"), "<[^>]+>", "");

    [Fact]
    public void The_upgrade_path_backs_up_state_and_keeps_the_digest_for_rollback()
    {
        var backup = PartText("backup-and-rollback");
        foreach (var needed in new[] { "oauth.db", "oauth.db-wal", "keys", "UNIFI_MCP_DATA_KEY", "docker compose stop", "RepoDigests", "@sha256:", "chmod 700" })
            Assert.Contains(needed, backup);
        Assert.Contains("href=\"#backup-and-rollback\"", Guide.Parts.Single(p => p.Id == "updating").Html);
    }

    [Fact]
    public void The_shared_server_trust_model_sits_next_to_the_setup_guides()
    {
        var sharing = PartText("sharing-one-server");
        foreach (var needed in new[] { "UNIFI_SITE", "siteId", "MCP_ACCESS_TOKEN", "MCP_LAN_ACCESS_TOKEN", "MCP_ALLOWED_LOGINS",
                     "MCP_ALLOWED_GOOGLE_EMAILS", "Disconnect", "UnifiMcp.Audit", "separate instance" })
            Assert.Contains(needed, sharing);
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------------

    private static string Pin(string dockerfile, string image)
    {
        var text = File.ReadAllText(Path.Combine(Root, dockerfile));
        var m = Regex.Match(text, $@"(?m)^FROM\s+(?:--platform=\S+\s+)?mcr\.microsoft\.com/dotnet/{image}:(\S+?)(?:@sha256:[0-9a-f]{{64}})?(?:\s|$)");
        Assert.True(m.Success, $"{dockerfile} has no FROM mcr.microsoft.com/dotnet/{image}");
        return m.Groups[1].Value;
    }

    [GeneratedRegex("""<PackageReference Include="Microsoft\.(?:AspNetCore\.[\w.]+|Data\.Sqlite)" Version="(10\.\d+\.\d+)" />""")]
    private static partial Regex PackageVersion();

    /// <summary>The workflow's jobs by id, each as the text of its block. Two-space-indented keys under <c>jobs:</c>.</summary>
    private static Dictionary<string, string> Jobs()
    {
        var lines = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "image.yml")).Replace("\r\n", "\n").Split('\n');
        var jobs = new Dictionary<string, string>();
        var inJobs = false;
        string? current = null;
        var block = new List<string>();
        void Flush()
        {
            if (current is not null) jobs[current] = string.Join('\n', block);
            block.Clear();
        }
        foreach (var line in lines)
        {
            if (!inJobs) { inJobs = line == "jobs:"; continue; }
            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) break;
            if (Regex.Match(line, @"^  ([\w-]+):\s*$") is { Success: true } m)
            {
                Flush();
                current = m.Groups[1].Value;
                continue;
            }
            block.Add(line);
        }
        Flush();
        Assert.NotEmpty(jobs);
        return jobs;
    }

    private static IEnumerable<string> Workflows() =>
        Directory.EnumerateFiles(Path.Combine(Root, ".github", "workflows"))
            .Where(f => f.EndsWith(".yml", StringComparison.Ordinal) || f.EndsWith(".yaml", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    /// <summary>dependabot.yml's update entries, each as its ecosystem and the text of its block.</summary>
    private static List<(string Ecosystem, string Block)> DependabotUpdates()
    {
        var text = File.ReadAllText(Path.Combine(Root, ".github", "dependabot.yml")).Replace("\r\n", "\n");
        var start = text.IndexOf("\nupdates:\n", StringComparison.Ordinal);
        Assert.True(start >= 0, ".github/dependabot.yml has no top-level updates: list");
        var updates = text[(start + "\nupdates:\n".Length)..];
        if (Regex.Match(updates, @"(?m)^\S") is { Success: true } next) updates = updates[..next.Index]; // the next top-level key
        return Regex.Split(updates, @"(?m)^  - (?=package-ecosystem:)").Skip(1)
            .Select(b => (Regex.Match(b, @"^package-ecosystem:\s*""?([\w-]+)""?").Groups[1].Value, "    " + b))
            .ToList();
    }

    /// <summary>The items of every block list under <paramref name="key"/> in a YAML block, quotes and comments dropped.</summary>
    private static List<string> YamlList(string block, string key) =>
        Regex.Matches(block, $@"(?m)^([ ]*){Regex.Escape(key)}:[ ]*\n((?:\1[ ]+-[ ].*(?:\n|$))+)")
            .SelectMany(m => Regex.Matches(m.Groups[2].Value, @"(?m)^[ ]+-[ ]+""?([^""#\n]*?)""?[ ]*(?:#.*)?$").Select(i => i.Groups[1].Value))
            .ToList();

    private static IReadOnlyList<string> Needs(string job)
    {
        var m = Regex.Match(job, @"(?m)^    needs:\s*(.+)$");
        if (!m.Success) return [];
        return m.Groups[1].Value.Trim().Trim('[', ']').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? Bash()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [@"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe"]
            : ["/bin/bash", "/usr/bin/bash"];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "unifi-mcp.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
