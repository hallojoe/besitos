using Microsoft.Extensions.Configuration;
namespace Besitos.Demo.AppHost;

internal static class MailResources
{
    public static IResourceBuilder<MailPitContainerResource> AddQueueMailpit(this IDistributedApplicationBuilder builder,
        IResourceBuilder<ProjectResource> console)
    {
        var retention = builder.Configuration.GetValue("Mailpit:MaxMessages", 100_000);
        if (retention < 0) throw new ArgumentException("Mailpit retention cannot be negative.");
        var mailpit = builder.AddMailPit("mailpit")
            .WithEnvironment("MP_MAX_MESSAGES", retention.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithExplicitStart();
        console.WithEnvironment("Mailpit__Url", mailpit.GetEndpoint("http"));
        return mailpit;
    }
}
