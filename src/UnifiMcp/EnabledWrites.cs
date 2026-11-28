namespace UnifiMcp;

/// <summary>Where an operation stands under the current settings.</summary>
public enum WriteState
{
    /// <summary>A read: always on, run through <see cref="ReadOperationTool"/>.</summary>
    Read,
    /// <summary>A write, with UNIFI_ALLOW_WRITES off.</summary>
    WritesOff,
    /// <summary>A write, with writes on but left out of UNIFI_WRITE_TOOLS.</summary>
    NotListed,
    /// <summary>An enabled write: it has its own tool.</summary>
    Enabled,
}

/// <summary>
/// The enabled writes: the write operations that get a tool. None with UNIFI_ALLOW_WRITES off, else all of them, or only
/// those UNIFI_WRITE_TOOLS names. Built once when the host is built; the tool registration, the tools that mention writes
/// and the startup line all ask this one instance. A write that is not enabled has no tool registered, and that is what
/// enforces the list: <see cref="UnifiClient"/>'s own gate checks only UNIFI_ALLOW_WRITES.
/// </summary>
public sealed class EnabledWrites
{
    private readonly IReadOnlySet<string> _names;

    private EnabledWrites(bool on, bool limited, IReadOnlyList<ApiOperation> operations, int allWrites)
    {
        On = on;
        Limited = limited;
        Operations = operations;
        AllWrites = allWrites;
        _names = operations.Select(o => o.ToolName).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The enabled writes, in catalog order, each once.</summary>
    public IReadOnlyList<ApiOperation> Operations { get; }

    /// <summary>How many write operations the catalog has, enabled or not.</summary>
    public int AllWrites { get; }

    /// <summary>Whether writes are on at all (UNIFI_ALLOW_WRITES).</summary>
    public bool On { get; }

    /// <summary>Whether the owner named the write tools with UNIFI_WRITE_TOOLS (whether or not writes are on).</summary>
    public bool Limited { get; }

    public WriteState StateOf(ApiOperation op) =>
        !op.IsWrite ? WriteState.Read
        : !On ? WriteState.WritesOff
        : _names.Contains(op.ToolName) ? WriteState.Enabled
        : WriteState.NotListed;

    /// <summary>
    /// Checks the names in UNIFI_WRITE_TOOLS against the catalog, even with writes off, so a typo shows before writes are
    /// turned on.
    /// </summary>
    /// <exception cref="ArgumentException">A name in UNIFI_WRITE_TOOLS is not a write tool of the catalog.</exception>
    public static EnabledWrites From(Settings settings, OpenApiCatalog catalog)
    {
        var all = catalog.Operations.Where(o => o.IsWrite).ToList();
        if (settings.WriteTools is { } named)
        {
            // Exact names only: a near miss is not guessed at, because a guess could switch on a write the owner did not mean.
            var names = all.Select(o => o.ToolName).ToHashSet(StringComparer.Ordinal);
            if (named.FirstOrDefault(n => !names.Contains(n)) is { } bad)
            {
                var what = catalog.Find(bad) is { IsWrite: false }
                    ? $"'{bad}' is a read operation, and reads are always on; list only write tools."
                    : $"'{bad}' is not a write tool. Names are exact and case-sensitive, with no wildcards.";
                throw new ArgumentException($"UNIFI_WRITE_TOOLS: {what} The write tools are: {string.Join(", ", names.Order(StringComparer.Ordinal))}.");
            }
        }
        var enabled = !settings.AllowWrites ? []
            : settings.WriteTools is { } only ? all.Where(o => only.Contains(o.ToolName, StringComparer.Ordinal)).ToList()
            : all;
        return new EnabledWrites(settings.AllowWrites, settings.WriteTools is not null, enabled, all.Count);
    }
}
