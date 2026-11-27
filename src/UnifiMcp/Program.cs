using UnifiMcp;

if (args.Contains(HealthCheck.Argument))
    return await HealthCheck.RunAsync(Environment.GetEnvironmentVariable, Console.Out);

var settings = Settings.FromEnvironment(Environment.GetEnvironmentVariable);
await using var app = McpHost.Build(settings,
    environmentNames: Environment.GetEnvironmentVariables().Keys.Cast<string>());
await app.RunAsync();
return 0;
