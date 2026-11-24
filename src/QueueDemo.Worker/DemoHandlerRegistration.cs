using Besitos.Worker;

namespace Besitos.Demo.Worker;

public static class DemoHandlerRegistration
{
    public static IServiceCollection AddDemoHandlers(this IServiceCollection services) => services
        .AddQueueHandler<EchoHandler>(new HandlerDescriptor("demo.echo.v1", "Success", "Completes immediately without processing the payload. Used for throughput tests.")
        {
            ExamplePayload = "Hello, durable world.", DuplicateHandling = DuplicateHandling.NaturallyIdempotent,
            DuplicateHandlingNotes = "This handler has no business side effects."
        })
        .AddQueueHandler<DelayHandler>(new HandlerDescriptor("demo.delay.v1", "45s · renewal", "Waits before completing. Metadata controls the delay; the default is 45 seconds.")
        {
            ExamplePayload = "Test lease renewal.", ExampleMetadata = "{\"milliseconds\":45000}",
            DuplicateHandling = DuplicateHandling.NaturallyIdempotent, DuplicateHandlingNotes = "A retry waits again without changing business data."
        })
        .AddQueueHandler<TransientHandler>(new HandlerDescriptor("demo.transient.v1", "Retry twice", "Fails the first two attempts by default, then succeeds. Metadata can change the failure count.")
        {
            ExamplePayload = "Test retries.", ExampleMetadata = "{\"failFirst\":2}",
            DuplicateHandling = DuplicateHandling.NaturallyIdempotent, DuplicateHandlingNotes = "Failures and completion have no business side effects."
        })
        .AddQueueHandler<PoisonHandler>(new HandlerDescriptor("demo.poison.v1", "Poison", "Always fails permanently. Used to test dead letters and replay.")
        {
            ExamplePayload = "Test dead letters.", DuplicateHandling = DuplicateHandling.NaturallyIdempotent,
            DuplicateHandlingNotes = "Every execution fails without changing business data."
        });
}
