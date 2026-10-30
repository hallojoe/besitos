using System.Text.Json;
using System.Threading.Channels;

namespace Besitos.Demo.Web;

public sealed record StreamEvent(string Id, string Type, string Json);

/// <summary>A bounded session replay ring. Slow subscribers disconnect and resume through Last-Event-ID.</summary>
public sealed class EventHub
{
    private readonly object _gate = new();
    private readonly Queue<StreamEvent> _history = new();
    private readonly Dictionary<Channel<StreamEvent>, CancellationTokenSource> _clients = [];
    private readonly string _session = Guid.NewGuid().ToString("N");
    private long _sequence;
    private const int Capacity = 512;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public int RetainedCount { get { lock (_gate) return _history.Count; } }
    public int ClientCount { get { lock (_gate) return _clients.Count; } }

    public void Publish(string type, object data)
    {
        lock (_gate)
        {
            var value = new StreamEvent($"{_session}:{++_sequence}", type, JsonSerializer.Serialize(data, Json));
            _history.Enqueue(value);
            if (_history.Count > Capacity) _history.Dequeue();
            foreach (var (client, cancellation) in _clients)
                if (!client.Writer.TryWrite(value)) { client.Writer.TryComplete(); cancellation.Cancel(); }
        }
    }

    public Subscription Subscribe(string? lastEventId, object snapshot)
    {
        lock (_gate)
        {
            if (_clients.Count >= 32) throw new InvalidOperationException("The SSE client limit was reached.");
            var channel = Channel.CreateBounded<StreamEvent>(new BoundedChannelOptions(Capacity + 2)
            { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
            var previous = _history.FirstOrDefault(x => x.Id == lastEventId);
            if (previous is not null)
            {
                var found = false;
                foreach (var value in _history)
                {
                    if (found) channel.Writer.TryWrite(value);
                    if (value.Id == lastEventId) found = true;
                }
            }
            else
            {
                // Reset carries the current session cursor, not a made-up durable history identity.
                channel.Writer.TryWrite(new($"{_session}:{_sequence}", "reset",
                    JsonSerializer.Serialize(new { reason = lastEventId is null ? "connected" : "history-gap", snapshot }, Json)));
            }
            var cancellation = new CancellationTokenSource();
            _clients.Add(channel, cancellation);
            return new Subscription(channel.Reader, cancellation.Token, () =>
            {
                lock (_gate) { _clients.Remove(channel); channel.Writer.TryComplete(); cancellation.Dispose(); }
            });
        }
    }
    public sealed class Subscription(ChannelReader<StreamEvent> reader, CancellationToken cancellation, Action dispose) : IDisposable
    {
        public ChannelReader<StreamEvent> Reader { get; } = reader;
        public CancellationToken Cancellation { get; } = cancellation;
        public void Dispose() => dispose();
    }
    public static string Frame(StreamEvent value) => $"id: {value.Id}\nevent: {value.Type}\ndata: {value.Json}\n\n";
}
