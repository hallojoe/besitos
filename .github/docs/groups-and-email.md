# Queue groups and batch email

Each provider has two independent logical queues. `default` runs the existing demo commands. `many-email` runs `email.batch.v1`.

The AppHost creates one default replica and three email replicas for each provider. Both groups share that provider's storage resource. Queue names isolate their work. This reserves worker capacity for a workload; it does not provide message priorities or ordered processing.

## Start and configure

Start the console with Aspire. Start Mailpit, the chosen storage resource, and the desired worker group in the dashboard. All worker resources require explicit startup and begin paused. Replica resource names have generated suffixes. The dashboard shows each replica separately.

Set these AppHost configuration values before startup:

| Setting | Default | Meaning |
|---|---:|---|
| `Groups:default:Replicas` | 1 | Default worker instances |
| `Groups:many-email:Replicas` | 3 | Email worker instances |
| `Groups:default:Concurrency` | 1 | Handler slots per default instance |
| `Groups:many-email:Concurrency` | 1 | Handler slots per email instance |
| `Mailpit:MaxMessages` | 100000 | Retained Mailpit messages; zero disables pruning |

Use environment names such as `Groups__many-email__Concurrency=2`. Each group permits 1–32 replicas and 1–128 slots per replica. Three replicas with two slots provide six concurrent batch handlers. Each batch sends its recipients sequentially.

The AppHost derives SMTP addresses from Mailpit endpoints. No fixed SMTP port is required. Mailpit captures test emails. The demo configures plaintext SMTP without authentication for this local resource.

Standalone workers accept `Queue__Name`, `Worker__Concurrency`, and `Smtp__Host`, `Smtp__Port`, `Smtp__Security`, `Smtp__Username`, `Smtp__Password`, `Smtp__FromAddress`, and `Smtp__FromName`. SMTP security uses MailKit's `SecureSocketOptions`. Outside the AppHost, its default is `Auto`.

## Console controls

Select the provider and group in Operations. The console shows connected replicas, configured concurrency, and active handler slots. Queue statistics describe shared storage once. Azure source and poison counts remain approximate.

Select **Enqueue a command** to open the envelope modal. It closes with Cancel or Escape and restores focus. For `many-email`, the button opens the test email generator. Set total emails and emails per envelope. Generated recipients use `example.test`.

Start and pause apply to the entire selected group. The console sends desired state in each replica's heartbeat response. A pending indicator remains until live replicas acknowledge the change. Pause stops new claims; existing handlers finish.

The console owns generation jobs and their cancellation. Worker endpoints accept bounded enqueue batches through the shared proxy. Progress includes only acknowledged IDs. Failed or cancelled requests can leave the current batch uncertain. The console does not retry such requests automatically. Azure sends within a batch remain non-atomic.

Generation progress counts envelopes and emails separately. Email counters show SMTP acceptance, connections, and batch attempts across live replicas. These counters reset after replica restarts. SMTP acceptance is not final delivery.

Console state is temporary. Workers retain their last processing state during console outages. A console restart resets desired state to paused. Late replicas receive the group's current state. Heartbeats expire after ten seconds. No queue data is deleted by these control changes.

Existing `/api/{provider}/...` routes still target `default`. Group routes use `/api/{provider}/groups/{group}/...`. Direct worker control/load endpoints remain for standalone tools. A connected console overrides direct processing controls on the next heartbeat. Use group controls for managed replicas.

## Reuse the sender

Reference `Queue.Email`, then register its handler and services:

```csharp
services.AddEmailBatches(new SmtpOptions
{
    Host = "localhost",
    Port = 1025,
    Security = MailKit.Security.SecureSocketOptions.None
});
services.AddQueueWorker(new QueueWorkerOptions { QueueName = "many-email" });

var batch = new EmailBatch("Test message", "Message text", ["recipient@example.test"]);
await store.EnqueueAsync(batch.ToEnvelope());
```

`IEmailBatchSender` can also send a batch directly. It validates the complete batch before connecting. Each batch owns one SMTP client and connection. It sends separate MIME messages, so recipients do not see each other's addresses. Clients are never shared across concurrent handlers.

A batch permits 1–100 recipients, a subject up to 200 characters, and text/HTML bodies up to 16,000 characters each. Queue payload limits still apply. Azure permits a smaller complete initial JSON message than relational providers. Large content can require smaller batches.

Malformed commands and permanent SMTP rejections become permanent queue failures. Transient failures use the queue retry policy. Cancellation stops further sends. Disconnect failures do not turn accepted batches into retries. Metrics use the `Besitos.Email` meter.

A retry sends the whole envelope again. Emails accepted before an interruption can therefore appear twice. Message IDs remain stable for correlation, but do not suppress duplicates. There is no per-recipient delivery ledger, campaign manager, attachment support, or production deployment configuration.
