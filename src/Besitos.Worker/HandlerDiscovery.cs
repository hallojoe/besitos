using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Besitos.Worker;

/// <summary>Opt-in startup discovery metadata. Only explicitly supplied assemblies are scanned.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class QueueHandlerAttribute(string command, string displayName, string description) : Attribute
{
    public string Command { get; } = command;
    public string DisplayName { get; } = displayName;
    public string Description { get; } = description;
    public string PayloadMediaType { get; set; } = "text/plain";
    public string? PayloadSchemaJson { get; set; }
    public string? ExamplePayload { get; set; }
    public string? ExampleMetadata { get; set; }
    public DuplicateHandling DuplicateHandling { get; set; }
    public string? DuplicateHandlingNotes { get; set; }

    internal HandlerDescriptor Descriptor()
    {
        using var schema = PayloadSchemaJson is null ? null : JsonDocument.Parse(PayloadSchemaJson);
        return new HandlerDescriptor(Command, DisplayName, Description)
        {
            PayloadMediaType = PayloadMediaType, PayloadSchema = schema?.RootElement.Clone(),
            ExamplePayload = ExamplePayload, ExampleMetadata = ExampleMetadata,
            DuplicateHandling = DuplicateHandling, DuplicateHandlingNotes = DuplicateHandlingNotes
        }.Snapshot();
    }
}

public static class HandlerDiscovery
{
    /// <summary>Registers attributed handlers once. Does not load files, instantiate handlers, or scan during dispatch.</summary>
    public static IServiceCollection AddQueueHandlersFromAssembly(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var registrations = assembly.GetTypes().OrderBy(type => type.FullName, StringComparer.Ordinal)
            .Select(type => (Type: type, Attribute: type.GetCustomAttribute<QueueHandlerAttribute>()))
            .Where(item => item.Attribute is not null)
            .Select(item => new HandlerRegistration(item.Attribute!.Command, item.Type) { Descriptor = item.Attribute.Descriptor() }).ToArray();
        // Validate the complete batch before changing DI. Duplicate discovery is an error, never last-registration-wins.
        _ = new HandlerCatalogue(services.Where(service => service.ServiceType == typeof(HandlerRegistration))
            .Select(service => service.ImplementationInstance).OfType<HandlerRegistration>().Concat(registrations));
        foreach (var registration in registrations)
        {
            services.AddScoped(registration.HandlerType);
            services.AddSingleton(registration);
        }
        return services.AddHandlerCatalogue();
    }
}
