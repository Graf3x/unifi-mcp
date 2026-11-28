using UnifiMcp;

if (args.Contains(HealthCheck.Argument))
    return await HealthCheck.RunAsync(Environment.GetEnvironmentVariable, Console.Out);

// Windows looks environment names up ignoring case, Linux (the image) exactly; the environment compares them the same way.
var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
    environment[(string)entry.Key] = (string?)entry.Value ?? "";
await McpHost.RunAsync(environment, Console.Error);
return 0;
