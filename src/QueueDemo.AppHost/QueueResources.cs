using System.Security.Cryptography;
using System.Text.Json;

namespace Besitos.Demo.AppHost;

internal static class QueueResources
{
    public static IResourceBuilder<ParameterResource> AddDemoKey(this IDistributedApplicationBuilder builder)
        => builder.AddParameter("demo-key", () => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), secret: true);

    public static IResourceBuilder<ProjectResource> AddQueueConsole(this IDistributedApplicationBuilder builder, IResourceBuilder<ParameterResource> key)
        => builder.AddProject<Projects.QueueDemo_Web>("console")
            .WithHttpEndpoint(name: "http")
            .WithHttpHealthCheck("/health")
            .WithEnvironment("Demo__Key", key);

    public static IResourceBuilder<ProjectResource> AddSqliteWorker(this IDistributedApplicationBuilder builder,
        IResourceBuilder<ProjectResource> console, IResourceBuilder<ParameterResource> key)
    {
        var directory = builder.Configuration["SqliteDirectory"] ?? Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../.data"));
        directory = Path.GetFullPath(directory, builder.AppHostDirectory);
        builder.AddQueueSqliteDatabase(directory);
        var connection = new System.Data.Common.DbConnectionStringBuilder { ["Data Source"] = Path.Combine(directory, "queue.db") };
        return builder.AddQueueWorker("sqlite", console, key)
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

    public static IResourceBuilder<ProjectResource> AddPostgresWorker(this IDistributedApplicationBuilder builder,
        IResourceBuilder<ProjectResource> console, IResourceBuilder<ParameterResource> key)
    {
        var database = builder.AddQueuePostgresDatabase();
        return builder.AddQueueWorker("postgres", console, key).WithReference(database, "queue").WaitFor(database);
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

    public static IResourceBuilder<ProjectResource> AddSqlServerWorker(this IDistributedApplicationBuilder builder,
        IResourceBuilder<ProjectResource> console, IResourceBuilder<ParameterResource> key)
    {
        var database = builder.AddQueueSqlServerDatabase();
        return builder.AddQueueWorker("sqlserver", console, key).WithReference(database, "queue").WaitFor(database);
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

    private static IResourceBuilder<ProjectResource> AddQueueWorker(this IDistributedApplicationBuilder builder, string provider,
        IResourceBuilder<ProjectResource> console, IResourceBuilder<ParameterResource> key)
    {
        var worker = builder.AddProject<Projects.QueueDemo_Worker>($"{provider}-worker")
            .WithHttpEndpoint(name: "http")
            .WithHttpHealthCheck("/health")
            .WithExplicitStart()
            .WithEnvironment("Demo__Key", key)
            .WithEnvironment("Queue__Provider", provider)
            .WithEnvironment("Worker__AutoProcess", "false")
            .WithEnvironment("Demo__WebUrl", console.GetEndpoint("http"));
        // Endpoint references allocate addresses. Neither resource waits for the other.
        console.WithEnvironment($"Providers__{provider}", worker.GetEndpoint("http"));
        return worker;
    }
}
