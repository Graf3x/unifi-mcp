using UnifiMcp;

var settings = Settings.FromEnvironment(Environment.GetEnvironmentVariable);
await using var app = McpHost.Build(settings);
await app.RunAsync();
