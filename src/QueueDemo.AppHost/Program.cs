using Besitos.Demo.AppHost;

var builder = DistributedApplication.CreateBuilder(args);
var key = builder.AddDemoKey();
var console = builder.AddQueueConsole(key);
builder.AddSqliteWorker(console, key);
builder.AddPostgresWorker(console, key);
builder.AddSqlServerWorker(console, key);
builder.Build().Run();
