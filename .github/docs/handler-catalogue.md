# Handler registration and catalogue

Besitos registers handlers explicitly by default. Registration creates a command-to-type mapping for each worker.
The worker resolves the handler from a fresh dependency-injection scope for each execution.
Catalogue metadata is prepared during registration or startup. Existing dispatch uses the same cached dictionary lookup.
Catalogue requests do not query queue storage or construct handlers.

## Explicit registration

Existing registration remains supported:

```csharp
services.AddQueueHandler<SendReceiptHandler>("receipt.send.v1");
```

This produces a minimal catalogue entry. Duplicate handling is `Unspecified`, not an inferred guarantee.
Add a descriptor when clients need documentation:

```csharp
services.AddQueueHandler<SendReceiptHandler>(new HandlerDescriptor(
    "receipt.send.v1", "Send receipt", "Submits one receipt through the configured provider.")
{
    PayloadMediaType = "application/json",
    ExamplePayload = """{"operationId":"aa7fdbcf-f52e-434e-a4a5-57518dcbe090","recipient":"test@example.test","text":"Thank you."}""",
    DuplicateHandling = DuplicateHandling.ProviderSupported,
    DuplicateHandlingNotes = "The adapter reuses the operation key. Protection depends on the provider's retention window."
});
```

`HandlerDescriptor` also accepts `PayloadSchema` as a JSON Schema object or boolean, and `ExampleMetadata` as JSON object text.
Schema, examples, descriptions, and duplicate handling are author declarations. They do not inspect or verify implementation behavior.
Schemas are not automatically enforced during dispatch. The handler remains responsible for validating its input.
Registration snapshots schema documents, so callers can dispose their original `JsonDocument` instances.

Descriptions permit 2,048 characters. Examples and schemas permit 16,384 characters each.
Example metadata permits 4,096 characters. Display names permit 128 characters.
Include public documentation only. Do not embed credentials, live customer data, or private implementation details.

Duplicate declarations are `Unspecified`, `None`, `NaturallyIdempotent`, `Transactional`, `ProviderSupported`, and `DurableReservation`.
They serialize as strings without changing existing queue-state serialization.
The email demo declares `None`: its SMTP sender can repeat previously accepted emails.
The optional [protected email handler](protected-email.md) declares `DurableReservation` and blocks uncertain sends for review.

## Read the catalogue through code

```csharp
var catalogue = services.GetRequiredService<IHandlerCatalogue>();
var all = catalogue.Handlers;
var receipt = catalogue.Find("receipt.send.v1"); // null when absent

var groupCatalogue = runtime.GetHandlerCatalogue("receipts");
```

The DI catalogue lists the application's registrations.
`QueueRuntime.GetHandlerCatalogue(group)` lists only that group's configured commands, even before runtime startup.
`QueueWorker.HandlerCatalogue` exposes the mapping associated with that worker.
The collections are immutable snapshots. Command matching is case-sensitive.
Duplicate commands, descriptor mismatches, and invalid handler types fail before execution.
Registration changes require a new worker, normally through an application restart.

The queue store still accepts opaque command names. A catalogue does not introduce submission-time handler checks.
An unknown command still becomes a permanent failure when a worker processes it.

## HTTP endpoints

Register and map the optional administration services as before:

```csharp
builder.Services.AddQueueAdministration(new() { GroupName = "receipts" });
// Apply the host's authentication and authorization.
app.MapQueueAdministration();
```

| Route | Result |
|---|---|
| `GET /handlers` | Queue name and the selected worker group's descriptors |
| `GET /handlers/{command}` | One descriptor, or 404 Problem Details |
| `GET /api/{provider}/handlers` | Console proxy for the default group |
| `GET /api/{provider}/handlers/{command}` | Console proxy for one default-group descriptor |
| `GET /api/{provider}/groups/{group}/handlers` | Console proxy for a selected group |
| `GET /api/{provider}/groups/{group}/handlers/{command}` | Console proxy for one selected-group descriptor |

The proxy reports unknown groups as 404 and unavailable workers as 503 Problem Details.
The worker routes inherit the host's existing access controls. The demo requires `X-Besitos-Key` on worker requests.
The console keeps that key on its server. The browser never receives it.

Development demo hosts expose `/openapi/handlers.json` for the catalogue routes.
This document is scoped to the catalogue. Legacy command DTOs currently trigger a separate TimeSpan schema-generation incompatibility.
Other hosts can use the endpoint metadata with their own OpenAPI configuration.
See [Handlers.http](../../src/Queue.Administration/Handlers.http) for requests.

A group endpoint reports the responding worker's configuration. It does not verify consistency across replicas.
Deploy matching registrations to replicas that consume the same queue.

## Console behavior

The Registered handlers panel shows the selected group's declarations.
Expand a handler card to add test items directly to its queue group.
Generic handlers offer item count, payload, and media type. Payloads start from the declared example.
Email handlers offer total emails, recipients per queue item, subject, and body.
Both email cards generate unique `example.test` recipients. Protected batches also receive fresh operation IDs.
Queue options expose metadata, maximum attempts, and initial delay for every card.
Each form has independent progress, errors, and cancellation.

The console submits at most 100 envelopes per request through `POST /api/{provider}/groups/{group}/enqueue/batch`.
It also bounds request sizes. The worker applies its existing validation and provider batch semantics.
The browser keeps only the current batch in memory. Keep the tab open until submission finishes.
Cancellation waits for the current request, then stops further submissions.
Changing groups or refreshing the catalogue also stops subsequent requests. Pending requests retain their original target.
Failed or unconfirmed requests are not retried automatically. The last request may have saved items.
Progress counts confirmed queue insertions, not handler completion or email delivery.

The enqueue dialog loads descriptions, duplicate-handling notes, schemas, and examples from that catalogue.
The console also permits manually entered commands. Unlisted commands show a warning.
The email generator requires a registered `email.batch.v1` command before enabling submission.

The browser fetches metadata when the selected group or connected worker identities change.
Routine telemetry snapshots do not refetch the catalogue.
Refresh handlers requests a new copy. Failed requests clear stale declarations.
Late responses from a previously selected group are ignored.
Metadata and example payloads render as text. Schema references are not fetched or executed.

## Optional typed JSON handlers

```csharp
public sealed record NotifyRequest([property: Required] string Recipient);

public sealed class NotifyHandler : JsonCommandHandler<NotifyRequest>
{
    protected override Task HandleAsync(QueueItem item, NotifyRequest payload, CancellationToken token)
    {
        // Use the validated payload. Keep business operations idempotent.
        return Task.CompletedTask;
    }
}
```

Import `System.ComponentModel.DataAnnotations` for validation attributes.
Register this class through `AddQueueHandler` like any other handler.
The adapter accepts `application/json` and media types ending in `+json`.
It deserializes once and validates root properties and root `IValidatableObject` rules.
Nested object graphs require explicit validation. Descriptor schemas do not replace runtime validation.
Malformed JSON, null payloads, and validation failures become permanent failures with safe messages.
Business exceptions retain normal retry behavior. Cancellation and the original `QueueItem` remain available.

Default JSON options use the shared `JsonSerializerOptions.Web` instance.
Custom options must be frozen during startup before passing them to the protected base constructor.
This preserves serializer metadata caching across handler scopes.
Typed parsing and validation are opt-in. Existing opaque handlers perform no additional parsing.
The [counter example](../../examples/IdempotentHandlers.cs) combines this adapter with transactional idempotency.

## Optional startup discovery

```csharp
[QueueHandler("notify.v1", "Notify", "Sends a notification.",
    PayloadMediaType = "application/json", DuplicateHandling = DuplicateHandling.Unspecified)]
public sealed class NotifyHandler : JsonCommandHandler<NotifyRequest>
{
    protected override Task HandleAsync(QueueItem item, NotifyRequest payload, CancellationToken token)
        => Task.CompletedTask;
}

services.AddQueueHandlersFromAssembly(typeof(NotifyHandler).Assembly);
```

Only attributed handlers in the explicitly supplied assembly are registered.
Unattributed types are ignored. Handlers are not instantiated during discovery.
Repeated discovery and duplicate command names are errors. Do not mix explicit and discovered registration for the same command.
Reflection runs during registration, never during dispatch. No plugin directories are searched or assemblies loaded from disk.
Prefer explicit registration for trimmed deployments. Discovery does not provide trimming preservation rules.

## Verification

```bash
dotnet test src/Queue.Tests
python3 src/Queue.SystemTests/handler-catalogue-checks.py
```

The browser checks require `playwright-cli` and an installed browser.
They create temporary SQLite databases and local workers, then remove them.
They verify API scoping, authentication, OpenAPI, dynamic examples, safe text, delayed responses, unavailable workers, and mobile layout.
Generated emails remain queued; these checks do not contact an SMTP server.
