using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Besitos.Administration;
using Besitos.Sqlite;
using Besitos.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Besitos.Tests;

public sealed class HandlerCatalogueTests
{
    [Fact]
    public void MetadataSnapshotsDoNotConstructHandlersAndLegacyRegistrationWorks()
    {
        var services = new ServiceCollection();
        using (var schema = JsonDocument.Parse("""{"type":"object"}"""))
            services.AddQueueHandler<NeverConstruct>(new HandlerDescriptor("documented.v1", "Documented", "A test handler.")
            { PayloadSchema = schema.RootElement, DuplicateHandling = DuplicateHandling.Transactional });
        services.AddQueueHandler<NeverConstruct>("legacy.v1");
        using var provider = services.BuildServiceProvider();
        var catalogue = provider.GetRequiredService<IHandlerCatalogue>();
        Assert.Equal("object", catalogue.Find("documented.v1")!.PayloadSchema!.Value.GetProperty("type").GetString());
        Assert.Equal(DuplicateHandling.Unspecified, catalogue.Find("legacy.v1")!.DuplicateHandling);
        Assert.Null(catalogue.Find("missing.v1"));
        Assert.Null(catalogue.Find("DOCUMENTED.v1"));
        Assert.Throws<NotSupportedException>(() => ((ICollection<HandlerDescriptor>)catalogue.Handlers).Clear());
        var json = JsonSerializer.Serialize(catalogue.Handlers);
        Assert.Contains("Transactional", json);
        Assert.DoesNotContain(nameof(NeverConstruct), json);
    }

    [Fact]
    public void InvalidAndDuplicateRegistrationsFailWithoutConstructingHandlers()
    {
        Assert.Throws<ArgumentException>(() => new HandlerCatalogue([
            new("test.v1", typeof(NeverConstruct)), new("test.v1", typeof(NeverConstruct))]));
        Assert.Throws<ArgumentException>(() => new HandlerCatalogue([new("test.v1", typeof(string))]));
        Assert.Throws<ArgumentException>(() => new HandlerCatalogue([new("test.v1", typeof(NeverConstruct))
        { Descriptor = new("other.v1", "Other", "Mismatched command.") }]));
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddQueueHandler<NeverConstruct>(new HandlerDescriptor("test.v1", "", "Missing name.")));
        Assert.Empty(services);
    }

    [Fact]
    public async Task RuntimeAndAdministrationCataloguesAreScopedAndDoNotOpenStores()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddQueueHandler<NeverConstruct>("first.v1");
        services.AddQueueHandler<NeverConstruct>("second.v1");
        services.AddQueueRuntime(runtime => runtime
            .AddProvider(SqliteQueueStore.Descriptor, (_, _) => throw new InvalidOperationException("Store factory must not run."))
            .AddStore("business", "sqlite", new Dictionary<string, string>())
            .AddGroup("first", "business", new() { QueueName = "first-queue" }, ["first.v1"])
            .AddGroup("second", "business", new() { QueueName = "second-queue" }, ["second.v1"]));
        services.AddQueueAdministration(new() { GroupName = "first" });
        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<QueueRuntime>();
        Assert.Equal("second.v1", Assert.Single(runtime.GetHandlerCatalogue("second").Handlers).Command);
        var administration = provider.GetRequiredService<WorkerAdministration>();
        Assert.Equal("first-queue", administration.HandlerQueueName);
        Assert.Equal("first.v1", Assert.Single(administration.Handlers.Handlers).Command);
        Assert.Null(administration.Handlers.Find("second.v1"));
        Assert.Empty(runtime.Workers);
    }

    [Fact]
    public void DiscoveryIsExplicitAndRejectsRepeatedScanningBeforeChangingServices()
    {
        var services = new ServiceCollection().AddHandlerCatalogue();
        using (var empty = services.BuildServiceProvider()) Assert.Empty(empty.GetRequiredService<IHandlerCatalogue>().Handlers);
        services.AddQueueHandlersFromAssembly(typeof(DiscoveredHandler).Assembly);
        var count = services.Count;
        Assert.Throws<ArgumentException>(() => services.AddQueueHandlersFromAssembly(typeof(DiscoveredHandler).Assembly));
        Assert.Equal(count, services.Count);
        using var provider = services.BuildServiceProvider();
        var descriptor = Assert.Single(provider.GetRequiredService<IHandlerCatalogue>().Handlers);
        Assert.Equal("discovered.v1", descriptor.Command);
        Assert.Equal("Discovered handler", descriptor.DisplayName);
    }

    [Fact]
    public async Task HttpCatalogueAndOpenApiWorkWithoutAWorkerOrStore()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddOpenApi();
        builder.Services.AddQueueHandler<NeverConstruct>(new HandlerDescriptor("test.v1", "Test", "Metadata only.")
        { DuplicateHandling = DuplicateHandling.None });
        builder.Services.AddQueueAdministration();
        await using var app = builder.Build();
        app.MapQueueHandlers();
        app.MapOpenApi();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var catalogue = await client.GetFromJsonAsync<HandlerCatalogueResponse>("/handlers");
            Assert.Equal("default", catalogue!.QueueName);
            Assert.Equal("test.v1", Assert.Single(catalogue.Handlers).Command);
            var descriptor = await client.GetFromJsonAsync<HandlerDescriptor>("/handlers/test.v1");
            Assert.Equal(DuplicateHandling.None, descriptor!.DuplicateHandling);
            using var missing = await client.GetAsync("/handlers/missing.v1");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal("application/problem+json", missing.Content.Headers.ContentType!.MediaType);
            var openapi = await client.GetStringAsync("/openapi/v1.json");
            Assert.Contains("/handlers/{command}", openapi);
            Assert.Contains("List registered handlers", openapi);
        }
        finally { await app.StopAsync(); }
    }

    [Theory]
    [InlineData("{broken", "application/json")]
    [InlineData("null", "application/json")]
    [InlineData("{}", "application/json")]
    [InlineData("{\"name\":\"Ada\",\"count\":0}", "application/json")]
    [InlineData("{\"name\":\"Ada\",\"count\":2}", "text/plain")]
    public async Task TypedHandlersRejectInvalidPayloadsBeforeBusinessCode(string payload, string media)
    {
        var handler = new TypedHandler();
        var error = await Assert.ThrowsAsync<PermanentCommandException>(() => handler.HandleAsync(Item(payload, media), default));
        Assert.Equal(0, handler.Calls);
        Assert.DoesNotContain("Ada", error.Message);
    }

    [Fact]
    public async Task TypedHandlersPreserveContextCancellationAndBusinessFailures()
    {
        var item = Item("{\"name\":\"Ada\",\"count\":2}", "application/example+json; charset=utf-8");
        var handler = new TypedHandler();
        await handler.HandleAsync(item, default);
        Assert.Same(item, handler.Item);
        Assert.Equal("Ada", handler.Payload!.Name);
        Assert.Equal(2, handler.Payload.Count);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.HandleAsync(item, cancellation.Token));
        Assert.Equal(1, handler.Calls);
        handler.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => handler.HandleAsync(item, default));
    }

    private static QueueItem Item(string payload, string media)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), "default", "test.v1", payload, media, null,
            QueueState.Leased, now, now, 1, 5, Guid.NewGuid(), now.AddMinutes(1), null);
    }

    public sealed class NeverConstruct : ICommandHandler
    {
        public NeverConstruct() => throw new InvalidOperationException("Metadata must not construct handlers.");
        public Task HandleAsync(QueueItem item, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [QueueHandler("discovered.v1", "Discovered handler", "An opt-in discovery example.", PayloadMediaType = "application/json")]
    public sealed class DiscoveredHandler : ICommandHandler
    {
        public DiscoveredHandler() => throw new InvalidOperationException("Discovery must not construct handlers.");
        public Task HandleAsync(QueueItem item, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed record Payload([property: Required] string Name, [property: Range(1, 10)] int Count);
    private sealed class TypedHandler : JsonCommandHandler<Payload>
    {
        public int Calls;
        public bool Fail;
        public QueueItem? Item;
        public Payload? Payload;
        protected override Task HandleAsync(QueueItem item, Payload payload, CancellationToken cancellationToken)
        {
            Calls++; Item = item; Payload = payload;
            return Fail ? Task.FromException(new IOException("Business failure.")) : Task.CompletedTask;
        }
    }
}
