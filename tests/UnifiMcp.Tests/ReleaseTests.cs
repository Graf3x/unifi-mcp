using System.Diagnostics;
using System.Text.RegularExpressions;

namespace UnifiMcp.Tests;

/// <summary>
/// The release around the code: which runtime the images run, and the gate an image passes before it is published.
/// These read the repository's files; nothing here builds an image.
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
