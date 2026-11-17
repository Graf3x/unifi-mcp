namespace UnifiMcp;

/// <summary>Whether the public (Claude connector) side can serve. Ready only until something marks it otherwise.</summary>
public sealed class ConnectorState
{
    public bool Ready => Reason is null;
    public string? Reason { get; private set; }
    /// <summary>False when values were missing at startup, so none of the connector's services were registered.</summary>
    public bool Configured { get; private set; } = true;
    /// <summary>A reason found while the host was being built, before any logger exists: the startup check logs it once.</summary>
    public string? UnloggedReason { get; private set; }

    public void NotConfigured(IReadOnlyList<string> missing)
    {
        Configured = false;
        Reason = "not configured: " + string.Join("; ", missing);
    }

    /// <param name="logAtStart">The caller cannot log: the startup check writes the reason once, at Warning.</param>
    public void Broken(string reason, bool logAtStart = false)
    {
        Reason = reason;
        if (logAtStart) UnloggedReason = reason;
    }
}
