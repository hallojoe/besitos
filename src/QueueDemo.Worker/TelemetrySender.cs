using System.Threading.Channels;
using Besitos.Worker;
using Besitos.Email;

namespace Besitos.Demo.Worker;

public sealed class TelemetrySender(IHttpClientFactory clients, IConfiguration configuration, IServiceProvider services)
    : BackgroundService, IQueueTelemetry
{
    private long _dropped;
    private readonly Channel<WorkerEvent> _buffer = Channel.CreateBounded<WorkerEvent>(new BoundedChannelOptions(1024)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    public long Dropped => Interlocked.Read(ref _dropped);
    public void Record(WorkerEvent value)
    {
        if (!_buffer.Writer.TryWrite(value)) Interlocked.Increment(ref _dropped);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoint = configuration["Demo:WebUrl"];
        if (string.IsNullOrWhiteSpace(endpoint)) return;
        var client = clients.CreateClient("telemetry");
        client.BaseAddress = new Uri(endpoint);
        client.DefaultRequestHeaders.Add(DemoSecurity.Header, DemoSecurity.RequireKey(configuration));
        client.Timeout = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            var events = new List<WorkerEvent>(100);
            while (events.Count < 100 && _buffer.Reader.TryRead(out var value)) events.Add(value);
            try
            {
                var worker = services.GetRequiredService<QueueWorker>();
                var status = worker.Status;
                var email = services.GetService<EmailMetrics>();
                using var response = await client.PostAsJsonAsync("/internal/events",
                    new TelemetryBatch(configuration["Queue:Provider"]!, status, Dropped, events, email is null ? null : new(email.Accepted, email.Connections, email.Batches)), stoppingToken);
                if (!response.IsSuccessStatusCode) Interlocked.Add(ref _dropped, events.Count);
                else if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
                {
                    var directive = await response.Content.ReadFromJsonAsync<WorkerDirective>(stoppingToken);
                    if (directive is not null)
                    {
                        if (directive.ProcessingEnabled) worker.StartProcessing();
                        else worker.Pause();
                    }
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            { Interlocked.Add(ref _dropped, events.Count); }
            try { await Task.Delay(events.Count == 100 ? 100 : 1000, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
