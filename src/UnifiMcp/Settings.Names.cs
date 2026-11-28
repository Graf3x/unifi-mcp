using System.Collections.Frozen;

namespace UnifiMcp;

/// <summary>
/// The environment names this server reads, so a misspelt one is reported instead of silently ignored: a typo in
/// MCP_SETUP_PAGE would otherwise leave the setup page on.
/// </summary>
public sealed partial record Settings
{
    /// <summary>
    /// Every name <see cref="FromEnvironment"/> reads: the names it asks for when nothing is set. Every read is made whatever
    /// the values (EnvironmentNamesTests checks a full environment asks for the same), so a new setting is known as soon as
    /// it is read.
    /// </summary>
    public static IReadOnlySet<string> EnvironmentNames => KnownNames.Value;

    private static readonly Lazy<FrozenSet<string>> KnownNames = new(() =>
    {
        var read = new HashSet<string>(StringComparer.Ordinal);
        FromEnvironment(name => { read.Add(name); return null; });
        return read.ToFrozenSet(StringComparer.Ordinal);
    });

    /// <summary>
    /// Names with our prefixes that belong to other containers, so they may sit in the same .env without a warning.
    /// UNIFI_MCP_TUNNEL_TOKEN is the Fleet key deploy/compose.yaml hands cloudflared as TUNNEL_TOKEN.
    /// </summary>
    public static readonly IReadOnlySet<string> OtherContainerNames = new[] { "UNIFI_MCP_TUNNEL_TOKEN" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] CheckedPrefixes = ["UNIFI_", "MCP_"];

    // A suggestion within two edits catches a dropped, doubled, swapped or wrong letter without guessing at far-off names.
    private const int MaxSuggestionDistance = 2;

    /// <summary>
    /// One warning per name that starts with UNIFI_ or MCP_ and is not a known one, naming the closest known name when it
    /// is within <see cref="MaxSuggestionDistance"/> edits. Takes names only, so no value can reach the log. Other prefixes
    /// (GOOGLE_, DOTNET_, PATH) are left alone: tools other than this server set them too.
    /// </summary>
    /// <param name="nameCase">How the process's environment compares names, so a known name in other case warns only where
    /// it is not read: case-sensitive on Linux (the image), ignoring case on Windows.</param>
    public static IReadOnlyList<string> UnknownNameWarnings(IEnumerable<string> names, IEqualityComparer<string> nameCase)
    {
        var known = EnvironmentNames.Concat(OtherContainerNames).ToHashSet(nameCase);
        return names
            // Any case: on Linux mcp_setup_page is never read, which is exactly the silent typo this catches.
            .Where(n => CheckedPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) && !known.Contains(n))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(n => Closest(n) is { } meant
                ? $"Environment variable {n} is not a setting this server reads, so it has no effect. Did you mean {meant}?"
                : $"Environment variable {n} is not a setting this server reads, so it has no effect. The settings are listed in .env.example and the README's Settings reference.")
            .ToList();
    }

    private static string? Closest(string name) =>
        EnvironmentNames.Concat(OtherContainerNames)
            .Select(k => (Name: k, Distance: EditDistance(name.ToUpperInvariant(), k)))
            .Where(c => c.Distance <= MaxSuggestionDistance)
            .OrderBy(c => c.Distance).ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => c.Name)
            .FirstOrDefault();

    /// <summary>Levenshtein distance: the fewest single-character inserts, deletes or substitutions from a to b.</summary>
    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1], previous[j]) + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
