/* Sample answers for the development host page. Made-up names and addresses; field names follow DeviceViewModel.cs. */
(function () {
  "use strict";
  var BUILT = "2026-11-21T12:00:00Z";

  function port(index, over) {
    var p = { index: index, name: "Port " + index, state: "down", connector: "RJ45", rxErrors: 0, txErrors: 0, drops: 0, linkDowns: 0,
              flags: [], connected: [], connectedCount: 0 };
    Object.keys(over || {}).forEach(function (k) { p[k] = over[k]; });
    return p;
  }
  function up(index, speed, max, over) {
    var p = port(index, { state: "up", speedMbps: speed, maxSpeedMbps: max, duplex: "full", stp: "forwarding" });
    Object.keys(over || {}).forEach(function (k) { p[k] = over[k]; });
    return p;
  }
  function client(name, n) { return { kind: "client", name: name, mac: "cc:00:00:00:00:" + (n < 16 ? "0" : "") + n.toString(16) }; }
  function device(over, ports) {
    var d = { name: "Device", mac: "aa:00:00:00:00:00", model: "US24", type: "usw", state: "online", ip: "192.168.1.2", firmware: "7.4.1",
              uptimeSeconds: 172800, cpuPct: 3, memoryPct: 55, temperatureC: 61, overheating: false, ports: ports || [] };
    Object.keys(over).forEach(function (k) { d[k] = over[k]; });
    return { view: "device", builtAt: BUILT, device: d };
  }

  var corePorts = [];
  for (var i = 1; i <= 24; i++) corePorts.push(i % 3 === 0 ? port(i) : up(i, 1000, 1000));
  corePorts[0] = up(1, 1000, 1000, { poeW: 6.65, connected: [{ kind: "device", name: "Hall AP", mac: "aa:00:00:00:00:01" }], connectedCount: 1 });
  corePorts[1] = up(2, 100, 1000, { name: "NAS", duplex: "half", rxErrors: 1532, drops: 12, linkDowns: 14,
    flags: ["errors", "flapping", "half duplex", "slow link"], connected: [client("nas", 1)], connectedCount: 1,
    note: "Linked at 100 Mb half duplex on a port that can do 1 GbE; receive errors; link dropped 14 times since the device restarted: suspect the cable or the device's network card." });
  corePorts[4] = up(5, 100, 1000, { name: "Printer", flags: ["slow link"], connected: [client("printer", 2)], connectedCount: 1,
    note: "Linked at 100 Mb on a port that can do 1 GbE: the device or its cable may only support this speed." });
  corePorts[6] = up(7, 1000, 1000, { poeW: 4.2, connected: [client("door camera", 3)], connectedCount: 1 });
  corePorts[10] = up(11, 100, 100, { name: "Old hub" });
  corePorts[12] = up(13, 1000, 1000, { poeW: 12.9 });
  corePorts[14] = up(15, 1000, 1000, { name: "Shed", linkDowns: 9, flags: ["flapping"],
    note: "Link dropped 9 times since the device restarted: check the cable and the device's power." });
  corePorts[19] = port(20, { name: "Spare", state: "disabled" });
  corePorts[22] = up(23, 1000, 1000, { name: "Media switch", connectedCount: 7,
    connected: [client("console", 4), client("receiver", 5), client("set-top box", 6), client("speaker", 7), client("tv", 8)] });
  corePorts.push(up(25, 10000, 10000, { name: "SFP+ 1", connector: "SFPPLUS", connected: [{ kind: "device", name: "Gateway", mac: "aa:00:00:00:00:04" }], connectedCount: 1 }));
  corePorts.push(port(26, { name: "SFP+ 2", connector: "SFPPLUS", maxSpeedMbps: 10000 }));

  var bigPorts = [];
  for (var b = 1; b <= 48; b++) bigPorts.push(b % 5 === 0 ? port(b) : up(b, 1000, 1000, b % 4 === 0 ? { poeW: 3.1 } : {}));
  bigPorts[8] = up(9, 100, 1000, { flags: ["slow link"], note: "Linked at 100 Mb on a port that can do 1 GbE: the device or its cable may only support this speed." });
  bigPorts[30] = up(31, 1000, 1000, { txErrors: 44, flags: ["errors"], note: "Send errors: suspect the cable or the device's network card." });
  for (var s = 49; s <= 52; s++) bigPorts.push(s === 49 ? up(s, 25000, 25000, { name: "SFP28 " + (s - 48), connector: "SFP28" }) : port(s, { name: "SFP28 " + (s - 48), connector: "SFP28", maxSpeedMbps: 25000 }));

  var flexPorts = [up(1, 2500, 2500, { name: "Uplink" }), up(2, 1000, 2500), port(3), up(4, 2500, 2500), port(5)];

  var hostilePorts = [
    up(1, 100, 1000, { name: "a|b", flags: ["slow link"], connected: [client("<img src=x onerror=alert(1)>", 9)], connectedCount: 1,
      note: "Linked at 100 Mb on a port that can do 1 GbE: the device or its cable may only support this speed." }),
    up(2, 1000, 1000, { name: "<b>bold</b>", connected: [client("\"; alert(1); //", 10)], connectedCount: 1 }),
    port(3), port(4), port(5), port(6),
  ];

  var core = device({ name: "Core Switch", mac: "aa:00:00:00:00:03", updateTo: "7.4.12", poeUsedW: 23.75, poeBudgetW: 95,
    uplink: { type: "wire", deviceName: "Gateway", remotePort: "9", speedMbps: 10000 } }, corePorts);
  var big = device({ name: "Rack Switch", mac: "aa:00:00:00:00:05", model: "US48PRO", ip: "192.168.1.3", poeUsedW: 31, poeBudgetW: 600, overheating: true }, bigPorts);
  var flex = device({ name: "Desk Switch", mac: "aa:00:00:00:00:06", model: "USWED35", ip: "192.168.1.4", temperatureC: undefined }, flexPorts);
  var gateway = device({ name: "Gateway", mac: "aa:00:00:00:00:04", model: "UDMPRO", type: "udm", ip: "192.168.1.1", firmware: "6.0.15",
    uptimeSeconds: 90000, cpuPct: 53.2, memoryPct: 72.3, temperatureC: 45 }, []);
  var hostile = device({ name: "Lab Switch", mac: "aa:00:00:00:00:07", model: "USMINI", ip: "192.168.1.5" }, hostilePorts);
  var hallAp = device({ name: "Hall AP", mac: "aa:00:00:00:00:01", model: "U6P", type: "uap", ip: "192.168.1.20", firmware: "8.8.14",
    uptimeSeconds: 86400, cpuPct: 7.5, memoryPct: 30.1, temperatureC: undefined,
    uplink: { type: "wire", deviceName: "Core Switch", remotePort: "1", speedMbps: 1000 } }, []);

  function tile(model, issues) {
    var d = model.device;
    return { name: d.name, mac: d.mac, model: d.model, type: d.type, state: d.state, uptimeSeconds: d.uptimeSeconds, cpuPct: d.cpuPct,
             memoryPct: d.memoryPct, temperatureC: d.temperatureC, firmware: d.firmware, updateTo: d.updateTo, issues: issues || [] };
  }

  window.SAMPLES = {
    devices: { view: "devices", builtAt: BUILT, devices: [
      { name: "Garage AP", mac: "aa:00:00:00:00:02", model: "U6M", type: "uap", state: "offline", firmware: "8.8.14", issues: [] },
      tile(core, ["port(s) 2, 5, 15 flagged"]), tile(flex), tile(gateway), tile(hallAp), tile(hostile, ["port(s) 1 flagged"]), tile(big, ["port(s) 9, 31 flagged", "uplink errors"]),
    ] },
    message: { view: "message", builtAt: BUILT, message: "'switch' matches 2 devices; use the IP or MAC.", candidates: [
      { name: "Core Switch", ip: "192.168.1.2", mac: "aa:00:00:00:00:03" }, { name: "Rack Switch", ip: "192.168.1.3", mac: "aa:00:00:00:00:05" },
    ] },
    messageOnly: { view: "message", builtAt: BUILT, message: "No device matches 'toaster'. Devices: Garage AP, Core Switch, Gateway, Hall AP." },
    down: { view: "message", builtAt: BUILT, message: "The console answered 503 to GET /api/s/default/stat/device: service unavailable" },
    named: { core: core, big: big, flex: flex, gateway: gateway, hostile: hostile },
    byMac: {},
  };
  [core, big, flex, gateway, hostile, hallAp].forEach(function (m) { window.SAMPLES.byMac[m.device.mac] = m; });
})();
