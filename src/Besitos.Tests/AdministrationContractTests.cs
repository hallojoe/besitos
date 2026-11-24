using System.Text.Json;
using Besitos.Administration;
using Besitos.Demo;
using Besitos.Demo.Web;
using Besitos.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Besitos.Tests;

public sealed class AdministrationContractTests
{
    [Fact]
    public async Task AdministrationMapsWithoutEmailOrStorageInitialization()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddQueueAdministration();
        await using var app = builder.Build();
        app.MapQueueAdministration();
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText).ToArray();
        Assert.Contains("/snapshot", routes);
        Assert.Contains("/items/{id:guid}/replay", routes);
        Assert.Contains("/control/{action}", routes);
        Assert.DoesNotContain(typeof(WorkerAdministration).Assembly.GetReferencedAssemblies(), a => a.Name == "Besitos.Email");
    }

    [Fact]
    public void ConfiguredUnknownProviderAndGroupAcceptHeartbeats()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WorkerGroups:custom-store:custom-group:Endpoint"] = "http://localhost",
            ["WorkerGroups:custom-store:custom-group:Replicas"] = "2",
            ["WorkerGroups:custom-store:custom-group:Workload"] = "custom.workload.v1"
        }).Build();
        var registry = new ProviderRegistry(configuration, new NoClients(), TimeProvider.System);
        Assert.Single(registry.Snapshots());
        registry.Register(new("custom-store", new("process", "custom-group", 1, true, []), 0, []));
        var snapshot = Assert.Single(registry.Snapshots());
        Assert.Equal("custom.workload.v1", snapshot.Workload);
        Assert.Equal(2, snapshot.ExpectedReplicas);
        Assert.True(registry.Control("custom-store", "custom-group", "start").ProcessingEnabled);
    }

    [Fact]
    public void LegacyProtocolStillDeserializesAndAssembliesForwardMovedTypes()
    {
        var json = """{"provider":"sqlite","worker":{"processId":"old","queueName":"default","concurrency":1,"paused":true,"loops":[]},"droppedEvents":0,"events":[],"email":null}""";
        var value = JsonSerializer.Deserialize<TelemetryBatch>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("old", value!.Worker.ProcessId);
        Assert.Null(value.Diagnostics);
        var shared = typeof(DemoOpenTelemetry).Assembly;
        Assert.Contains(typeof(TelemetryBatch), shared.GetForwardedTypes());
        Assert.Contains(typeof(ProviderRegistry), typeof(BenchmarkCatalog).Assembly.GetForwardedTypes());
        Assert.DoesNotContain(typeof(QueueWorker).Assembly.GetReferencedAssemblies(), a =>
            a.Name is "Besitos.Sqlite" or "Besitos.AzureStorage" or "Besitos.Administration" or "Besitos.Console");
        Assert.DoesNotContain(typeof(IQueueStore).Assembly.GetReferencedAssemblies(), a =>
            a.Name!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || a.Name.StartsWith("Azure", StringComparison.Ordinal));
    }

    private sealed class NoClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Registration must not connect.");
    }
}
