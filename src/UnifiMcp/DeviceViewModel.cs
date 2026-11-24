namespace UnifiMcp;

// The device reading's facts: what DeviceReading makes of a console device record. The text answer and the view both
// render them, so the two cannot disagree. Every value is read from the console by name; no console object is passed
// through. Serialised for the view with camelCase names and without nulls, by Answer.

/// <summary>One answer for the view. <c>View</c> is <c>devices</c>, <c>device</c> or <c>message</c>.</summary>
internal sealed record DiagnosticsView(string View, string? SiteId, string BuiltAt, IReadOnlyList<DeviceTile>? Devices = null,
    DeviceDetail? Device = null, string? Message = null, IReadOnlyList<Candidate>? Candidates = null);

/// <summary><c>UpdateTo</c> is the version an update brings, <see cref="DeviceReading.UnknownVersion"/> when the console does not say, null without one.</summary>
internal sealed record DeviceTile(string Name, string Mac, string Model, string Type, string State, long? UptimeSeconds, double? CpuPct,
    double? MemoryPct, double? TemperatureC, string Firmware, string? UpdateTo, IReadOnlyList<string> Issues);

internal sealed record Candidate(string Name, string Ip, string Mac);

/// <summary>
/// <c>TemperatureC</c> is the hottest of <c>Sensors</c>, else the device's one general reading. <c>UpdateTo</c> as on
/// <see cref="DeviceTile"/>.
/// </summary>
internal sealed record DeviceDetail(string Name, string Mac, string Model, string Type, string State, string Ip, string Firmware, string? UpdateTo,
    long? UptimeSeconds, double? CpuPct, double? MemoryPct, double? TemperatureC, bool Overheating, double? PoeUsedW,
    double? PoeBudgetW, UplinkFacts? Uplink, IReadOnlyList<PortView> Ports, IReadOnlyList<Sensor> Sensors, bool HasFan, long? FanLevel,
    IReadOnlyList<StorageUse> Storage, IReadOnlyList<RadioFacts> Radios);

/// <summary><c>Interface</c> names the device's own uplink interface, for an uplink to no known device.</summary>
internal sealed record UplinkFacts(string Type, string? DeviceName, string? RemotePort, long? SpeedMbps, bool FullDuplex = false, long RxErrors = 0,
    long TxErrors = 0, string? Interface = null);

internal sealed record Sensor(string Name, double? C);

internal sealed record StorageUse(string Name, double? UsedPct);

/// <summary><c>Radio</c> is the console's band code: <c>ng</c>, <c>na</c>, <c>6e</c>.</summary>
internal sealed record RadioFacts(string Radio, long? Channel, long? WidthMhz, double? TxPower, long? Clients, double? ChannelUsePct,
    double? InterferencePct, double? RetriesPct);

/// <summary><c>State</c> is <c>up</c>, <c>down</c> or <c>disabled</c>. <c>Connected</c> holds at most five entries; <c>ConnectedCount</c> is the total.</summary>
internal sealed record PortView(int Index, string Name, string State, long? SpeedMbps, long? MaxSpeedMbps, string Connector, string? Duplex,
    double? PoeW, long RxErrors, long TxErrors, long Drops, long LinkDowns, string? Stp, IReadOnlyList<string> Flags,
    string? Note, IReadOnlyList<Connected> Connected, int ConnectedCount);

/// <summary><c>Kind</c> is <c>client</c> or <c>device</c>.</summary>
internal sealed record Connected(string Kind, string Name, string Mac);
