using Aspire.Hosting.Dotnet;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Hosting.Azure;

namespace Besitos.Demo.AppHost;

internal static class QueueResources
{
    public static IResourceBuilder<ParameterResource> AddDemoKey(this IDistributedApplicationBuilder builder)
        => builder.AddParameter("demo-key", () => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), secret: true);

    public static IResourceBuilder<DotnetProjectResource> AddQueueConsole(this IDistributedApplicationBuilder builder, IResourceBuilder<ParameterResource> key)
        => builder.AddDotnetProject("console", "../QueueDemo.Web/QueueDemo.Web.csproj")
            .WithHttpEndpoint(name: "http")
            .WithHttpHealthCheck("/health")
            .WithEnvironment("Demo__Key", key);

    public static IResourceBuilder<DotnetProjectResource> AddSqliteWorker(this IDistributedApplicationBuilder builder,
        IResourceBuilder<DotnetProjectResource> console, IResourceBuilder<ParameterResource> key, IResourceBuilder<MailPitContainerResource> mailpit)
    {
        var directory = builder.Configuration["SqliteDirectory"] ?? Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../.data"));
        directory = Path.GetFullPath(directory, builder.AppHostDirectory);
        builder.AddQueueSqliteDatabase(directory);
        var connection = new System.Data.Common.DbConnectionStringBuilder { ["Data Source"] = Path.Combine(directory, "queue.db") };
        builder.AddQueueWorker("sqlite", console, key, "many-email", mailpit)
            .WithEnvironment("ConnectionStrings__queue", connection.ConnectionString);
        return builder.AddQueueWorker("sqlite", console, key, "default", mailpit)
            .WithEnvironment("ConnectionStrings__queue", connection.ConnectionString);
    }

    private static IResourceBuilder<SqliteResource> AddQueueSqliteDatabase(this IDistributedApplicationBuilder builder, string directory)
    {
        var database = builder.AddSqlite("sqlite-queue", directory, "queue.db");
        if (builder.ExecutionContext.IsRunMode)
        {
            database.WithSqliteWeb(ui => ui.WithExplicitStart());
        }
        return database;
    }

    public static IResourceBuilder<DotnetProjectResource> AddPostgresWorker(this IDistributedApplicationBuilder builder,
        IResourceBuilder<DotnetProjectResource> console, IResourceBuilder<ParameterResource> key, IResourceBuilder<MailPitContainerResource> mailpit)
    {
        var database = builder.AddQueuePostgresDatabase();
        builder.AddQueueWorker("postgres", console, key, "many-email", mailpit).WithReference(database, "queue").WaitFor(database);
        return builder.AddQueueWorker("postgres", console, key, "default", mailpit).WithReference(database, "queue").WaitFor(database);
    }

    private static IResourceBuilder<PostgresDatabaseResource> AddQueuePostgresDatabase(this IDistributedApplicationBuilder builder)
    {
        var server = builder.AddPostgres("postgres")
            .WithImageTag("18.1")
            .WithDataVolume("besitos-postgres-data")
            .WithExplicitStart();
        if (builder.ExecutionContext.IsRunMode)
        {
            // Aspire's default server list also includes the unrelated SonarQube database.
            server.WithPgAdmin(ui => ui.WithExplicitStart()
                .WithEnvironment("PGADMIN_SERVER_JSON_FILE", "/pgadmin4/queue-servers.json")
                .WithContainerFiles("/pgadmin4", async (_, cancellationToken) =>
                [
                    new ContainerFile
                    {
                        Name = "queue-servers.json",
                        Contents = JsonSerializer.Serialize(new
                        {
                            Servers = new Dictionary<string, object>
                            {
                                ["1"] = new
                                {
                                    Name = server.Resource.Name,
                                    Group = "Servers",
                                    Host = server.Resource.Name,
                                    Port = server.Resource.PrimaryEndpoint.TargetPort,
                                    Username = "postgres",
                                    SSLMode = "prefer",
                                    MaintenanceDB = "besitos",
                                    PasswordExecCommand = $"echo '{await server.Resource.PasswordParameter.GetValueAsync(cancellationToken)}'"
                                }
                            }
                        })
                    }
                ]));
        }
        return server.AddDatabase("postgres-queue", "besitos");
    }

    public static IResourceBuilder<DotnetProjectResource> AddSqlServerWorker(this IDistributedApplicationBuilder builder,
        IResourceBuilder<DotnetProjectResource> console, IResourceBuilder<ParameterResource> key, IResourceBuilder<MailPitContainerResource> mailpit)
    {
        var database = builder.AddQueueSqlServerDatabase();
        builder.AddQueueWorker("sqlserver", console, key, "many-email", mailpit).WithReference(database, "queue").WaitFor(database);
        return builder.AddQueueWorker("sqlserver", console, key, "default", mailpit).WithReference(database, "queue").WaitFor(database);
    }

    private static IResourceBuilder<SqlServerDatabaseResource> AddQueueSqlServerDatabase(this IDistributedApplicationBuilder builder)
    {
        var server = builder.AddSqlServer("sqlserver")
            .WithImageTag("2022-CU22-ubuntu-22.04")
            .WithDataVolume("besitos-sqlserver-data")
            .WithExplicitStart();
        if (builder.ExecutionContext.IsRunMode)
        {
            server.WithDbGate(ui => ui.WithExplicitStart());
        }
        return server.AddDatabase("sqlserver-queue", "besitos");
    }

    public static IResourceBuilder<DotnetProjectResource> AddAzureStorageWorker(this IDistributedApplicationBuilder builder,
        IResourceBuilder<DotnetProjectResource> console, IResourceBuilder<ParameterResource> key, IResourceBuilder<MailPitContainerResource> mailpit)
    {
        var queues = builder.AddQueueAzureStorage();
        builder.AddQueueWorker("azurestorage", console, key, "many-email", mailpit).WithReference(queues, "queue").WaitFor(queues);
        return builder.AddQueueWorker("azurestorage", console, key, "default", mailpit).WithReference(queues, "queue").WaitFor(queues);
    }

    private static IResourceBuilder<AzureQueueStorageResource> AddQueueAzureStorage(this IDistributedApplicationBuilder builder)
        => builder.AddAzureStorage("azurite")
            .RunAsEmulator(emulator => emulator.WithImageTag("3.35.0")
                .WithDataVolume("besitos-azurite-data").WithExplicitStart())
            .AddQueues("azurestorage-queue");

    private static IResourceBuilder<DotnetProjectResource> AddQueueWorker(this IDistributedApplicationBuilder builder, string provider,
        IResourceBuilder<DotnetProjectResource> console, IResourceBuilder<ParameterResource> key, string group,
        IResourceBuilder<MailPitContainerResource> mailpit)
    {
        var replicas = builder.Configuration.GetValue($"Groups:{group}:Replicas", group == "default" ? 1 : 3);
        var concurrency = builder.Configuration.GetValue($"Groups:{group}:Concurrency", 1);
        if (replicas is < 1 or > 32 || concurrency is < 1 or > 128)
            throw new ArgumentException("Use 1–32 replicas and 1–128 handler slots per replica.");
        var name = group == "default" ? $"{provider}-worker" : $"{provider}-{group}-worker";
        var worker = builder.AddDotnetProject(name, "../QueueDemo.Worker/QueueDemo.Worker.csproj")
            .WithReplicas(replicas)
            .WithHttpEndpoint(name: "http")
            .WithHttpHealthCheck("/health")
            .WithExplicitStart()
            .WithEnvironment("Demo__Key", key)
            .WithEnvironment("Queue__Provider", provider)
            .WithEnvironment("Queue__Name", group)
            .WithEnvironment("Worker__Concurrency", concurrency.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("Worker__AutoProcess", "false")
            .WithEnvironment("Demo__WebUrl", console.GetEndpoint("http"));
        if (group == "many-email")
            worker.WithEnvironment("Smtp__Host", mailpit.GetEndpoint("smtp").Property(EndpointProperty.Host))
                .WithEnvironment("Smtp__Port", mailpit.GetEndpoint("smtp").Property(EndpointProperty.Port))
                .WithEnvironment("Smtp__Security", "None")
                .WithReference(mailpit).WaitFor(mailpit);
        console.WithEnvironment($"WorkerGroups__{provider}__{group}__Endpoint", worker.GetEndpoint("http"))
            .WithEnvironment($"WorkerGroups__{provider}__{group}__Replicas", replicas.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment($"WorkerGroups__{provider}__{group}__Concurrency", concurrency.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return worker;
    }
}
