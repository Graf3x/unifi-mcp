using UnifiMcp;

if (args.Contains(HealthCheck.Argument))
    return await HealthCheck.RunAsync(Environment.GetEnvironmentVariable, Console.Out);

await McpHost.RunAsync(Environment.GetEnvironmentVariable, Environment.GetEnvironmentVariables().Keys.Cast<string>(), Console.Error);
return 0;
