using Besitos.Demo.AppHost;

var builder = DistributedApplication.CreateBuilder(args);
var key = builder.AddDemoKey();
var console = builder.AddQueueConsole(key);
var mailpit = builder.AddQueueMailpit(console);
builder.AddSqliteWorker(console, key, mailpit);
builder.AddPostgresWorker(console, key, mailpit);
builder.AddSqlServerWorker(console, key, mailpit);
builder.AddAzureStorageWorker(console, key, mailpit);
builder.AddSonarQube();
await builder.Build().RunAsync();
