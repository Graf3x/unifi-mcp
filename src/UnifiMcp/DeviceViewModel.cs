namespace UnifiMcp;

// What the device diagnostics view draws. Every value is read from the console by name in DiagnosticTools; no console
// object is passed through. Serialised with camelCase names and without nulls, by Answer.

/// <summary>One answer for the view. <c>View</c> is <c>devices</c>, <c>device</c> or <c>message</c>.</summary>
internal sealed record DiagnosticsView(string View, string? SiteId, string BuiltAt, IReadOnlyList<DeviceTile>? Devices = null,
    DeviceDetail? Device = null, string? Message = null, IReadOnlyList<Candidate>? Candidates = null);

internal sealed record DeviceTile(string Name, string Mac, string Model, string Type, string State, long? UptimeSeconds, double? CpuPct,
    double? MemoryPct, double? TemperatureC, string Firmware, string? UpdateTo, IReadOnlyList<string> Issues);

internal sealed record Candidate(string Name, string Ip, string Mac);

internal sealed record DeviceDetail(string Name, string Mac, string Model, string Type, string State, string Ip, string Firmware, string? UpdateTo,
    long? UptimeSeconds, double? CpuPct, double? MemoryPct, double? TemperatureC, bool Overheating, double? PoeUsedW,
    double? PoeBudgetW, UplinkFacts? Uplink, IReadOnlyList<PortView> Ports);

internal sealed record UplinkFacts(string Type, string? DeviceName, string? RemotePort, long? SpeedMbps);

/// <summary><c>State</c> is <c>up</c>, <c>down</c> or <c>disabled</c>. <c>Connected</c> holds at most five entries; <c>ConnectedCount</c> is the total.</summary>
internal sealed record PortView(int Index, string Name, string State, long? SpeedMbps, long? MaxSpeedMbps, string Connector, string? Duplex,
    double? PoeW, long RxErrors, long TxErrors, long Drops, long LinkDowns, string? Stp, IReadOnlyList<string> Flags,
    string? Note, IReadOnlyList<Connected> Connected, int ConnectedCount);

/// <summary><c>Kind</c> is <c>client</c> or <c>device</c>.</summary>
internal sealed record Connected(string Kind, string Name, string Mac);
