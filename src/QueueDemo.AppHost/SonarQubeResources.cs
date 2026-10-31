namespace Besitos.Demo.AppHost;

internal static class SonarQubeResources
{
    public static IResourceBuilder<ContainerResource> AddSonarQube(this IDistributedApplicationBuilder builder)
    {
        var password = builder.AddParameter("sonar-db-password", new GenerateParameterDefault { MinLength = 32 },
            secret: true, persist: true);
        var postgres = builder.AddSonarPostgres(password);
        var database = postgres.AddDatabase("sonar-db", "sonar");
        var endpoint = postgres.GetEndpoint("tcp");

        return builder.AddContainer("sonarqube", "docker.io/library/sonarqube", "26.10.0.133078-community")
            .WithHttpEndpoint(port: 9000, targetPort: 9000, name: "http")
            .WithEnvironment("SONAR_JDBC_URL", ReferenceExpression.Create(
                $"jdbc:postgresql://{endpoint.Property(EndpointProperty.Host)}:{endpoint.Property(EndpointProperty.Port)}/{database.Resource.DatabaseName}"))
            .WithEnvironment("SONAR_JDBC_USERNAME", postgres.Resource.UserNameReference)
            .WithEnvironment("SONAR_JDBC_PASSWORD", password)
            .WithVolume("besitos-sonarqube-data", "/opt/sonarqube/data")
            .WithVolume("besitos-sonarqube-extensions", "/opt/sonarqube/extensions")
            .WithVolume("besitos-sonarqube-logs", "/opt/sonarqube/logs")
            .WithExplicitStart()
            .WaitFor(database);
    }

    private static IResourceBuilder<PostgresServerResource> AddSonarPostgres(this IDistributedApplicationBuilder builder,
        IResourceBuilder<ParameterResource> password)
        => builder.AddPostgres("sonar-postgres", password: password)
            .WithImageTag("18.1")
            .WithDataVolume("besitos-sonar-postgres-data")
            .WithExplicitStart();
}
